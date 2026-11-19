namespace Agent.Core.Guards;

/// <summary>Masks personal data for logs and for the copy of form data that may reach prompts.</summary>
public static class PiiMasker
{
    public static string MaskName(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts.Select(p => string.Concat(p.AsSpan(0, 1), "***")));
    }

    public static string MaskEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : string.Concat(value.AsSpan(0, 1), "***", value.AsSpan(at));
    }

    public static string MaskPhone(string value)
    {
        var digits = value.Count(char.IsDigit);
        if (digits <= 4)
        {
            return "***";
        }

        var seen = 0;
        var chars = value.Select(c =>
        {
            if (!char.IsDigit(c))
            {
                return c;
            }

            seen++;
            return seen <= 2 || seen > digits - 2 ? c : '*';
        });
        return new string(chars.ToArray());
    }

    /// <summary>Chooses a masking style from the field name. Unknown fields that carry identity are fully hidden.</summary>
    public static string MaskField(string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var name = fieldName.ToLowerInvariant();
        if (name.Contains("email", StringComparison.Ordinal))
        {
            return MaskEmail(value);
        }

        if (name.Contains("phone", StringComparison.Ordinal))
        {
            return MaskPhone(value);
        }

        if (name.EndsWith("name", StringComparison.Ordinal))
        {
            return MaskName(value);
        }

        if (name.Contains("passport", StringComparison.Ordinal) || name.Contains("birth", StringComparison.Ordinal))
        {
            return "***";
        }

        return value;
    }

    public static IReadOnlyDictionary<string, string> MaskAll(IReadOnlyDictionary<string, string> raw) =>
        raw.ToDictionary(kv => kv.Key, kv => MaskField(kv.Key, kv.Value), StringComparer.Ordinal);
}
