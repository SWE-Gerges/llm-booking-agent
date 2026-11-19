using System.Text.RegularExpressions;

namespace Agent.Core.Guards;

public enum SensitiveDataKind
{
    CardNumber,
    Iban,
    AccountNumber,
}

/// <summary>
/// Finds payment data in free text. Returns only the kind of data found, never the value, so callers cannot leak it
/// into logs. Phone and passport numbers are deliberately not treated as sensitive here.
/// </summary>
public static partial class SensitiveDataDetector
{
    private static readonly IReadOnlyDictionary<string, int> IbanLengths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16, ["BG"] = 22,
        ["BH"] = 22, ["BR"] = 29, ["BY"] = 28, ["CH"] = 21, ["CR"] = 22, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22,
        ["DK"] = 18, ["DO"] = 28, ["EE"] = 20, ["EG"] = 29, ["ES"] = 24, ["FI"] = 18, ["FO"] = 18, ["FR"] = 27,
        ["GB"] = 22, ["GE"] = 22, ["GI"] = 23, ["GL"] = 18, ["GR"] = 27, ["GT"] = 28, ["HR"] = 21, ["HU"] = 28,
        ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23, ["IS"] = 26, ["IT"] = 27, ["JO"] = 30, ["KZ"] = 20, ["LB"] = 28,
        ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22, ["MK"] = 19,
        ["MT"] = 31, ["MU"] = 30, ["NL"] = 18, ["NO"] = 15, ["PK"] = 24, ["PL"] = 28, ["PS"] = 29, ["PT"] = 25,
        ["QA"] = 29, ["RO"] = 24, ["RS"] = 22, ["SA"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27,
        ["TN"] = 24, ["TR"] = 26, ["UA"] = 29, ["VA"] = 22, ["XK"] = 20,
    };

    public static SensitiveDataKind? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeDigits(text);
        var chains = FindDigitChains(normalized);

        if (ContainsCardNumber(normalized, chains))
        {
            return SensitiveDataKind.CardNumber;
        }

        if (ContainsIban(normalized))
        {
            return SensitiveDataKind.Iban;
        }

        return ContainsKeywordedNumber(normalized, chains) ? SensitiveDataKind.AccountNumber : null;
    }

    internal static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    internal static bool HasKnownIssuerPrefix(string digits)
    {
        var length = digits.Length;
        int Prefix(int n) => int.Parse(digits.AsSpan(0, n), System.Globalization.CultureInfo.InvariantCulture);

        if (digits[0] == '4')
        {
            return length is 13 or 16 or 19;
        }

        var two = Prefix(2);
        if (length == 16 && (two is >= 51 and <= 55 || Prefix(4) is >= 2221 and <= 2720))
        {
            return true;
        }

        if (length == 15 && two is 34 or 37)
        {
            return true;
        }

        if (length is >= 16 and <= 19 && (Prefix(4) == 6011 || two == 65 || Prefix(3) is >= 644 and <= 649 || two == 62 || Prefix(4) is >= 3528 and <= 3589))
        {
            return true;
        }

        if (length is >= 14 and <= 19 && (Prefix(3) is >= 300 and <= 305 || two is 36 or 38 or 39))
        {
            return true;
        }

        return length is >= 13 and <= 19 && (two is 50 or 67 || two is >= 56 and <= 58);
    }

    internal static bool IsValidIban(string compact)
    {
        if (compact.Length < 15 || !IbanLengths.TryGetValue(compact[..2], out var expected) || compact.Length < expected)
        {
            return false;
        }

        var candidate = compact[..expected];
        var remainder = 0;
        foreach (var c in string.Concat(candidate.AsSpan(4), candidate.AsSpan(0, 4)))
        {
            if (c is >= '0' and <= '9')
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    private static bool ContainsCardNumber(string text, IReadOnlyList<DigitChain> chains)
    {
        foreach (var chain in chains)
        {
            for (var i = 0; i < chain.Groups.Count; i++)
            {
                if (chain.Groups[i].Index > 0 && text[chain.Groups[i].Index - 1] == '+')
                {
                    continue;
                }

                var joined = string.Empty;
                for (var j = i; j < chain.Groups.Count; j++)
                {
                    joined += chain.Groups[j].Value;
                    if (joined.Length > 19)
                    {
                        break;
                    }

                    if (joined.Length >= 13 && HasKnownIssuerPrefix(joined) && PassesLuhn(joined))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsIban(string text)
    {
        foreach (Match match in IbanCandidate().Matches(text))
        {
            var compact = match.Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (IsValidIban(compact))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsKeywordedNumber(string text, IReadOnlyList<DigitChain> chains)
    {
        var keywords = FinancialKeyword().Matches(text);
        if (keywords.Count == 0)
        {
            return false;
        }

        foreach (var chain in chains)
        {
            var candidates = new List<(int Start, int End, int Digits)>
            {
                (chain.Start, chain.End, chain.Groups.Sum(g => g.Length)),
            };
            candidates.AddRange(chain.Groups.Select(g => (g.Index, g.Index + g.Length, g.Length)));

            foreach (var (start, end, digits) in candidates)
            {
                if (digits is < 8 or > 34 || IsoDate().IsMatch(text.AsSpan(start, end - start)))
                {
                    continue;
                }

                foreach (Match keyword in keywords)
                {
                    var keywordEnd = keyword.Index + keyword.Length;
                    var before = start - keywordEnd;
                    var after = keyword.Index - end;
                    if (before is >= 0 and <= 30 || after is >= 0 and <= 15)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static List<DigitChain> FindDigitChains(string text)
    {
        var chains = new List<DigitChain>();
        DigitChain? current = null;
        foreach (Match match in DigitRun().Matches(text))
        {
            var group = new DigitGroup(match.Index, match.Length, match.Value);
            if (current is not null)
            {
                var last = current.Groups[^1];
                var gap = group.Index - (last.Index + last.Length);
                if (gap == 1 && text[last.Index + last.Length] is ' ' or '-')
                {
                    current.Groups.Add(group);
                    continue;
                }

                chains.Add(current);
            }

            current = new DigitChain([group]);
        }

        if (current is not null)
        {
            chains.Add(current);
        }

        return chains;
    }

    private static string NormalizeDigits(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '٠' and <= '٩')
            {
                chars[i] = (char)('0' + (c - '٠'));
            }
            else if (c is >= '۰' and <= '۹')
            {
                chars[i] = (char)('0' + (c - '۰'));
            }
        }

        return new string(chars);
    }

    private sealed record DigitGroup(int Index, int Length, string Value);

    private sealed class DigitChain(List<DigitGroup> groups)
    {
        public List<DigitGroup> Groups { get; } = groups;

        public int Start => Groups[0].Index;

        public int End => Groups[^1].Index + Groups[^1].Length;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2}\d{2}(?:[ ]?[A-Za-z0-9]){11,30}")]
    private static partial Regex IbanCandidate();

    [GeneratedRegex(
        @"\b(?:card|credit|debit|cvv|cvc|iban|account|acct|routing|sort\s*code|visa|mastercard)\b|بطاقة|البطاقة|بطاقتي|كارت|الكارت|حساب|الحساب|حسابي|ايبان|آيبان|الايبان|الآيبان|فيزا|ماستر",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FinancialKeyword();
}
