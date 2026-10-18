using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleeto.Core.Performance;
using Npgsql;

namespace Fleeto.Infrastructure.Performance;

/// <summary>One node of a plan: what it does and on which table or index.</summary>
public sealed record PerfPlanNode(int Depth, string NodeType, string? Relation, string? Index, double TotalCost, double Rows);

/// <summary>The generic plan of a statement, or why there is none.</summary>
public sealed record PerfPlan(string? Json, IReadOnlyList<PerfPlanNode> Nodes, string? Problem)
{
    public bool Planned => Json is not null;

    /// <summary>Tables the plan reads in full.</summary>
    public IReadOnlyList<string> SequentialScans => Nodes.Where(n => n.NodeType == "Seq Scan" && n.Relation is not null).Select(n => n.Relation!).Distinct().ToList();
}

/// <summary>
/// The plan of a statement the monitor stored (0.7.0 step 2, spec §2.8 and §11): a generic plan only (EXPLAIN without ANALYZE,
/// so nothing runs), in a read-only transaction with a short statement timeout, and only for one SELECT or WITH without data
/// changes. The text is never supplied by a browser: the page sends the id of a statement and the server looks up its own
/// normalized text, whose literals were replaced by <c>?</c>; those become parameters, so the planner sees no values either.
/// </summary>
public static partial class PerformancePlanner
{
    public const int StatementTimeoutMs = 5000;

    // StatementTimeoutMs.
    private const string TransactionSql = "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = 5000";

    private static readonly string[] Forbidden =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "COPY", "CREATE", "ALTER", "DROP", "GRANT", "REVOKE", "CALL", "DO", "LOCK", "VACUUM",
        "ANALYZE", "REFRESH", "NOTIFY", "LISTEN", "UNLISTEN", "SET", "RESET", "EXECUTE", "PREPARE", "DEALLOCATE", "DISCARD", "CHECKPOINT", "REINDEX",
        "CLUSTER", "COMMENT", "SECURITY", "IMPORT", "LOAD", "INTO"
    ];

    /// <summary>
    /// The statement ready for EXPLAIN, or why it cannot be planned. Every <c>?</c> becomes a parameter numbered after the highest
    /// <c>$n</c> it already has. Refused: more than one statement, anything but SELECT or WITH, and any keyword that changes data,
    /// outside quoted names. A locking clause (FOR UPDATE) is allowed: a plan never takes the lock.
    /// </summary>
    public static (string? Sql, string? Problem) Prepare(string? normalized)
    {
        var sql = (normalized ?? string.Empty).Trim().TrimEnd(';').Trim();
        if (sql.Length == 0)
        {
            return (null, "The statement has no text to plan.");
        }

        if (sql.Contains(';', StringComparison.Ordinal))
        {
            return (null, "Only a single statement can be planned.");
        }

        var words = QuotedName().Replace(sql, "\"\"");
        words = LockingClause().Replace(words, " ");
        var first = FirstWord().Match(words);
        if (!first.Success || !(first.Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Value.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, "Only SELECT and WITH statements are planned; this one changes data or the schema.");
        }

        foreach (Match word in Word().Matches(words))
        {
            if (Forbidden.Contains(word.Value, StringComparer.OrdinalIgnoreCase))
            {
                return (null, $"The statement contains {word.Value.ToUpperInvariant()}, so it is not planned: plans are only made of statements that read.");
            }
        }

        var next = Parameter().Matches(sql).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
        var result = new StringBuilder(sql.Length + 16);
        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        var inName = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '"')
            {
                inName = !inName;
            }

            if (inName)
            {
                result.Append(c);
            }
            else if (c == '?')
            {
                result.Append('$').Append(next.ToString(CultureInfo.InvariantCulture));
                next++;
            }
            else if (c == '@' && NamedParameter().Match(sql, i) is { Success: true } parameter && parameter.Index == i)
            {
                // A parameter of raw SQL in the code (@since), which Npgsql numbers before sending; the same name is one parameter.
                if (!named.TryGetValue(parameter.Groups[1].Value, out var number))
                {
                    named[parameter.Groups[1].Value] = number = next++;
                }

                result.Append('$').Append(number.ToString(CultureInfo.InvariantCulture));
                i += parameter.Length - 1;
            }
            else
            {
                result.Append(c);
            }
        }

        return (result.ToString(), null);
    }

    /// <summary>Plans the statement as the role of this process; a statement it may not read is reported, not escalated.</summary>
    public static async Task<PerfPlan> ExplainAsync(NpgsqlDataSource dataSource, string? normalized, CancellationToken cancellationToken)
    {
        var (sql, problem) = Prepare(normalized);
        if (sql is null)
        {
            return new PerfPlan(null, [], problem);
        }

        using var scope = PerfContext.Begin(PerfKeys.DashboardSource);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var setup = new NpgsqlCommand(TransactionSql, connection, transaction))
            {
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            // Through the database function: a generic plan needs its $n unbound, which a statement sent by Npgsql never has.
            await using var explain = new NpgsqlCommand("SELECT fleeto_perf_explain($1)", connection, transaction);
            explain.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sql });
            var json = await explain.ExecuteScalarAsync(cancellationToken) as string;
            await transaction.RollbackAsync(cancellationToken);
            return json is null ? new PerfPlan(null, [], "PostgreSQL returned no plan.") : new PerfPlan(Pretty(json), Nodes(json), null);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return new PerfPlan(null, [], ex.MessageText.Contains("fleeto_perf_explain", StringComparison.Ordinal)
                ? "The database does not let web plan statements yet. Run install.sh for this instance again, which applies the grants."
                : "This statement reads a table the web component may not read, so it cannot be planned from this page.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedFunction && ex.MessageText.Contains("fleeto_perf_explain", StringComparison.Ordinal))
        {
            return new PerfPlan(null, [], "The database has no planning function yet. Run install.sh for this instance again, which runs the migrations.");
        }
        catch (PostgresException ex)
        {
            return new PerfPlan(null, [], $"PostgreSQL could not plan it: {PerfRedactor.Default.Text(ex.MessageText)}");
        }
    }

    /// <summary>The nodes of a plan in JSON, depth first.</summary>
    public static IReadOnlyList<PerfPlanNode> Nodes(string json)
    {
        var nodes = new List<PerfPlanNode>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var root in document.RootElement.EnumerateArray())
            {
                if (root.TryGetProperty("Plan", out var plan))
                {
                    Walk(plan, 0, nodes);
                }
            }
        }
        catch (JsonException)
        {
            // A plan that does not parse has no nodes to show; its text is still there.
        }

        return nodes;
    }

    private static void Walk(JsonElement plan, int depth, List<PerfPlanNode> nodes)
    {
        string? Text(string name) => plan.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        double Number(string name) => plan.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

        nodes.Add(new PerfPlanNode(depth, Text("Node Type") ?? "?", Text("Relation Name"), Text("Index Name"), Number("Total Cost"), Number("Plan Rows")));
        if (nodes.Count < 200 && plan.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Walk(child, depth + 1, nodes);
            }
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    [GeneratedRegex("\"(?:[^\"]|\"\")*\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"\bFOR\s+(?:NO\s+KEY\s+)?(?:UPDATE|SHARE)(?:\s+OF\s+[\w"",\s]+?)?(?:\s+(?:NOWAIT|SKIP\s+LOCKED))?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LockingClause();

    [GeneratedRegex(@"[A-Za-z_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();

    [GeneratedRegex(@"\b[A-Za-z_]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\$(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    // @name not preceded by a word character or another operator character, so @> and <@ stay operators.
    [GeneratedRegex(@"(?<![\w@<>])@([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameter();
}
