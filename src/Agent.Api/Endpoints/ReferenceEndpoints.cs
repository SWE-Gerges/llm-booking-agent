using System.Security.Claims;
using Agent.Abstractions.Llm;
using Agent.Api.Contracts;
using Agent.Api.Security;
using Agent.Core.Lookups;
using Agent.Core.Tools;
using Agent.Domains.Shared;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Agent.Api.Endpoints;

public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/bookings/{bookingId}/status", GetBookingStatusAsync)
            .RequireAuthorization(AuthPolicies.Session)
            .WithTags("Bookings")
            .WithName("GetBookingStatus")
            .WithSummary("Report the status of a booking created in this session.")
            .Produces<BookingStatusResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/lookups/countries", (CountryCatalog countries) =>
                Results.Ok(countries.All.Select(c => new CountryResponse(c.Code, c.Name)).ToList()))
            .WithTags("Lookups")
            .WithName("ListCountries")
            .WithSummary("ISO 3166 country codes and names used by the forms.")
            .Produces<List<CountryResponse>>();

        api.MapGet("/tools", (ToolRegistry registry) => Results.Ok(registry.Definitions))
            .RequireAuthorization(AuthPolicies.ClientKey)
            .WithTags("Tools")
            .WithName("ListTools")
            .WithSummary("The tool definitions registered with the assistant, in prompt order.")
            .Produces<IReadOnlyList<LlmToolDefinition>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).WithTags("Health");
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(Agent.Persistence.DependencyInjection.ReadyTag) })
            .WithTags("Health");
        return app;
    }

    private static async Task<IResult> GetBookingStatusAsync(
        string bookingId,
        ClaimsPrincipal user,
        BookingStatusService statuses,
        CancellationToken cancellationToken)
    {
        var found = await statuses.GetAsync(user.FindFirstValue(AuthSchemes.SessionClaim)!, bookingId, cancellationToken);
        if (found.Count == 0)
        {
            return Results.Problem(
                title: "Booking not found",
                detail: "No booking with that id exists in this session.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var s = found[0];
        return Results.Ok(new BookingStatusResponse(
            s.BookingId,
            s.Service.ToString().ToLowerInvariant(),
            s.Status.ToString(),
            new MoneyResponse(s.Total.Amount, s.Total.Currency),
            s.PaymentUrl,
            s.CreatedAt,
            s.ConfirmedAt));
    }
}
