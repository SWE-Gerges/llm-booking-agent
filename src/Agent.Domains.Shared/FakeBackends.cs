using System.Security.Cryptography;
using System.Text;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared;

/// <summary>Deterministic payment-method list for demos and tests.</summary>
internal sealed class InMemoryPaymentBackend : IPaymentBackend
{
    public Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(ServiceType service, string currency, CancellationToken cancellationToken)
    {
        var methods = new List<PaymentMethod>
        {
            new("card", "card", $"Credit or debit card ({currency}, secure payment page)"),
            new("bank_transfer", "bank_transfer", "Bank transfer"),
        };
        if (service == ServiceType.Hotel)
        {
            methods.Add(new PaymentMethod("pay_at_property", "pay_at_property", "Pay at the property"));
        }


        return Task.FromResult<IReadOnlyList<PaymentMethod>>(methods);
    }
}

/// <summary>
/// In-memory booking backend. Booking ids derive from the idempotency key, so replaying a submission returns the very
/// same booking, exactly like a well-behaved supplier API.
/// </summary>
internal sealed class InMemoryBookingBackend(IOptions<FakeBackendOptions> options, TimeProvider clock) : IBookingBackend
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BookingConfirmation> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BookingConfirmation> _byId = new(StringComparer.Ordinal);

    /// <summary>Number of distinct bookings created. Lets tests prove that duplicate submissions created only one.</summary>
    public int CreatedCount
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    public Task<BookingConfirmation> SubmitAsync(BookingSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.IdempotencyKey);
        if (submission.Total.Amount <= 0)
        {
            throw new ArgumentException("A booking needs a positive total.", nameof(submission));
        }

        if (submission.Service == ServiceType.Trip
            && (submission.Lines is not { Count: > 0 } lines || lines.Sum(l => l.Total.Amount) != submission.Total.Amount))
        {
            throw new ArgumentException("A trip needs lines whose totals add up to the submitted total.", nameof(submission));
        }

        lock (_gate)
        {
            if (_byKey.TryGetValue(submission.IdempotencyKey, out var existing))
            {
                return Task.FromResult(existing);
            }

            var id = "BK-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(submission.IdempotencyKey)))[..10];
            var created = new BookingConfirmation(
                id,
                submission.Service,
                BookingStatus.PendingPayment,
                submission.Total,
                $"https://pay.example.com/checkout/{id}",
                clock.GetUtcNow());
            _byKey[submission.IdempotencyKey] = created;
            _byId[id] = created;
            return Task.FromResult(created);
        }
    }

    public Task<BookingStatusInfo?> GetStatusAsync(string bookingId, CancellationToken cancellationToken)
    {
        BookingConfirmation? booking;
        lock (_gate)
        {
            _byId.TryGetValue(bookingId, out booking);
        }

        if (booking is null)
        {
            return Task.FromResult<BookingStatusInfo?>(null);
        }

        var confirmAt = booking.CreatedAt.AddSeconds(options.Value.AutoConfirmAfterSeconds);
        var confirmed = clock.GetUtcNow() >= confirmAt;
        return Task.FromResult<BookingStatusInfo?>(new BookingStatusInfo(
            booking.BookingId,
            booking.Service,
            confirmed ? BookingStatus.Confirmed : BookingStatus.PendingPayment,
            booking.Total,
            booking.PaymentUrl,
            booking.CreatedAt,
            confirmed ? confirmAt : null));
    }
}
