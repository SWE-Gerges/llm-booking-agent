using Agent.Abstractions.Booking;
using Agent.Abstractions.Chat;

namespace Agent.Api.Contracts;

public sealed record StartChatRequest(string? Locale);

public sealed record StartChatResponse(
    string SessionToken,
    DateTimeOffset ExpiresAt,
    string Greeting,
    string Locale,
    string Direction,
    string AssistantName);

public sealed record SendMessageRequest(string Message);

public sealed record ChatMessageResponse(
    string Reply,
    string Locale,
    string Direction,
    string State,
    IReadOnlyList<StructuredDataBlock> StructuredData);

public sealed record HistoryItem(string Role, string Text, DateTimeOffset CreatedAt);

public sealed record HistoryResponse(IReadOnlyList<HistoryItem> Messages);

public sealed record FormAcceptedResponse(
    string Message,
    string Locale,
    string Direction,
    IReadOnlyList<PaymentMethod> PaymentMethods,
    string NextStep);

public sealed record MoneyResponse(decimal Amount, string Currency);

public sealed record BookingStatusResponse(
    string BookingId,
    string Service,
    string Status,
    MoneyResponse Total,
    string PaymentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

public sealed record CountryResponse(string Code, string Name);
