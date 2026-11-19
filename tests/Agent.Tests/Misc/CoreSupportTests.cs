using System.Text.Json;
using Agent.Core.Forms;
using Agent.Core.Language;
using Agent.Core.Lookups;
using Agent.Core.Options;
using Agent.Core.Tools;
using Agent.Tests.Support;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.Misc;

public sealed class LanguageDetectorTests
{
    [Theory]
    [InlineData("hello there", "en", "en")]
    [InlineData("مرحبا بك", "en", "ar")]
    [InlineData("I want to fly to Lisbon", "ar", "en")]
    [InlineData("hello مرحبا بك اليوم", "en", "ar")]
    [InlineData("hello world مرحبا", "ar", "en")]
    [InlineData("12345 !!! 😀", "ar", "ar")]
    [InlineData("12345 !!! 😀", "en", "en")]
    [InlineData("", "en", "en")]
    [InlineData("привет", "ar", "ar")]
    [InlineData("café crème", "ar", "en")]
    public void Script_decides_the_reply_language_and_the_session_default_breaks_ties(string text, string fallback, string expected)
    {
        Assert.Equal(expected, LanguageDetector.Detect(text, fallback));
    }

    [Theory]
    [InlineData("ar", "rtl")]
    [InlineData("en", "ltr")]
    [InlineData("fr", "ltr")]
    public void Direction_follows_the_locale(string locale, string direction)
    {
        Assert.Equal(direction, LanguageDetector.DirectionOf(locale));
    }
}

public sealed class LocalizerTests
{
    private static readonly Localizer Text = new();

    public static TheoryData<MessageKey> Keys => new(Enum.GetValues<MessageKey>());

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_message_exists_in_both_locales(MessageKey key)
    {
        var english = Text.Get(key, "en", "Aria");
        var arabic = Text.Get(key, "ar", "Aria");

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(arabic));
        Assert.NotEqual(english, arabic);
        Assert.Contains(arabic, c => c is >= '؀' and <= 'ۿ');
        Assert.DoesNotContain(english, c => c is >= '؀' and <= 'ۿ');
    }

    [Fact]
    public void Greeting_includes_the_assistant_name()
    {
        Assert.Contains("Nova", Text.Get(MessageKey.Greeting, "en", "Nova"), StringComparison.Ordinal);
        Assert.Contains("Nova", Text.Get(MessageKey.Greeting, "ar", "Nova"), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_locales_fall_back_to_english()
    {
        Assert.Equal(Text.Get(MessageKey.RateLimited, "en"), Text.Get(MessageKey.RateLimited, "fr"));
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Text.Get((MessageKey)999, "en"));
    }
}

public sealed class FieldErrorsTests
{
    private static FieldErrors Create() =>
        new(new CountryCatalog(), new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Required_text_is_trimmed_and_length_checked()
    {
        var errors = Create();

        Assert.Equal("Jane", errors.RequiredText("first", "  Jane "));
        Assert.Null(errors.RequiredText("empty", "   "));
        Assert.Null(errors.RequiredText("missing", null));
        Assert.Null(errors.RequiredText("long", new string('x', 101)));

        var dictionary = errors.ToDictionary();
        Assert.False(dictionary.ContainsKey("first"));
        Assert.Contains("required", dictionary["empty"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required", dictionary["missing"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", dictionary["long"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_text_allows_blank_but_not_too_long()
    {
        var errors = Create();

        Assert.Null(errors.OptionalText("note", null));
        Assert.Null(errors.OptionalText("note", "   "));
        Assert.Equal("quiet room", errors.OptionalText("note", " quiet room "));
        Assert.False(errors.Any);
        Assert.Null(errors.OptionalText("long", new string('x', 501)));
        Assert.True(errors.Any);
    }

    [Theory]
    [InlineData("jane@example.com", true)]
    [InlineData("jane.smith+tag@mail.example.com", true)]
    [InlineData("jane@example", false)]
    [InlineData("jane example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("", false)]
    public void Email_shape(string value, bool valid)
    {
        var errors = Create();

        var result = errors.Email("email", value);

        Assert.Equal(valid, result is not null);
        Assert.Equal(!valid, errors.Any);
    }

    [Theory]
    [InlineData("+15555550100", true)]
    [InlineData("(555) 555-0100", true)]
    [InlineData("+31 6 1234 5678", true)]
    [InlineData("abc", false)]
    [InlineData("123456", false)]
    [InlineData("+1234567890123456", false)]
    [InlineData("", false)]
    public void Phone_shape_and_digit_count(string value, bool valid)
    {
        var errors = Create();

        Assert.Equal(valid, errors.Phone("phone", value) is not null);
        Assert.Equal(!valid, errors.Any);
    }

    [Theory]
    [InlineData("US", "US")]
    [InlineData("nl", "NL")]
    [InlineData(" de ", "DE")]
    public void Country_codes_are_normalised_to_upper_case(string input, string expected)
    {
        var errors = Create();

        Assert.Equal(expected, errors.CountryCode("country", input));
        Assert.False(errors.Any);
    }

    [Theory]
    [InlineData("USA")]
    [InlineData("U1")]
    [InlineData("")]
    public void Malformed_country_codes_are_rejected(string input)
    {
        var errors = Create();

        Assert.Null(errors.CountryCode("country", input));
        Assert.True(errors.Any);
    }

    [Fact]
    public void Unassigned_country_codes_are_rejected_when_the_platform_has_region_data()
    {
        var errors = Create();

        var result = errors.CountryCode("country", "ZZ");

        if (new CountryCatalog().All.Count > 0)
        {
            Assert.Null(result);
            Assert.True(errors.Any);
        }
    }

    [Fact]
    public void A_missing_country_is_an_error_only_when_required()
    {
        var errors = Create();

        Assert.Null(errors.CountryCode("optional", null, required: false));
        Assert.False(errors.Any);
        Assert.Null(errors.CountryCode("required", null));
        Assert.True(errors.Any);
    }

    [Fact]
    public void Dates_are_parsed_strictly_and_compared_against_the_clock()
    {
        var errors = Create();

        Assert.Equal("1990-01-31", errors.Date("dob", "1990-01-31", mustBePast: true));
        Assert.Null(errors.Date("today-past", "2026-03-10", mustBePast: true));
        Assert.Equal("2026-03-11", errors.Date("tomorrow", "2026-03-11", mustBeFuture: true));
        Assert.Null(errors.Date("today-future", "2026-03-10", mustBeFuture: true));
        Assert.Null(errors.Date("format", "10/03/2026"));
        Assert.Null(errors.Date("impossible", "2026-02-30"));
        Assert.Null(errors.Date("blank", " "));
        Assert.Null(errors.Date("optional", null, required: false));

        var dictionary = errors.ToDictionary();
        Assert.Equal(["blank", "format", "impossible", "today-future", "today-past"], dictionary.Keys.Order(StringComparer.Ordinal));
        Assert.False(dictionary.ContainsKey("optional"));
    }

    [Fact]
    public void One_of_accepts_listed_values_case_insensitively()
    {
        var errors = Create();

        Assert.Equal("f", errors.OneOf("gender", "F", "m", "f", "x"));
        Assert.Null(errors.OneOf("gender", "q", "m", "f", "x"));
        Assert.Null(errors.OneOf("gender", null, "m", "f", "x"));
        Assert.Equal(2, errors.ToDictionary()["gender"].Length);
    }

    [Fact]
    public void Messages_for_the_same_field_accumulate()
    {
        var errors = Create();

        errors.Add("email", "first");
        errors.Add("email", "second");

        Assert.Equal(["first", "second"], errors.ToDictionary()["email"]);
    }
}

public sealed class CountryCatalogTests
{
    [Fact]
    public void Countries_are_sorted_by_code_and_have_names()
    {
        var catalog = new CountryCatalog();

        Assert.Equal(catalog.All.Select(c => c.Code).Order(StringComparer.Ordinal), catalog.All.Select(c => c.Code));
        Assert.All(catalog.All, c =>
        {
            Assert.Equal(2, c.Code.Length);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
        Assert.Equal(catalog.All.Count, catalog.All.Select(c => c.Code).Distinct().Count());
    }

    [Fact]
    public void Validity_is_case_sensitive_and_needs_two_letters()
    {
        var catalog = new CountryCatalog();

        Assert.False(catalog.IsValid("us"));
        Assert.False(catalog.IsValid("USA"));
        Assert.False(catalog.IsValid(""));
        Assert.False(catalog.IsValid("U1"));
        Assert.True(catalog.IsValid("US"));
        Assert.True(catalog.IsValid("NL"));
        if (catalog.All.Count > 0)
        {
            Assert.False(catalog.IsValid("ZZ"));
            Assert.True(catalog.All.Count > 100);
        }
    }
}

public sealed class AgentOptionsValidatorTests
{
    private static readonly AgentOptionsValidator Validator = new();

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validator.Validate(null, new AgentOptions()).Succeeded);
    }

    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var options = new AgentOptions();

        Assert.Equal("Aria", options.AssistantName);
        Assert.Equal("EUR", options.Currency);
        Assert.Equal("UTC", options.TimeZone);
        Assert.Equal(20, options.HistoryWindow);
        Assert.Equal(8, options.TrimChunk);
        Assert.Equal(8, options.MaxToolIterations);
        Assert.Equal(TimeSpan.FromMinutes(3), options.TurnTimeout);
        Assert.Equal(2000, options.MaxMessageLength);
        Assert.Equal(1024, options.MaxTokens);
        Assert.Null(options.Temperature);
        Assert.Equal(TimeSpan.FromMinutes(30), options.SearchMaxAge);
    }

    [Fact]
    public void Unsupported_locale_is_rejected()
    {
        var result = Validator.Validate(null, new AgentOptions { DefaultLocale = "fr" });

        Assert.True(result.Failed);
        Assert.Contains("DefaultLocale", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(6, 8)]
    public void Window_must_be_larger_than_the_trim_chunk(int window, int chunk)
    {
        var result = Validator.Validate(null, new AgentOptions { HistoryWindow = window, TrimChunk = chunk });

        Assert.True(result.Failed);
        Assert.Contains("HistoryWindow", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_time_zone_is_rejected()
    {
        var result = Validator.Validate(null, new AgentOptions { TimeZone = "Mars/Olympus_Mons" });

        Assert.True(result.Failed);
        Assert.Contains("TimeZone", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void All_problems_are_reported_together()
    {
        var result = Validator.Validate(null, new AgentOptions { DefaultLocale = "xx", HistoryWindow = 5, TrimChunk = 9, TimeZone = "Nowhere/Land" });

        Assert.Equal(3, result.Failures!.Count());
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("")]
    public void Currency_must_be_an_upper_case_iso_code_by_data_annotations(string currency)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(new AgentOptions { Currency = currency });

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(context.ObjectInstance, context, results, validateAllProperties: true);

        Assert.False(valid);
    }

    [Fact]
    public void Registered_options_validate_on_access_through_dependency_injection()
    {
        using var harness = OrchestratorHarness.Create(config: new Dictionary<string, string?> { ["Agent:DefaultLocale"] = "xx" });

        var error = Assert.Throws<OptionsValidationException>(() => harness.Get<IOptions<AgentOptions>>().Value);

        Assert.Contains("DefaultLocale", error.Message, StringComparison.Ordinal);
    }
}

public sealed class ToolInputValidatorTests
{
    private const string Schema = """
        {"type":"object",
         "properties":{
           "q":{"type":"string","minLength":1},
           "n":{"type":"integer","minimum":1},
           "e":{"type":"string","enum":["a","b"]}},
         "required":["q"],
         "additionalProperties":false}
        """;

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{"q":"x"}""")]
    [InlineData("""{"q":"x","n":2,"e":"a"}""")]
    public void Valid_input_passes(string json)
    {
        var tool = new FakeTool("v", schemaJson: Schema);

        Assert.Null(new ToolInputValidator().Validate(tool, Input(json)));
    }

    [Theory]
    [InlineData("{}", "q")]
    [InlineData("""{"q":5}""", "/q")]
    [InlineData("""{"q":""}""", "/q")]
    [InlineData("""{"q":"x","n":0}""", "/n")]
    [InlineData("""{"q":"x","n":"two"}""", "/n")]
    [InlineData("""{"q":"x","e":"c"}""", "/e")]
    [InlineData("""{"q":"x","zzz":1}""", "zzz")]
    public void Invalid_input_is_described_for_the_model(string json, string mention)
    {
        var tool = new FakeTool("v", schemaJson: Schema);

        var problem = new ToolInputValidator().Validate(tool, Input(json));

        Assert.NotNull(problem);
        Assert.StartsWith("Invalid input for tool 'v'.", problem, StringComparison.Ordinal);
        Assert.Contains(mention, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_object_input_is_rejected()
    {
        var tool = new FakeTool("v", schemaJson: Schema);

        Assert.NotNull(new ToolInputValidator().Validate(tool, Input("[1,2]")));
        Assert.NotNull(new ToolInputValidator().Validate(tool, Input("\"text\"")));
    }

    [Fact]
    public void The_compiled_schema_is_cached_per_tool()
    {
        var tool = new CountingSchemaTool("counted", Schema);
        var validator = new ToolInputValidator();

        validator.Validate(tool, Input("""{"q":"x"}"""));
        validator.Validate(tool, Input("{}"));
        validator.Validate(tool, Input("""{"q":"y"}"""));

        Assert.Equal(1, tool.SchemaReads);
    }

    [Fact]
    public void Very_long_problem_lists_are_capped()
    {
        var names = Enumerable.Range(0, 200).Select(i => $"field_number_{i}").ToList();
        var properties = string.Join(',', names.Select(n => $"\"{n}\":{{\"type\":\"string\"}}"));
        var schema = $$"""{"type":"object","properties":{ {{properties}} },"required":[{{string.Join(',', names.Select(n => $"\"{n}\""))}}],"additionalProperties":false}""";
        var tool = new FakeTool("big", schemaJson: schema);

        var problem = new ToolInputValidator().Validate(tool, Input("{}"));

        Assert.NotNull(problem);
        Assert.True(problem.Length <= 600);
    }
}
