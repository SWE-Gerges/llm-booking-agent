using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;
using Agent.Domains.Shared;
using Agent.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.SharedTests;

public class BookingBackendTests
{
    private readonly FakeTimeProvider _clock = new(DomainHarness.Start);

    private InMemoryBookingBackend Backend(int autoConfirmSeconds = 30) =>
        new(TestOptions.Of(new FakeBackendOptions { AutoConfirmAfterSeconds = autoConfirmSeconds }), _clock);

    private static BookingSubmission Submission(string key, decimal total = 100m, ServiceType service = ServiceType.Flight) =>
        new(service, "item-1", null, "qt-1", new Money(total, "EUR"), new Dictionary<string, string>(), key);

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_booking_and_creates_one()
    {
        var backend = Backend();

        var first = await backend.SubmitAsync(Submission("key-1"), CancellationToken.None);
        var second = await backend.SubmitAsync(Submission("key-1"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, backend.CreatedCount);
        Assert.Equal(BookingStatus.PendingPayment, first.Status);
    }

    [Fact]
    public async Task Different_keys_create_different_bookings_with_stable_ids()
    {
        var backend = Backend();

        var a = await backend.SubmitAsync(Submission("key-a"), CancellationToken.None);
        var b = await backend.SubmitAsync(Submission("key-b"), CancellationToken.None);
        var aAgain = await Backend().SubmitAsync(Submission("key-a"), CancellationToken.None);

        Assert.NotEqual(a.BookingId, b.BookingId);
        Assert.Equal(2, backend.CreatedCount);
        Assert.Equal(a.BookingId, aAgain.BookingId);
        Assert.Matches("^BK-[0-9A-F]{10}$", a.BookingId);
        Assert.Equal($"https://pay.example.com/checkout/{a.BookingId}", a.PaymentUrl);
    }

    [Fact]
    public async Task Concurrent_submissions_with_one_key_create_a_single_booking()
    {
        var backend = Backend();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => backend.SubmitAsync(Submission("race"), CancellationToken.None))));

        Assert.Single(results.Select(r => r.BookingId).Distinct());
        Assert.Equal(1, backend.CreatedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_total_is_refused(int total)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Backend().SubmitAsync(Submission("k", total), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_idempotency_key_is_refused(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Backend().SubmitAsync(Submission(key), CancellationToken.None));
    }

    [Fact]
    public async Task Status_moves_from_pending_to_confirmed_after_the_configured_delay()
    {
        var backend = Backend(30);
        var booking = await backend.SubmitAsync(Submission("k"), CancellationToken.None);

        var pending = await backend.GetStatusAsync(booking.BookingId, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(29));
        var stillPending = await backend.GetStatusAsync(booking.BookingId, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var confirmed = await backend.GetStatusAsync(booking.BookingId, CancellationToken.None);

        Assert.Equal(BookingStatus.PendingPayment, pending!.Status);
        Assert.Null(pending.ConfirmedAt);
        Assert.Equal(BookingStatus.PendingPayment, stillPending!.Status);
        Assert.Equal(BookingStatus.Confirmed, confirmed!.Status);
        Assert.Equal(booking.CreatedAt.AddSeconds(30), confirmed.ConfirmedAt);
        Assert.Equal(booking.PaymentUrl, confirmed.PaymentUrl);
        Assert.Equal(new Money(100m, "EUR"), confirmed.Total);
    }

    [Fact]
    public async Task An_unknown_booking_has_no_status()
    {
        Assert.Null(await Backend().GetStatusAsync("BK-UNKNOWN", CancellationToken.None));
    }

    [Fact]
    public async Task A_delay_of_zero_confirms_immediately()
    {
        var backend = Backend(0);
        var booking = await backend.SubmitAsync(Submission("k"), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, (await backend.GetStatusAsync(booking.BookingId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Payment_methods_always_include_card_and_transfer_and_hotels_add_pay_at_property()
    {
        var payments = new InMemoryPaymentBackend();

        var flight = await payments.GetPaymentMethodsAsync(ServiceType.Flight, "EUR", CancellationToken.None);
        var hotel = await payments.GetPaymentMethodsAsync(ServiceType.Hotel, "USD", CancellationToken.None);

        Assert.Equal(["card", "bank_transfer"], flight.Select(m => m.Id));
        Assert.Equal(["card", "bank_transfer", "pay_at_property"], hotel.Select(m => m.Id));
        Assert.Contains("USD", hotel[0].Label, StringComparison.Ordinal);
    }
}
