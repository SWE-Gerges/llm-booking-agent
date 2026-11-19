using Agent.Abstractions.Forms;
using Agent.Abstractions.Sessions;
using Agent.Domains.Flights;
using Agent.Domains.Flights.Forms;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.FlightTests;

public class PassengerFormHandlerTests
{
    private const string Passport = "P1234567";

    private static PassengerFormRequest Valid(int passengers = 1, bool withPassport = false) => new()
    {
        Passengers = Enumerable.Range(0, passengers).Select(i => new PassengerDetails
        {
            FirstName = i == 0 ? "Jane" : "Janet",
            LastName = i == 0 ? "Doe" : "Roe",
            DateOfBirth = "1990-05-01",
            Gender = "female",
            Nationality = "NL",
            PassportNumber = withPassport ? Passport : null,
            PassportExpiry = withPassport ? "2031-01-01" : null,
        }).ToList(),
        Contact = new Agent.Domains.Shared.ContactDetails { Email = "user@example.com", Phone = "+15555550100" },
    };

    private static IFormSubmissionHandler<PassengerFormRequest> Handler(DomainHarness h) =>
        h.Get<IFormSubmissionHandler<PassengerFormRequest>>();

    private static async Task<FormSubmissionResult> SubmitAsync(DomainHarness h, SessionContext s, PassengerFormRequest request) =>
        await Handler(h).HandleAsync(s.SessionId, request, CancellationToken.None);

    [Fact]
    public async Task Submitting_without_a_selected_flight_is_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await SubmitAsync(h, s, Valid());

        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("form"));
        Assert.Null((await h.ContextAsync(s, ServiceType.Flight))?.Form);
    }

    [Fact]
    public async Task Selecting_without_a_confirmed_price_is_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await h.RunAsync(s, "search_flights", Scenarios.FlightSearchJson(Scenarios.DateAfterStart(10), 1));

        var result = await SubmitAsync(h, s, Valid());

        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("form"));
    }

    [Fact]
    public async Task The_passenger_count_must_equal_the_searched_adults()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport, adults: 2);

        var result = await SubmitAsync(h, s, Valid(1));

        Assert.False(result.Success);
        Assert.Contains("2", Assert.Single(result.Errors["passengers"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_form_is_stored_and_answers_with_payment_methods()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport, adults: 2);

        var result = await SubmitAsync(h, s, Valid(2));

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal("FormAccepted", result.NextStepMessageKey);
        Assert.Contains(result.PaymentMethods, m => m.Id == "card");
        Assert.Contains(result.PaymentMethods, m => m.Id == "bank_transfer");
        var form = (await h.ContextAsync(s, ServiceType.Flight))!.Form!;
        Assert.Equal(h.Clock.GetUtcNow(), form.SubmittedAt);
        Assert.Equal("Jane", form.Raw["passengers[0].firstName"]);
        Assert.Equal("Roe", form.Raw["passengers[1].lastName"]);
        Assert.Equal("user@example.com", form.Raw["contact.email"]);
        Assert.Equal("+15555550100", form.Raw["contact.phone"]);
    }

    [Fact]
    public async Task The_masked_copy_contains_none_of_the_raw_personal_values()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => o.RequiresPassport);

        var result = await SubmitAsync(h, s, Valid(withPassport: true));

        Assert.True(result.Success);
        var form = (await h.ContextAsync(s, ServiceType.Flight))!.Form!;
        Assert.Equal(form.Raw.Keys.Order(), form.Masked.Keys.Order());
        foreach (var secret in new[] { "Jane", "Doe", "user@example.com", "+15555550100", Passport, "1990-05-01" })
        {
            Assert.DoesNotContain(form.Masked.Values, v => v.Contains(secret, StringComparison.Ordinal));
        }

        Assert.Equal("J***", form.Masked["passengers[0].firstName"]);
        Assert.Equal("D***", form.Masked["passengers[0].lastName"]);
        Assert.StartsWith("u***@", form.Masked["contact.email"], StringComparison.Ordinal);
        Assert.Equal("***", form.Masked["passengers[0].passportNumber"]);
        Assert.Equal("NL", form.Masked["passengers[0].nationality"]);
        Assert.Equal("female", form.Masked["passengers[0].gender"]);
    }

    [Fact]
    public async Task Invalid_fields_are_reported_per_field_and_nothing_is_stored()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        var request = Valid();
        request.Passengers[0].FirstName = " ";
        request.Passengers[0].DateOfBirth = "2999-01-01";
        request.Passengers[0].Gender = "robot";
        request.Passengers[0].Nationality = "XX1";
        request.Contact.Email = "not-an-email";
        request.Contact.Phone = "abc";

        var result = await SubmitAsync(h, s, request);

        Assert.False(result.Success);
        foreach (var field in new[]
                 {
                     "passengers[0].firstName", "passengers[0].dateOfBirth", "passengers[0].gender",
                     "passengers[0].nationality", "contact.email", "contact.phone",
                 })
        {
            Assert.True(result.Errors.ContainsKey(field), $"expected an error for {field}");
        }

        Assert.False(result.Errors.ContainsKey("passengers[0].lastName"));
        Assert.Null((await h.ContextAsync(s, ServiceType.Flight))!.Form);
    }

    [Theory]
    [InlineData("01/05/1990")]
    [InlineData("1990-02-30")]
    [InlineData("")]
    public async Task Birth_dates_must_be_valid_iso_dates(string value)
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        var request = Valid();
        request.Passengers[0].DateOfBirth = value;

        var result = await SubmitAsync(h, s, request);

        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("passengers[0].dateOfBirth"));
    }

    [Fact]
    public async Task Passport_details_are_required_when_the_supplier_demands_them()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => o.RequiresPassport);

        var result = await SubmitAsync(h, s, Valid());

        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("passengers[0].passportNumber"));
        Assert.True(result.Errors.ContainsKey("passengers[0].passportExpiry"));
    }

    [Fact]
    public async Task An_expired_passport_is_rejected()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => o.RequiresPassport);
        var request = Valid(withPassport: true);
        request.Passengers[0].PassportExpiry = "2020-01-01";

        var result = await SubmitAsync(h, s, request);

        Assert.False(result.Success);
        Assert.Contains("future", Assert.Single(result.Errors["passengers[0].passportExpiry"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passport_details_are_optional_otherwise_but_still_validated_when_given()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);

        var omitted = await SubmitAsync(h, s, Valid());
        var malformed = Valid(withPassport: true);
        malformed.Passengers[0].PassportExpiry = "01/01/2031";
        var rejected = await SubmitAsync(h, s, malformed);
        var provided = await SubmitAsync(h, s, Valid(withPassport: true));

        Assert.True(omitted.Success);
        Assert.False(rejected.Success);
        Assert.True(rejected.Errors.ContainsKey("passengers[0].passportExpiry"));
        Assert.True(provided.Success);
        Assert.Equal(Passport, (await h.ContextAsync(s, ServiceType.Flight))!.Form!.Raw["passengers[0].passportNumber"]);
    }

    [Fact]
    public async Task Submitting_again_replaces_the_stored_form()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await Scenarios.ConfirmFlightAsync(h, s, o => !o.RequiresPassport);
        await SubmitAsync(h, s, Valid());
        var changed = Valid();
        changed.Passengers[0].FirstName = "Joan";

        var result = await SubmitAsync(h, s, changed);

        Assert.True(result.Success);
        Assert.Equal("Joan", (await h.ContextAsync(s, ServiceType.Flight))!.Form!.Raw["passengers[0].firstName"]);
    }
}
