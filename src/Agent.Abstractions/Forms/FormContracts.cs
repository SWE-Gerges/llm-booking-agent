using Agent.Abstractions.Booking;

namespace Agent.Abstractions.Forms;

public enum FormFieldType
{
    Text,
    Email,
    Phone,
    Date,
    Select,
    Country,
}

public sealed record FormOption(string Value, string Label);

/// <param name="RequiredBy">Why the field is required when it is conditional, for example "international_travel".</param>
public sealed record FormField(
    string Name,
    string Label,
    FormFieldType Type,
    bool Required,
    string? RequiredBy = null,
    IReadOnlyList<FormOption>? Options = null);

public sealed record FormGroup(string Id, string Title, IReadOnlyList<FormField> Fields);

/// <summary>Describes a server-side form. Forms are rendered by the API consumer and submitted to non-LLM endpoints.</summary>
public sealed record FormSchema(string FormType, string Title, IReadOnlyList<FormGroup> Groups);

public sealed record FormSubmissionResult(
    bool Success,
    IReadOnlyDictionary<string, string[]> Errors,
    string? NextStepMessageKey,
    IReadOnlyList<PaymentMethod> PaymentMethods)
{
    public static FormSubmissionResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(false, errors, null, []);

    public static FormSubmissionResult Accepted(IReadOnlyList<PaymentMethod> paymentMethods, string nextStepMessageKey) =>
        new(true, new Dictionary<string, string[]>(), nextStepMessageKey, paymentMethods);
}

/// <summary>Handles one form type. Registered per form so the API layer never grows a catch-all method.</summary>
public interface IFormSubmissionHandler<in TRequest>
    where TRequest : class
{
    string FormType { get; }

    Task<FormSubmissionResult> HandleAsync(string sessionId, TRequest request, CancellationToken cancellationToken);
}
