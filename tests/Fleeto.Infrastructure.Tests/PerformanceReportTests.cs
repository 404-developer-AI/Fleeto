using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The export for Claude Code (0.7.0, PERFORMANCE.md §9): sections in the order of the spec, instructions for this repository first,
/// limitations always last, everything redacted, and never above 100 KB.
/// </summary>
public sealed class PerformanceReportTests
{
    private static readonly DateTime From = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

    private static PerfSeries Series(PerfKind kind, string key, string subKey, int count, double ms)
    {
        var histogram = new int[PerfHistogram.BucketCount];
        histogram[PerfHistogram.BucketOf(ms)] = count;
        return new PerfSeries("web", kind, key, subKey, count, 0, ms * count, ms, PerfHistogram.Trim(histogram), 0, count, ms * count / 2, 0, count);
    }

    private static PerformanceDataset Data(IReadOnlyList<PerfSeries> series, IReadOnlyList<PerfStatementDelta>? statements = null,
        IReadOnlyList<string>? missing = null) =>
        new(From, From.AddHours(1), PerformanceSettings.Default, series, [], null, statements ?? [], null, null, [], [],
            [new PerfMarkerView(From.AddMinutes(5), "Deploy", "Fleeto 0.7.0")], missing ?? ["Diagnose did not run."]);

    private static string Report(PerformanceDataset data, PerfRedactor? redactor = null)
    {
        var findings = PerformanceRules.Evaluate(data);
        return PerformanceReport.Markdown(data, findings, PerformanceRules.Score(data, findings), From.AddHours(1), "0.7.0-alpha.1",
            redactor ?? PerfRedactor.Default);
    }

    [Fact]
    public void The_report_starts_with_the_instructions_and_ends_with_the_limitations()
    {
        var report = Report(Data([Series(PerfKind.Interaction, "/clients/{ClientId:guid}", "load", 100, 2_500)]));
        var order = new[]
        {
            "# Fleeto performance report", "## Instructions for Claude Code", "## Environment", "## Summary per category", "## Top bottlenecks",
            "## Page interactions", "## Markers", "## Missing data / limitations"
        }.Select(h => report.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains("src/Fleeto.Web/Components/Pages", report);
        Assert.Contains("WorkerLoop", report);
        Assert.Contains("DatabaseGrants", report);
        Assert.Contains("`/clients/{ClientId:guid}`", report);
        Assert.Contains("- Diagnose did not run.", report);
        Assert.EndsWith("- Diagnose did not run.\n\n", report);
        Assert.Contains("0.7.0-alpha.1", report);
    }

    [Fact]
    public void Statements_and_names_in_the_report_are_redacted()
    {
        var redactor = PerfRedactor.ForInstance(["ACME"], ["srv-dc01"]);
        var report = Report(Data(
            [Series(PerfKind.External, "hooks.acme.example", "2xx", 10, 10)],
            [new PerfStatementDelta(1, "SELECT * FROM \"Clients\" WHERE \"Code\" = 'ACME' AND \"Hostname\" = 'srv-dc01'", 100, 50_000, 1, 0, 0)]), redactor);
        Assert.DoesNotContain("ACME", report);
        Assert.DoesNotContain("srv-dc01", report);
        Assert.DoesNotContain("hooks.acme.example", report);
        Assert.Contains("WHERE \"Code\" = ? AND \"Hostname\" = ?", report);
    }

    [Fact]
    public void A_report_that_would_be_larger_than_100_kb_is_cut_at_a_section_and_keeps_its_limitations()
    {
        var statements = Enumerable.Range(1, 15)
            .Select(i => new PerfStatementDelta(i, "SELECT " + string.Join(", ", Enumerable.Range(0, 900).Select(c => $"\"Column{c}\"")) + $" FROM t{i}", 10, 10, 1, 0, 0))
            .ToList();
        var series = new[] { PerfKind.Http, PerfKind.Interaction, PerfKind.WorkerRun, PerfKind.AgentMessage, PerfKind.Db, PerfKind.Signing }
            .SelectMany(kind => Enumerable.Range(0, 20).Select(i => Series(kind, string.Concat(Enumerable.Repeat("/page ", 70)) + i, "GET 2xx", 30, 2_000)))
            .ToList();
        var report = Report(Data(series, statements));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report) <= PerformanceReport.MaxBytes);
        Assert.Contains("The report was cut here", report);
        Assert.Contains("## Missing data / limitations", report);
    }
}
