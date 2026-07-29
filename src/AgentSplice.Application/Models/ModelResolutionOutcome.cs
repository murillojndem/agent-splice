using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Observations;

namespace AgentSplice.Application.Models;

/// <summary>
/// The result of resolving a client-visible model identifier
/// (docs/SPECIFICATION.md FR-MOD-005, FR-TRACE-007).
/// </summary>
/// <remarks>
/// Three facts are carried separately because they are genuinely independent, and conflating them is
/// how a routing decision becomes invisible:
///
/// <list type="bullet">
/// <item><see cref="RoutingWasApplied"/> — AgentSplice chose the destination. True for an alias, for
/// a discovery match that had to be disambiguated, and for a pass-through, even when the identifier
/// never changed.</item>
/// <item><see cref="RequiresBodyRewrite"/> — the identifier the runtime will see differs from the one
/// the client sent, so the forwarded body cannot be the original bytes.</item>
/// <item><see cref="ModelResolution.Source"/> — how the destination was chosen.</item>
/// </list>
///
/// <see cref="ModelResolution.IsRoutingChange"/> answers only the second. An alias that selects a
/// runtime without renaming the model is still a routing decision that FR-TRACE-007 requires to be
/// represented as an event.
/// </remarks>
public sealed record ModelResolutionOutcome
{
    private ModelResolutionOutcome()
    {
    }

    /// <summary>The resolution, or <c>null</c> when the identifier did not resolve.</summary>
    public ModelResolution? Resolution { get; private init; }

    /// <summary>The runtime the request will be sent to, or <c>null</c> when unresolved.</summary>
    public RuntimeTarget? Runtime { get; private init; }

    /// <summary>Why resolution failed, or <c>null</c> when it succeeded.</summary>
    public FailureClass? Failure { get; private init; }

    /// <summary>True when AgentSplice, rather than the client, chose the destination.</summary>
    public bool RoutingWasApplied { get; private init; }

    /// <summary>Sanitised detail explaining the decision. Never a prompt, a host, or a credential.</summary>
    public SafeDetails Details { get; private init; } = SafeDetails.Empty;

    /// <summary>True when the identifier resolved to a runtime and upstream model.</summary>
    public bool Succeeded => Resolution is not null;

    /// <summary>True when the forwarded body must carry a different model identifier.</summary>
    public bool RequiresBodyRewrite => Resolution?.IsRoutingChange ?? false;

    /// <summary>Records a successful resolution.</summary>
    public static ModelResolutionOutcome Resolved(
        ModelResolution resolution,
        RuntimeTarget runtime,
        bool routingWasApplied,
        SafeDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(runtime);

        return new ModelResolutionOutcome
        {
            Resolution = resolution,
            Runtime = runtime,

            // A renamed identifier is always a routing decision, whatever else the caller observed.
            RoutingWasApplied = routingWasApplied || resolution.IsRoutingChange,
            Details = details ?? SafeDetails.Empty,
        };
    }

    /// <summary>Records that the identifier did not resolve, and why.</summary>
    public static ModelResolutionOutcome Unresolved(FailureClass failure, SafeDetails? details = null)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown failure class.");
        }

        return new ModelResolutionOutcome
        {
            Failure = failure,
            Details = details ?? SafeDetails.Empty,
        };
    }
}
