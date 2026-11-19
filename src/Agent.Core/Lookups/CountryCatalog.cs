using System.Globalization;

namespace Agent.Core.Lookups;

public sealed record Country(string Code, string Name);

/// <summary>
/// ISO 3166 country list derived from the platform's globalisation data, so no country table is maintained here.
/// With invariant globalisation (no ICU) the list is empty and any well-formed two-letter code is accepted.
/// </summary>
public sealed class CountryCatalog
{
    private readonly HashSet<string> _codes;

    public CountryCatalog()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                var code = region.TwoLetterISORegionName;
                if (code.Length == 2 && code.All(char.IsAsciiLetterUpper))
                {
                    found.TryAdd(code, region.EnglishName);
                }
            }
            catch (ArgumentException)
            {
                // Some culture names have no region data; they simply do not contribute a country.
            }
        }

        All = found.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new Country(kv.Key, kv.Value)).ToList();
        _codes = [.. found.Keys];
    }

    public IReadOnlyList<Country> All { get; }

    public bool IsValid(string code) =>
        code.Length == 2 && code.All(char.IsAsciiLetterUpper) && (_codes.Count == 0 || _codes.Contains(code));
}
