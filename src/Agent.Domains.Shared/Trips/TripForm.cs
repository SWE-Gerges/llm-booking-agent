using Agent.Abstractions.Booking;
using Agent.Abstractions.Forms;
using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Core.Forms;
using Agent.Core.Guards;
using Agent.Core.Lookups;
using Agent.Core.Options;
using Microsoft.Extensions.Options;

namespace Agent.Domains.Shared.Trips;

public sealed class TravellerDetails
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? DateOfBirth { get; set; }

    public string? Gender { get; set; }

    public string? Nationality { get; set; }

    public string? PassportNumber { get; set; }

    public string? PassportExpiry { get; set; }
}

public sealed class TripFormRequest
{
    public List<TravellerDetails> Travellers { get; set; } = [];

    public ContactDetails Contact { get; set; } = new();

    public string? SpecialRequests { get; set; }
}

public static class TripFormSchema
{
    public const string FormType = "trip";

    /// <summary>One group per distinct traveller for the whole trip (not per service) plus contact details.</summary>
    public static FormSchema Build(int travellers, bool passportRequired)
    {
        var groups = new List<FormGroup>();
        for (var i = 0; i < travellers; i++)
        {
            groups.Add(new FormGroup(
                $"traveller{i + 1}",
                $"Traveller {i + 1}",
                [
                    new FormField($"travellers[{i}].firstName", "First name", FormFieldType.Text, true),
                    new FormField($"travellers[{i}].lastName", "Last name", FormFieldType.Text, true),
                    new FormField($"travellers[{i}].dateOfBirth", "Date of birth", FormFieldType.Date, true),
                    new FormField(
                        $"travellers[{i}].gender",
                        "Gender",
                        FormFieldType.Select,
                        true,
                        Options: [new FormOption("female", "Female"), new FormOption("male", "Male"), new FormOption("unspecified", "Prefer not to say")]),
                    new FormField($"travellers[{i}].nationality", "Nationality", FormFieldType.Country, true),
                    new FormField($"travellers[{i}].passportNumber", "Passport number", FormFieldType.Text, passportRequired, passportRequired ? "flight" : null),
                    new FormField($"travellers[{i}].passportExpiry", "Passport expiry date", FormFieldType.Date, passportRequired, passportRequired ? "flight" : null),
                ]));
        }

        groups.Add(new FormGroup(
            "contact",
            "Contact",
            [
                new FormField("contact.email", "E-mail", FormFieldType.Email, true),
                new FormField("contact.phone", "Phone", FormFieldType.Phone, true),
            ]));
        groups.Add(new FormGroup("extras", "Extras", [new FormField("specialRequests", "Special requests", FormFieldType.Text, false)]));
        return new FormSchema(FormType, "Trip details", groups);
    }
}

/// <summary>
/// Validates the one combined traveller form of a trip. Travellers are entered once for the whole trip; entering the
/// same person twice is rejected, so every service books the same de-duplicated manifest.
/// </summary>
public sealed class TripFormHandler(
    IBookingContextStore contexts,
    TripBasket basket,
    IPaymentBackend payments,
    CountryCatalog countries,
    TimeProvider clock,
    IOptions<AgentOptions> options) : IFormSubmissionHandler<TripFormRequest>
{
    public string FormType => TripFormSchema.FormType;

    public async Task<FormSubmissionResult> HandleAsync(string sessionId, TripFormRequest request, CancellationToken cancellationToken)
    {
        var state = await basket.ViewAsync(sessionId, cancellationToken);
        if (state.BookingId is not null || state.Lines.Count == 0)
        {
            return Invalid("form", "The trip is empty or already booked. Add a service to a trip first.");
        }

        if (state.NeedsPriceCheck.Count > 0)
        {
            return Invalid("form", $"Confirm the price again for: {string.Join(", ", state.NeedsPriceCheck.Select(s => s.ToString().ToLowerInvariant()))}.");
        }

        var errors = new FieldErrors(countries, clock);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (request.Travellers.Count != state.Travellers)
        {
            errors.Add("travellers", $"Exactly {state.Travellers} traveller(s) are required for this trip.");
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < request.Travellers.Count; i++)
        {
            var t = request.Travellers[i];
            var prefix = $"travellers[{i}]";
            var first = errors.RequiredText($"{prefix}.firstName", t.FirstName, 60);
            var last = errors.RequiredText($"{prefix}.lastName", t.LastName, 60);
            var birth = errors.Date($"{prefix}.dateOfBirth", t.DateOfBirth, mustBePast: true);
            Put(values, $"{prefix}.firstName", first);
            Put(values, $"{prefix}.lastName", last);
            Put(values, $"{prefix}.dateOfBirth", birth);
            Put(values, $"{prefix}.gender", errors.OneOf($"{prefix}.gender", t.Gender, "female", "male", "unspecified"));
            Put(values, $"{prefix}.nationality", errors.CountryCode($"{prefix}.nationality", t.Nationality));

            if (state.PassportRequired)
            {
                Put(values, $"{prefix}.passportNumber", errors.RequiredText($"{prefix}.passportNumber", t.PassportNumber, 20));
                Put(values, $"{prefix}.passportExpiry", errors.Date($"{prefix}.passportExpiry", t.PassportExpiry, mustBeFuture: true));
            }
            else
            {
                Put(values, $"{prefix}.passportNumber", errors.OptionalText($"{prefix}.passportNumber", t.PassportNumber, 20));
                Put(values, $"{prefix}.passportExpiry", errors.Date($"{prefix}.passportExpiry", t.PassportExpiry, required: false, mustBeFuture: true));
            }

            if (first is not null && last is not null && birth is not null)
            {
                var key = $"{first.ToLowerInvariant()}|{last.ToLowerInvariant()}|{birth}";
                if (seen.TryGetValue(key, out var earlier))
                {
                    errors.Add(prefix, $"This traveller is already entered as traveller {earlier + 1}. Each traveller is entered once for the whole trip.");
                }
                else
                {
                    seen[key] = i;
                }
            }
        }

        Put(values, "contact.email", errors.Email("contact.email", request.Contact.Email));
        Put(values, "contact.phone", errors.Phone("contact.phone", request.Contact.Phone));
        Put(values, "specialRequests", errors.OptionalText("specialRequests", request.SpecialRequests, 500));

        if (errors.Any)
        {
            return FormSubmissionResult.Invalid(errors.ToDictionary());
        }

        var masked = PiiMasker.MaskAll(values).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (masked.ContainsKey("specialRequests"))
        {
            masked["specialRequests"] = "***";
        }

        await contexts.MutateAsync(
            sessionId,
            ServiceType.Trip,
            c => c with { Form = new FormRecord(values, masked, clock.GetUtcNow()) },
            cancellationToken);

        var methods = await payments.GetPaymentMethodsAsync(ServiceType.Trip, options.Value.Currency, cancellationToken);
        return FormSubmissionResult.Accepted(methods, "FormAccepted");
    }

    private static FormSubmissionResult Invalid(string field, string message) =>
        FormSubmissionResult.Invalid(new Dictionary<string, string[]> { [field] = [message] });

    private static void Put(Dictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values[key] = value;
        }
    }
}
