using System.Text.Json.Nodes;
using Agent.Abstractions;
using Agent.Abstractions.Booking;
using Agent.Abstractions.Sessions;
using Agent.Domains.Flights;
using Agent.Domains.Hotels;
using Agent.Domains.Shared;
using Agent.Domains.Shared.Trips;
using Agent.Domains.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.Support;

/// <summary>A booking backend that records every submission it receives and delegates to the in-memory supplier.</summary>
internal sealed class RecordingBookingBackend(IBookingBackend inner) : IBookingBackend
{
    private readonly List<BookingSubmission> _submissions = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<BookingSubmission> Submissions
    {
        get
        {
            lock (_gate)
            {
                return _submissions.ToList();
            }
        }
    }

    public Task<BookingConfirmation> SubmitAsync(BookingSubmission submission, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _submissions.Add(submission);
        }

        return inner.SubmitAsync(submission, cancellationToken);
    }

    public Task<BookingStatusInfo?> GetStatusAsync(string bookingId, CancellationToken cancellationToken) =>
        inner.GetStatusAsync(bookingId, cancellationToken);
}

/// <summary>Helpers for the transfer and trip tests: a harness with transfers registered and ready-made confirmations.</summary>
internal static class TripScenarios
{
    /// <summary>The domain harness plus the transfer domain (a sibling so tests of the 16 core tools stay untouched).</summary>
    public static Task<DomainHarness> CreateAsync(IDictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null) =>
        DomainHarness.CreateAsync(config, services =>
        {
            services.AddTransferDomain();
            configure?.Invoke(services);
        });

    /// <summary>Like <see cref="CreateAsync"/> but with a recording wrapper around the booking backend.</summary>
    public static async Task<(DomainHarness Harness, RecordingBookingBackend Backend)> CreateRecordingAsync(
        IDictionary<string, string?>? config = null,
        Action<IServiceCollection>? configure = null)
    {
        var harness = await CreateAsync(
            config,
            services =>
            {
                services.AddSingleton<IBookingBackend>(sp => new RecordingBookingBackend(sp.GetRequiredService<InMemoryBookingBackend>()));
                configure?.Invoke(services);
            });
        return (harness, (RecordingBookingBackend)harness.Get<IBookingBackend>());
    }

    public static string TransferSearchJson(string date, string time = "10:30", int? passengers = null, string pickup = "LIS", string dropoff = "Lisbon city centre")
    {
        var input = new JsonObject { ["pickup"] = pickup, ["dropoff"] = dropoff, ["date"] = date, ["time"] = time };
        if (passengers is { } count)
        {
            input["passengers"] = count;
        }

        return input.ToJsonString();
    }

    public static TransferSearchParams TransferParams(string date, string time = "10:30", int passengers = 1, string pickup = "LIS", string dropoff = "Lisbon city centre") =>
        new(pickup, dropoff, DateOnly.ParseExact(date, "yyyy-MM-dd"), TimeOnly.ParseExact(time, "HH:mm"), passengers);

    /// <summary>Searches transfers and selects the first offer matching <paramref name="pick"/> (default: the cheapest).</summary>
    public static async Task<TransferOffer> SelectTransferAsync(
        DomainHarness harness,
        SessionContext session,
        int? passengers = 1,
        Func<TransferOffer, bool>? pick = null,
        int day = 20)
    {
        var search = await harness.RunAsync(session, "search_transfers", TransferSearchJson(Scenarios.DateAfterStart(day), passengers: passengers));
        var offers = DomainHarness.ReadList<TransferOffer>(DomainHarness.Content(search), "offers");
        var offer = pick is null ? offers[0] : offers.First(pick);
        await harness.RunAsync(session, "select_transfer", $$"""{"offerId":"{{offer.Id}}"}""");
        return offer;
    }

    public static async Task AddToTripAsync(DomainHarness harness, SessionContext session, params string[] services)
    {
        foreach (var service in services)
        {
            await harness.RunAsync(session, "add_to_trip", $$"""{"service":"{{service}}"}""");
        }
    }

    /// <summary>Confirms one flight (party of <paramref name="adults"/>), one hotel (2 adults) and one transfer (<paramref name="passengers"/>).</summary>
    public static async Task<(FlightOffer Flight, TransferOffer Transfer)> ConfirmAllAsync(
        DomainHarness harness,
        SessionContext session,
        int adults = 1,
        int passengers = 1,
        Func<FlightOffer, bool>? pickFlight = null)
    {
        var flight = await Scenarios.ConfirmFlightAsync(harness, session, pickFlight ?? (o => !o.RequiresPassport), adults);
        await Scenarios.PrebookFirstRoomAsync(harness, session);
        var transfer = await SelectTransferAsync(harness, session, passengers);
        return (flight, transfer);
    }

    public static Task<BookingContext?> TripContextAsync(DomainHarness harness, SessionContext session) =>
        harness.ContextAsync(session, ServiceType.Trip);

    /// <summary>A valid combined traveller form for <paramref name="travellers"/> distinct people.</summary>
    public static TripFormRequest Form(int travellers, bool passport = true)
    {
        string[] first = ["Jane", "John", "Joan", "Jules", "Jade", "Jack", "Jill", "Jess", "Jory", "Jude", "Jean", "Jett"];
        var request = new TripFormRequest
        {
            Contact = new ContactDetails { Email = "user@example.com", Phone = "+15555550100" },
            SpecialRequests = "Quiet seats please",
        };
        for (var i = 0; i < travellers; i++)
        {
            request.Travellers.Add(new TravellerDetails
            {
                FirstName = first[i],
                LastName = "Doe",
                DateOfBirth = $"{1980 + i}-0{1 + (i % 9)}-1{i % 9}",
                Gender = i % 2 == 0 ? "female" : "male",
                Nationality = "NL",
                PassportNumber = passport ? $"P12345{i:D2}" : null,
                PassportExpiry = passport ? "2040-01-01" : null,
            });
        }

        return request;
    }

    public static async Task<FormSubmissionResultView> SubmitFormAsync(DomainHarness harness, SessionContext session, TripFormRequest request)
    {
        var handler = harness.Get<Agent.Abstractions.Forms.IFormSubmissionHandler<TripFormRequest>>();
        var result = await handler.HandleAsync(session.SessionId, request, CancellationToken.None);
        return new FormSubmissionResultView(result.Success, result.Errors, result.PaymentMethods);
    }

    public static string Serialize(object value) => AgentJson.Serialize(value);
}

internal sealed record FormSubmissionResultView(bool Success, IReadOnlyDictionary<string, string[]> Errors, IReadOnlyList<PaymentMethod> PaymentMethods);
