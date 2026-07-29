using System.Collections.ObjectModel;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Models;

/// <summary>
/// What AgentSplice currently knows about one runtime's model catalogue.
/// </summary>
/// <remarks>
/// Three states are deliberately distinct, because resolution reports them as three different
/// outcomes: a catalogue that is current, one that is being served past its refresh window because
/// the runtime could not be reached, and no catalogue at all. Collapsing the last two would make
/// "the model does not exist" indistinguishable from "AgentSplice could not ask", which is the
/// misleading evidence FR-TRACE-006 exists to prevent.
/// </remarks>
public sealed record RuntimeCatalogue
{
    private RuntimeCatalogue()
    {
    }

    /// <summary>The runtime this catalogue describes.</summary>
    public RuntimeEndpointId Runtime { get; private init; }

    /// <summary>The models known for this runtime. Empty when none are known.</summary>
    public IReadOnlyList<DiscoveredModel> Models { get; private init; } = [];

    /// <summary>When the catalogue was retrieved, or <c>null</c> when it never was.</summary>
    public DateTimeOffset? RetrievedAt { get; private init; }

    /// <summary>True when the catalogue is being served past its refresh window after a failure.</summary>
    public bool IsStale { get; private init; }

    /// <summary>The most recent refresh failure, or <c>null</c> when the last attempt succeeded.</summary>
    public UpstreamFailure? Failure { get; private init; }

    /// <summary>
    /// True when this catalogue can answer "does the runtime offer this model?". False means the
    /// question was never answered, not that the answer was no.
    /// </summary>
    public bool IsAvailable => RetrievedAt is not null;

    /// <summary>Records a catalogue the runtime returned.</summary>
    public static RuntimeCatalogue Fresh(
        RuntimeEndpointId runtime,
        IEnumerable<DiscoveredModel> models,
        DateTimeOffset retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(models);

        return new RuntimeCatalogue
        {
            Runtime = runtime,
            Models = new ReadOnlyCollection<DiscoveredModel>([.. models]),
            RetrievedAt = retrievedAt,
        };
    }

    /// <summary>Records a previously retrieved catalogue being reused after a failed refresh.</summary>
    public static RuntimeCatalogue Stale(
        RuntimeEndpointId runtime,
        IEnumerable<DiscoveredModel> models,
        DateTimeOffset retrievedAt,
        UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(failure);

        return new RuntimeCatalogue
        {
            Runtime = runtime,
            Models = new ReadOnlyCollection<DiscoveredModel>([.. models]),
            RetrievedAt = retrievedAt,
            IsStale = true,
            Failure = failure,
        };
    }

    /// <summary>Records that the runtime's catalogue is not known at all.</summary>
    public static RuntimeCatalogue Unavailable(RuntimeEndpointId runtime, UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new RuntimeCatalogue { Runtime = runtime, Failure = failure };
    }
}
