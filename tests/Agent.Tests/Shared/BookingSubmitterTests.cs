using Agent.Abstractions.Booking;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Sessions;
using Agent.Domains.Shared;
using Agent.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.SharedTests;

/// <summary>Fails the first <see cref="FailuresRemaining"/> submissions, recording the idempotency keys it was given.</summary>
internal sealed class FlakyBookingBackend(IBookingBackend inner) : IBookingBackend
{
    public int FailuresRemaining { get; set; }

    public List<string> Keys { get; } = [];

    public Task<BookingConfirmation> SubmitAsync(BookingSubmission submission, CancellationToken cancellationToken)
    {
        Keys.Add(submission.IdempotencyKey);
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new HttpRequestException("supplier unreachable");
        }

        return inner.SubmitAsync(submission, cancellationToken);
    }

    public Task<BookingStatusInfo?> GetStatusAsync(string bookingId, CancellationToken cancellationToken) =>
        inner.GetStatusAsync(bookingId, cancellationToken);
}

public class BookingSubmitterTests
{
    [Fact]
    public async Task Submitting_without_any_selection_is_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<BookingSubmitter>().SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None));

        Assert.Contains("Nothing has been selected", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task A_missing_selection_or_unconfirmed_price_is_refused(bool selected, bool priced, bool form)
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, selected: selected, priced: priced, form: form);

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<BookingSubmitter>().SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None));

        Assert.Contains("price", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task Missing_traveller_details_are_refused()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel, form: false);

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => h.Get<BookingSubmitter>().SubmitAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None));

        Assert.Contains("details", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task A_submission_books_at_the_server_side_confirmed_price()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, total: 777.77m);

        var result = await h.Get<BookingSubmitter>().SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);

        Assert.False(result.Replayed);
        Assert.Equal(new Money(777.77m, "EUR"), result.Confirmation.Total);
        Assert.Equal(ServiceType.Flight, result.Confirmation.Service);
        var context = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Equal(result.Confirmation.BookingId, context!.BookingId);
        Assert.False(string.IsNullOrEmpty(context.IdempotencyKey));
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
        var audit = Assert.Single(await BookingSeed.AuditRowsAsync(h, "booking.submitted"));
        Assert.Equal(s.SessionId, audit.SessionToken);
        Assert.Contains(result.Confirmation.BookingId, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submitting_twice_creates_one_booking_and_flags_the_replay()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel);
        var submitter = h.Get<BookingSubmitter>();

        var first = await submitter.SubmitAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None);
        var second = await submitter.SubmitAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Confirmation.BookingId, second.Confirmation.BookingId);
        Assert.Equal(first.Confirmation.PaymentUrl, second.Confirmation.PaymentUrl);
        Assert.Equal(first.Confirmation.Total, second.Confirmation.Total);
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
        Assert.Single(await BookingSeed.AuditRowsAsync(h, "booking.submitted"));
    }

    [Fact]
    public async Task Concurrent_submissions_create_exactly_one_booking()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight);
        var submitter = h.Get<BookingSubmitter>();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => submitter.SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None))));

        Assert.Single(results.Select(r => r.Confirmation.BookingId).Distinct());
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
        var context = await h.ContextAsync(s, ServiceType.Flight);
        Assert.Equal(results[0].Confirmation.BookingId, context!.BookingId);
        Assert.False(string.IsNullOrEmpty(context.IdempotencyKey));
    }

    [Fact]
    public async Task Flight_and_hotel_bookings_are_independent()
    {
        using var h = await DomainHarness.CreateAsync();
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight, total: 100m);
        await BookingSeed.SeedAsync(h, s, ServiceType.Hotel, total: 200m);
        var submitter = h.Get<BookingSubmitter>();

        var flight = await submitter.SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);
        var hotel = await submitter.SubmitAsync(s.SessionId, ServiceType.Hotel, CancellationToken.None);

        Assert.NotEqual(flight.Confirmation.BookingId, hotel.Confirmation.BookingId);
        Assert.Equal(100m, flight.Confirmation.Total.Amount);
        Assert.Equal(200m, hotel.Confirmation.Total.Amount);
        Assert.Equal(2, h.Get<InMemoryBookingBackend>().CreatedCount);
    }

    [Fact]
    public async Task A_failed_supplier_call_keeps_the_idempotency_key_so_the_retry_cannot_double_book()
    {
        FlakyBookingBackend? flaky = null;
        using var h = await DomainHarness.CreateAsync(configure: services =>
            services.AddSingleton<IBookingBackend>(sp => flaky = new FlakyBookingBackend(sp.GetRequiredService<InMemoryBookingBackend>()) { FailuresRemaining = 1 }));
        var s = await h.NewSessionAsync();
        await BookingSeed.SeedAsync(h, s, ServiceType.Flight);
        var submitter = h.Get<BookingSubmitter>();

        await Assert.ThrowsAsync<HttpRequestException>(() => submitter.SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None));
        var failedContext = await h.ContextAsync(s, ServiceType.Flight);
        var retry = await submitter.SubmitAsync(s.SessionId, ServiceType.Flight, CancellationToken.None);

        Assert.Null(failedContext!.BookingId);
        Assert.False(string.IsNullOrEmpty(failedContext.IdempotencyKey));
        Assert.Equal(2, flaky!.Keys.Count);
        Assert.Equal(flaky.Keys[0], flaky.Keys[1]);
        Assert.Equal(failedContext.IdempotencyKey, flaky.Keys[1]);
        Assert.Equal(1, h.Get<InMemoryBookingBackend>().CreatedCount);
        Assert.False(retry.Replayed);
    }
}
