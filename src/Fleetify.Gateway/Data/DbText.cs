namespace Fleetify.Gateway.Data;

/// <summary>
/// Makes agent-supplied text safe to store: PostgreSQL text and jsonb reject NUL characters, and every column has a
/// length limit. Agents are authenticated but not trusted with the database schema.
/// </summary>
internal static class DbText
{
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('\0'))
        {
            value = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        // Do not cut a surrogate pair in half: PostgreSQL rejects invalid UTF-16 after encoding.
        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    public static int ClampToInt(ulong value) => value > int.MaxValue ? int.MaxValue : (int)value;

    public static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
