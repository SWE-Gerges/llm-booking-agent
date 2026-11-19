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

namespace Agent.Domains.Hotels.Forms;

public sealed class LeadGuestDetails
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Nationality { get; set; }
}

public sealed class GuestFormRequest
{
    public LeadGuestDetails LeadGuest { get; set; } = new();

    public ContactDetails Contact { get; set; } = new();

    public string? SpecialRequests { get; set; }
}

public static class GuestFormSchema
{
    public const string FormType = "guest";

    public static FormSchema Build() => new(
        FormType,
        "Guest details",
        [
            new FormGroup(
                "leadGuest",
                "Lead guest",
                [
                    new FormField("leadGuest.firstName", "First name", FormFieldType.Text, true),
                    new FormField("leadGuest.lastName", "Last name", FormFieldType.Text, true),
                    new FormField("leadGuest.nationality", "Nationality", FormFieldType.Country, true),
                ]),
            new FormGroup(
                "contact",
                "Contact",
                [
                    new FormField("contact.email", "E-mail", FormFieldType.Email, true),
                    new FormField("contact.phone", "Phone", FormFieldType.Phone, true),
                ]),
            new FormGroup(
                "extras",
                "Extras",
                [new FormField("specialRequests", "Special requests", FormFieldType.Text, false)]),
        ]);
}

/// <summary>Validates and stores guest details in the booking context. The model never sees the values.</summary>
public sealed class GuestFormHandler(
    IBookingContextStore contexts,
    IPaymentBackend payments,
    CountryCatalog countries,
    TimeProvider clock,
    IOptions<AgentOptions> options) : IFormSubmissionHandler<GuestFormRequest>
{
    public string FormType => GuestFormSchema.FormType;

    public async Task<FormSubmissionResult> HandleAsync(string sessionId, GuestFormRequest request, CancellationToken cancellationToken)
    {
        var booking = await contexts.GetAsync(sessionId, ServiceType.Hotel, cancellationToken);
        if (booking?.SelectedItemId is null || booking.ConfirmedPrice is null)
        {
            return FormSubmissionResult.Invalid(new Dictionary<string, string[]>
            {
                ["form"] = ["No room is ready for guest details. Choose a room and pre-book it first."],
            });
        }

        var errors = new FieldErrors(countries, clock);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Put(values, "leadGuest.firstName", errors.RequiredText("leadGuest.firstName", request.LeadGuest.FirstName, 60));
        Put(values, "leadGuest.lastName", errors.RequiredText("leadGuest.lastName", request.LeadGuest.LastName, 60));
        Put(values, "leadGuest.nationality", errors.CountryCode("leadGuest.nationality", request.LeadGuest.Nationality));
        Put(values, "contact.email", errors.Email("contact.email", request.Contact.Email));
        Put(values, "contact.phone", errors.Phone("contact.phone", request.Contact.Phone));
        Put(values, "specialRequests", errors.OptionalText("specialRequests", request.SpecialRequests, 500));

        if (errors.Any)
        {
            return FormSubmissionResult.Invalid(errors.ToDictionary());
        }

        var masked = PiiMasker.MaskAll(values);
        if (masked.ContainsKey("specialRequests"))
        {
            masked = new Dictionary<string, string>(masked, StringComparer.Ordinal) { ["specialRequests"] = "***" };
        }

        await contexts.MutateAsync(
            sessionId,
            ServiceType.Hotel,
            c => c with { Form = new FormRecord(values, masked, clock.GetUtcNow()) },
            cancellationToken);

        var methods = await payments.GetPaymentMethodsAsync(ServiceType.Hotel, options.Value.Currency, cancellationToken);
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
