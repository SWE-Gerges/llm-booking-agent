using System.Globalization;
using System.Text.RegularExpressions;
using Agent.Core.Lookups;

namespace Agent.Core.Forms;

/// <summary>Collects per-field validation messages for server-side forms.</summary>
public sealed partial class FieldErrors(CountryCatalog countries, TimeProvider clock)
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            list = [];
            _errors[field] = list;
        }

        list.Add(message);
    }

    public string? RequiredText(string field, string? value, int maxLength = 100)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Add(field, "This field is required.");
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            Add(field, $"Must be at most {maxLength} characters.");
            return null;
        }

        return trimmed;
    }

    public string? OptionalText(string field, string? value, int maxLength = 500)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            Add(field, $"Must be at most {maxLength} characters.");
            return null;
        }

        return trimmed;
    }

    public string? Email(string field, string? value)
    {
        var trimmed = RequiredText(field, value, 200);
        if (trimmed is not null && !EmailShape().IsMatch(trimmed))
        {
            Add(field, "Enter a valid e-mail address.");
            return null;
        }

        return trimmed;
    }

    public string? Phone(string field, string? value)
    {
        var trimmed = RequiredText(field, value, 30);
        if (trimmed is null)
        {
            return null;
        }

        var digits = trimmed.Count(char.IsDigit);
        if (!PhoneShape().IsMatch(trimmed) || digits is < 7 or > 15)
        {
            Add(field, "Enter a valid phone number with 7 to 15 digits.");
            return null;
        }

        return trimmed;
    }

    public string? CountryCode(string field, string? value, bool required = true)
    {
        var trimmed = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (required)
            {
                Add(field, "This field is required.");
            }

            return null;
        }

        if (!countries.IsValid(trimmed))
        {
            Add(field, "Enter a valid two-letter country code.");
            return null;
        }

        return trimmed;
    }

    /// <summary>Parses an ISO date. <paramref name="mustBePast"/> / <paramref name="mustBeFuture"/> compare against the clock.</summary>
    public string? Date(string field, string? value, bool required = true, bool mustBePast = false, bool mustBeFuture = false)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (required)
            {
                Add(field, "This field is required.");
            }

            return null;
        }

        if (!DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Add(field, "Use the format yyyy-MM-dd.");
            return null;
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (mustBePast && date >= today)
        {
            Add(field, "The date must be in the past.");
            return null;
        }

        if (mustBeFuture && date <= today)
        {
            Add(field, "The date must be in the future.");
            return null;
        }

        return trimmed;
    }

    public string? OneOf(string field, string? value, params string[] allowed)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || !allowed.Contains(trimmed, StringComparer.Ordinal))
        {
            Add(field, $"Choose one of: {string.Join(", ", allowed)}.");
            return null;
        }

        return trimmed;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"^\+?[0-9 ()\-]{7,25}$")]
    private static partial Regex PhoneShape();
}
