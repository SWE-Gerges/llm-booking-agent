using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Sessions;
using Agent.Domains.Shared;
using Agent.Domains.Shared.Trips;
using Agent.Tests.Support;

namespace Agent.Tests.TripTests;

/// <summary>The one combined traveller form, traveller de-duplication and the single trip submission.</summary>
public class TripCheckoutTests
{
    /// <summary>A trip of two travellers (the hotel party is two adults) with a confirmed flight, hotel and transfer in the basket.</summary>
    private static async Task<SessionContext> ReadyTripAsync(DomainHarness h, bool passport = false)
    {
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s, adults: 2, passengers: 2, pickFlight: o => o.RequiresPassport == passport);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        return s;
    }

    [Fact]
    public async Task A_valid_form_is_stored_raw_with_a_masked_copy_and_answers_with_payment_methods()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);

        var result = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false));

        Assert.True(result.Success);
        Assert.NotEmpty(result.PaymentMethods);
        var form = (await TripScenarios.TripContextAsync(h, s))!.Form!;
        Assert.Equal("Jane", form.Raw["travellers[0].firstName"]);
        Assert.Equal("user@example.com", form.Raw["contact.email"]);
        Assert.DoesNotContain("user@example.com", form.Masked.Values);
        Assert.DoesNotContain("Jane", form.Masked.Values);
        Assert.Equal("***", form.Masked["specialRequests"]);
    }

    [Fact]
    public async Task The_same_person_entered_twice_is_rejected_whatever_the_letter_case_and_nothing_is_stored()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        var request = TripScenarios.Form(2, passport: false);
        request.Travellers[1].FirstName = request.Travellers[0].FirstName!.ToUpperInvariant();
        request.Travellers[1].LastName = request.Travellers[0].LastName!.ToLowerInvariant();
        request.Travellers[1].DateOfBirth = request.Travellers[0].DateOfBirth;

        var result = await TripScenarios.SubmitFormAsync(h, s, request);

        Assert.False(result.Success);
        var message = Assert.Single(result.Errors["travellers[1]"]);
        Assert.Contains("already entered as traveller 1", message, StringComparison.Ordinal);
        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task Two_different_people_with_the_same_name_are_not_duplicates()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        var request = TripScenarios.Form(2, passport: false);
        request.Travellers[1].FirstName = request.Travellers[0].FirstName;
        request.Travellers[1].LastName = request.Travellers[0].LastName;

        var result = await TripScenarios.SubmitFormAsync(h, s, request);

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task The_number_of_travellers_must_match_the_trip(int submitted)
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);

        var result = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(submitted, passport: false));

        Assert.False(result.Success);
        Assert.Contains("Exactly 2", Assert.Single(result.Errors["travellers"]), StringComparison.Ordinal);
        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task Passport_details_are_required_when_a_flight_in_the_trip_needs_them()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h, passport: true);

        var without = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false));
        var with = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: true));

        Assert.False(without.Success);
        Assert.Contains("travellers[0].passportNumber", without.Errors.Keys);
        Assert.Contains("travellers[1].passportExpiry", without.Errors.Keys);
        Assert.True(with.Success);
    }

    [Fact]
    public async Task Field_errors_are_reported_per_field_and_nothing_is_stored()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        var request = TripScenarios.Form(2, passport: false);
        request.Travellers[0].FirstName = " ";
        request.Travellers[1].Nationality = "ZZ";
        request.Contact.Email = "not-an-email";

        var result = await TripScenarios.SubmitFormAsync(h, s, request);

        Assert.False(result.Success);
        Assert.Contains("travellers[0].firstName", result.Errors.Keys);
        Assert.Contains("travellers[1].nationality", result.Errors.Keys);
        Assert.Contains("contact.email", result.Errors.Keys);
        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
    }

    [Fact]
    public async Task An_empty_trip_refuses_the_form()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await h.NewSessionAsync();

        var result = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(1, passport: false));

        Assert.False(result.Success);
        Assert.Contains("form", result.Errors.Keys);
    }

    [Fact]
    public async Task A_stale_price_refuses_the_form_until_it_is_confirmed_again()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        h.Clock.Advance(TimeSpan.FromMinutes(16));

        var result = await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false));

        Assert.False(result.Success);
        Assert.Contains("Confirm the price again", Assert.Single(result.Errors["form"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_service_after_the_form_means_the_form_must_be_submitted_again_before_checkout()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        Assert.True((await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false))).Success);

        await h.RunAsync(s, "remove_from_trip", """{"service":"transfer"}""");

        Assert.Null((await TripScenarios.TripContextAsync(h, s))!.Form);
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None));
        Assert.Contains("form", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkout_without_the_form_is_refused_and_books_nothing()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None));

        Assert.Contains("trip form", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task One_submission_books_every_service_at_the_server_held_prices_with_the_de_duplicated_manifest()
    {
        var (harness, backend) = await TripScenarios.CreateRecordingAsync();
        using var h = harness;
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s, adults: 2, passengers: 2);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        Assert.True((await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false))).Success);
        var expectedTotal = 0m;
        foreach (var service in new[] { ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer })
        {
            expectedTotal += (await h.ContextAsync(s, service))!.ConfirmedPrice!.Total.Amount;
        }

        var submitted = await h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None);

        Assert.False(submitted.Replayed);
        var submission = Assert.Single(backend.Submissions);
        Assert.Equal(ServiceType.Trip, submission.Service);
        Assert.Equal(expectedTotal, submission.Total.Amount);
        Assert.Equal(expectedTotal, submitted.Confirmation.Total.Amount);
        Assert.Equal(
            [ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer],
            submission.Lines!.Select(l => l.Service));
        Assert.Equal(expectedTotal, submission.Lines!.Sum(l => l.Total.Amount));
        Assert.Equal("Jane", submission.Details["travellers[0].firstName"]);
        Assert.Equal("John", submission.Details["travellers[1].firstName"]);
        Assert.Equal(2, submission.Details.Keys.Count(k => k.EndsWith(".firstName", StringComparison.Ordinal)));
        Assert.Equal((await TripScenarios.TripContextAsync(h, s))!.IdempotencyKey, submission.IdempotencyKey);
        Assert.Equal(32, submission.IdempotencyKey.Length);
    }

    [Fact]
    public async Task A_replayed_checkout_returns_the_same_booking_and_the_supplier_sees_one_submission()
    {
        var (harness, backend) = await TripScenarios.CreateRecordingAsync();
        using var h = harness;
        var s = await h.NewSessionAsync();
        await TripScenarios.ConfirmAllAsync(h, s, adults: 2, passengers: 2);
        await TripScenarios.AddToTripAsync(h, s, "flight", "hotel", "transfer");
        Assert.True((await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false))).Success);
        var submitter = h.Get<TripSubmitter>();

        var first = await submitter.SubmitTripAsync(s.SessionId, CancellationToken.None);
        var second = await submitter.SubmitTripAsync(s.SessionId, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Confirmation.BookingId, second.Confirmation.BookingId);
        Assert.Single(backend.Submissions);
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task Concurrent_checkouts_create_exactly_one_booking()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        Assert.True((await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false))).Success);
        var submitter = h.Get<TripSubmitter>();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => submitter.SubmitTripAsync(s.SessionId, CancellationToken.None)));

        Assert.Single(results.Select(r => r.Confirmation.BookingId).Distinct());
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task The_booking_consumes_the_selections_so_booking_them_again_needs_a_new_confirmation()
    {
        using var h = await TripScenarios.CreateAsync();
        var s = await ReadyTripAsync(h);
        Assert.True((await TripScenarios.SubmitFormAsync(h, s, TripScenarios.Form(2, passport: false))).Success);

        await h.Get<TripSubmitter>().SubmitTripAsync(s.SessionId, CancellationToken.None);

        foreach (var service in new[] { ServiceType.Flight, ServiceType.Hotel, ServiceType.Transfer })
        {
            var context = (await h.ContextAsync(s, service))!;
            Assert.Null(context.SelectedItemId);
            Assert.Null(context.ConfirmedPrice);
        }
    }
}
