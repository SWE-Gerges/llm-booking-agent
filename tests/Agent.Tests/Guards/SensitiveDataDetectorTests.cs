using Agent.Core.Guards;

namespace Agent.Tests.GuardTests;

public sealed class SensitiveDataDetectorTests
{
    /// <summary>Builds a Luhn-valid number of the requested length starting with <paramref name="prefix"/>.</summary>
    private static string LuhnNumber(string prefix, int length)
    {
        var body = prefix.PadRight(length - 1, '7');
        for (var check = 0; check <= 9; check++)
        {
            var candidate = body + check;
            if (SensitiveDataDetector.PassesLuhn(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("unreachable: exactly one check digit satisfies Luhn");
    }

    private static string BreakLuhn(string number) =>
        number[..^1] + (char)('0' + ((number[^1] - '0' + 1) % 10));

    private static string Grouped(string digits, char separator, int size = 4) =>
        string.Join(separator, Enumerable.Range(0, (digits.Length + size - 1) / size).Select(i => digits.Substring(i * size, Math.Min(size, digits.Length - (i * size)))));

    public static TheoryData<string, string> KnownCards => new()
    {
        { "visa", "4111111111111111" },
        { "visa-13", "4222222222222" },
        { "mastercard", "5555555555554444" },
        { "mastercard-2-series", "2221000000000009" },
        { "amex", "378282246310005" },
        { "discover", "6011111111111117" },
        { "diners", "30569309025904" },
        { "jcb", "3530111333300000" },
    };

    [Theory]
    [MemberData(nameof(KnownCards))]
    public void Well_known_test_cards_are_valid_fixtures(string brand, string number)
    {
        Assert.True(SensitiveDataDetector.PassesLuhn(number), brand);
        Assert.True(SensitiveDataDetector.HasKnownIssuerPrefix(number), brand);
    }

    [Theory]
    [MemberData(nameof(KnownCards))]
    public void Card_numbers_are_detected_plain_spaced_and_dashed(string brand, string number)
    {
        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect($"here {number} thanks"));
        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect($"here {Grouped(number, ' ')} thanks"));
        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect($"here {Grouped(number, '-')} thanks"));
        Assert.True(brand.Length > 0);
    }

    [Fact]
    public void Arabic_indic_digits_are_normalised_before_matching()
    {
        var arabicDigits = new string("4111111111111111".Select(c => (char)('٠' + (c - '0'))).ToArray());

        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect($"بطاقتي {arabicDigits}"));
    }

    [Fact]
    public void Card_number_embedded_in_a_longer_run_of_numbers_is_still_found()
    {
        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect("I need 4 seats 4111 1111 1111 1111"));
    }

    [Fact]
    public void Luhn_valid_number_with_an_unknown_issuer_prefix_is_not_a_card()
    {
        var unknown = LuhnNumber("9", 16);

        Assert.True(SensitiveDataDetector.PassesLuhn(unknown));
        Assert.Null(SensitiveDataDetector.Detect($"reference {unknown}"));
    }

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("5555555555554444")]
    [InlineData("378282246310005")]
    public void Issuer_prefix_with_a_failing_checksum_is_not_a_card(string valid)
    {
        Assert.Null(SensitiveDataDetector.Detect($"number {BreakLuhn(valid)}"));
    }

    [Fact]
    public void Phone_numbers_are_allowed_even_when_a_plus_prefixed_number_would_otherwise_look_like_a_card()
    {
        var thirteenDigits = LuhnNumber("4915123456", 13);

        Assert.Null(SensitiveDataDetector.Detect($"call me on +{thirteenDigits}"));
        Assert.Null(SensitiveDataDetector.Detect("call me on +15555550100"));
        Assert.Null(SensitiveDataDetector.Detect("my number is 0100 555 5550"));
        Assert.Equal(SensitiveDataKind.CardNumber, SensitiveDataDetector.Detect($"call me on {thirteenDigits}"));
    }

    [Theory]
    [InlineData("my passport is A12345678")]
    [InlineData("passport number 123456789")]
    [InlineData("departing 2026-03-10 returning 2026-03-17")]
    [InlineData("order ORD-1234567890123 please")]
    [InlineData("I need 12345678 seats")]
    [InlineData("my card ends 1234")]
    [InlineData("hello, how are you?")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ordinary_text_is_allowed(string message)
    {
        Assert.Null(SensitiveDataDetector.Detect(message));
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    [InlineData("DE89370400440532013000")]
    [InlineData("send it to de89370400440532013000 thanks")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("NL91 ABNA 0417 1643 00")]
    public void Valid_ibans_are_detected_spaced_or_compact(string message)
    {
        Assert.Equal(SensitiveDataKind.Iban, SensitiveDataDetector.Detect(message));
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 01")]
    [InlineData("ZZ89 3704 0044 0532 0130 00")]
    [InlineData("AB12 hello there my friend")]
    [InlineData("GB82 WEST 1234 5698")]
    public void Invalid_or_ordinary_look_alikes_are_not_ibans(string message)
    {
        Assert.NotEqual(SensitiveDataKind.Iban, SensitiveDataDetector.Detect(message));
    }

    [Theory]
    [InlineData("my account number is 12345678")]
    [InlineData("account: 1234 5678 9012")]
    [InlineData("12345678901 is my debit account")]
    [InlineData("IBAN 1234567890")]
    [InlineData("cvv and card 12345678")]
    public void Keyword_next_to_a_long_digit_run_is_detected_in_english(string message)
    {
        Assert.Equal(SensitiveDataKind.AccountNumber, SensitiveDataDetector.Detect(message));
    }

    [Theory]
    [InlineData("رقم حسابي ٠١٢٣٤٥٦٧٨٩٠")]
    [InlineData("رقم الحساب 12345678901")]
    [InlineData("بطاقة ائتمان ١٢٣٤٥٦٧٨")]
    public void Keyword_next_to_a_long_digit_run_is_detected_in_arabic(string message)
    {
        Assert.Equal(SensitiveDataKind.AccountNumber, SensitiveDataDetector.Detect(message));
    }

    [Fact]
    public void Digits_far_from_any_keyword_are_allowed()
    {
        var message = "the account was opened a while ago and nothing else matters here at all, anyway my reference is 12345678";

        Assert.Null(SensitiveDataDetector.Detect(message));
    }

    [Fact]
    public void Detection_exposes_only_the_kind_and_never_the_value()
    {
        var method = typeof(SensitiveDataDetector).GetMethod(nameof(SensitiveDataDetector.Detect))!;

        Assert.Equal(typeof(SensitiveDataKind?), method.ReturnType);
        Assert.Equal(["CardNumber", "Iban", "AccountNumber"], Enum.GetNames<SensitiveDataKind>());
    }

    [Theory]
    [InlineData("79927398713", true)]
    [InlineData("79927398710", false)]
    public void Luhn_checksum(string digits, bool expected)
    {
        Assert.Equal(expected, SensitiveDataDetector.PassesLuhn(digits));
    }

    [Theory]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("GB82WEST12345698765433", false)]
    [InlineData("XX82WEST12345698765432", false)]
    [InlineData("GB82", false)]
    public void Iban_mod_97(string compact, bool expected)
    {
        Assert.Equal(expected, SensitiveDataDetector.IsValidIban(compact));
    }
}
