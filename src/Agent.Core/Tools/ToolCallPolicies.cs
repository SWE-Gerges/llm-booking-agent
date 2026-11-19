using Agent.Abstractions.Ports;
using Agent.Abstractions.Sessions;
using Agent.Abstractions.Tools;
using Agent.Core.Options;
using Microsoft.Extensions.Options;

namespace Agent.Core.Tools;

/// <summary>
/// A second search in the same user turn is denied, except for exactly one corrected re-search when the first
/// result carried an assumption the model had to disclose.
/// </summary>
public sealed class OneSearchPerTurnPolicy : IToolCallPolicy
{
    public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken cancellationToken)
    {
        if (context.Tool is not ISearchTool)
        {
            return ValueTask.FromResult(PolicyDecision.Allow);
        }

        var turn = context.Turn;
        var allowed = turn.SearchCount == 0
            || (turn.SearchCount == 1 && turn.FirstSearchCarriedAssumption && !turn.CorrectedSearchUsed);

        return ValueTask.FromResult(
            allowed
                ? PolicyDecision.Allow
                : PolicyDecision.Deny(
                    "Only one search is allowed per user message. Answer from the results you already have, "
                    + "or ask the user for the missing detail and search on their next message."));
    }
}

/// <summary>Submit tools are refused until the price was re-checked or pre-booked in this session and is still fresh.</summary>
public sealed class FinalizeRequiresConfirmedPricePolicy(IBookingContextStore contexts, IOptions<AgentOptions> options) : IToolCallPolicy
{
    public async ValueTask<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken cancellationToken)
    {
        // A trip checkout combines several services; it is validated against each service by its own policy.
        if (context.Tool is not IFinalizingTool finalizing || finalizing.Service == ServiceType.Trip)
        {
            return PolicyDecision.Allow;
        }

        var booking = await contexts.GetAsync(context.Session.SessionId, finalizing.Service, cancellationToken);
        if (booking?.ConfirmedPrice is not { } confirmed)
        {
            return PolicyDecision.Deny(
                "The price has not been re-checked in this session. Re-check or pre-book the selected option first, then try again.");
        }

        return context.Now - confirmed.ConfirmedAt > options.Value.PriceConfirmationMaxAge
            ? PolicyDecision.Deny("The confirmed price is out of date. Re-check or pre-book the selected option again, then try again.")
            : PolicyDecision.Allow;
    }
}
