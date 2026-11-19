using Agent.Abstractions.Booking;
using Agent.Abstractions.Forms;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Forms;
using Agent.Core.Guards;
using Agent.Core.Lookups;
using Agent.Core.Options;
using Agent.Domains.Shared;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Flights.Forms;

public sealed class PassengerDetails
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? DateOfBirth { get; set; }

    public string? Gender { get; set; }

    public string? Nationality { get; set; }

    public string? PassportNumber { get; set; }

    public string? PassportExpiry { get; set; }
}

public sealed class PassengerFormRequest
{
    public List<PassengerDetails> Passengers { get; set; } = [];

    public ContactDetails Contact { get; set; } = new();
}

public static class PassengerFormSchema
{
    public const string FormType = "passenger";

    /// <summary>One group per traveller plus a contact group. Passport fields are mandatory only when the supplier requires them.</summary>
    public static FormSchema Build(int passengers, bool passportRequired, string supplier)
    {
        var groups = new List<FormGroup>();
        for (var i = 0; i < passengers; i++)
        {
            groups.Add(new FormGroup(
                $"passenger{i + 1}",
                $"Passenger {i + 1}",
                [
                    new FormField($"passengers[{i}].firstName", "First name", FormFieldType.Text, true),
                    new FormField($"passengers[{i}].lastName", "Last name", FormFieldType.Text, true),
                    new FormField($"passengers[{i}].dateOfBirth", "Date of birth", FormFieldType.Date, true),
                    new FormField(
                        $"passengers[{i}].gender",
                        "Gender",
                        FormFieldType.Select,
                        true,
                        Options: [new FormOption("female", "Female"), new FormOption("male", "Male"), new FormOption("unspecified", "Prefer not to say")]),
                    new FormField($"passengers[{i}].nationality", "Nationality", FormFieldType.Country, true),
                    new FormField($"passengers[{i}].passportNumber", "Passport number", FormFieldType.Text, passportRequired, passportRequired ? supplier : null),
                    new FormField($"passengers[{i}].passportExpiry", "Passport expiry date", FormFieldType.Date, passportRequired, passportRequired ? supplier : null),
                ]));
        }

        groups.Add(new FormGroup(
            "contact",
            "Contact",
            [
                new FormField("contact.email", "E-mail", FormFieldType.Email, true),
                new FormField("contact.phone", "Phone", FormFieldType.Phone, true),
            ]));
        return new FormSchema(FormType, "Passenger details", groups);
    }
}

/// <summary>Validates and stores passenger details in the booking context. The model never sees the values.</summary>
public sealed class PassengerFormHandler(
    IBookingContextStore contexts,
    IPaymentBackend payments,
    CountryCatalog countries,
    TimeProvider clock,
    IOptions<AgentOptions> options) : IFormSubmissionHandler<PassengerFormRequest>
{
    public string FormType => PassengerFormSchema.FormType;

    public async Task<FormSubmissionResult> HandleAsync(string sessionId, PassengerFormRequest request, CancellationToken cancellationToken)
    {
        var booking = await contexts.GetAsync(sessionId, ServiceType.Flight, cancellationToken);
        if (booking?.Search is not { } snapshot || booking.SelectedItemId is null || booking.ConfirmedPrice is null)
        {
            return FormSubmissionResult.Invalid(new Dictionary<string, string[]>
            {
                ["form"] = ["No flight is ready for passenger details. Choose a flight and re-check its price first."],
            });
        }

        var search = SearchSnapshots.ReadParams<FlightSearchParams>(snapshot);
        var offer = SearchSnapshots.ReadResults<FlightOffer>(snapshot).First(o => o.Id == booking.SelectedItemId);

        var errors = new FieldErrors(countries, clock);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (request.Passengers.Count != search.Adults)
        {
            errors.Add("passengers", $"Exactly {search.Adults} passenger(s) are required.");
        }

        for (var i = 0; i < request.Passengers.Count; i++)
        {
            var p = request.Passengers[i];
            var prefix = $"passengers[{i}]";
            Put(values, $"{prefix}.firstName", errors.RequiredText($"{prefix}.firstName", p.FirstName, 60));
            Put(values, $"{prefix}.lastName", errors.RequiredText($"{prefix}.lastName", p.LastName, 60));
            Put(values, $"{prefix}.dateOfBirth", errors.Date($"{prefix}.dateOfBirth", p.DateOfBirth, mustBePast: true));
            Put(values, $"{prefix}.gender", errors.OneOf($"{prefix}.gender", p.Gender, "female", "male", "unspecified"));
            Put(values, $"{prefix}.nationality", errors.CountryCode($"{prefix}.nationality", p.Nationality));

            if (offer.RequiresPassport)
            {
                Put(values, $"{prefix}.passportNumber", errors.RequiredText($"{prefix}.passportNumber", p.PassportNumber, 20));
                Put(values, $"{prefix}.passportExpiry", errors.Date($"{prefix}.passportExpiry", p.PassportExpiry, mustBeFuture: true));
            }
            else
            {
                Put(values, $"{prefix}.passportNumber", errors.OptionalText($"{prefix}.passportNumber", p.PassportNumber, 20));
                Put(values, $"{prefix}.passportExpiry", errors.Date($"{prefix}.passportExpiry", p.PassportExpiry, required: false, mustBeFuture: true));
            }
        }

        Put(values, "contact.email", errors.Email("contact.email", request.Contact.Email));
        Put(values, "contact.phone", errors.Phone("contact.phone", request.Contact.Phone));

        if (errors.Any)
        {
            return FormSubmissionResult.Invalid(errors.ToDictionary());
        }

        await contexts.MutateAsync(
            sessionId,
            ServiceType.Flight,
            c => c with { Form = new FormRecord(values, PiiMasker.MaskAll(values), clock.GetUtcNow()) },
            cancellationToken);

        var methods = await payments.GetPaymentMethodsAsync(ServiceType.Flight, options.Value.Currency, cancellationToken);
        return FormSubmissionResult.Accepted(methods, "FormAccepted");
    }

    private static void Put(Dictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values[key] = value;
        }
    }
}
