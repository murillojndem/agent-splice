using System.Collections.ObjectModel;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Runtimes;

namespace AgentSplice.Application.Models;

/// <summary>
/// The composed client-visible catalogue, together with what happened to each runtime that was asked.
/// </summary>
/// <remarks>
/// The outcomes are carried alongside the entries because the status a client receives depends on
/// them: an empty list because nothing is configured is a truthful 200, while an empty list because
/// no runtime could be reached is a 502. Discarding the outcomes would make those indistinguishable.
/// </remarks>
public sealed record ModelCatalogueResult
{
    private ModelCatalogueResult()
    {
    }

    /// <summary>The deduplicated, client-visible entries in the order they should be listed.</summary>
    public IReadOnlyList<ModelCatalogueEntry> Entries { get; private init; } = [];

    /// <summary>What happened for each runtime whose catalogue was consulted.</summary>
    public IReadOnlyList<RuntimeDiscoveryOutcome> Outcomes { get; private init; } = [];

    /// <summary>True when at least one runtime's catalogue was consulted.</summary>
    public bool AnyDiscoveryAttempted => Outcomes.Count > 0;

    /// <summary>True when every consulted runtime failed to yield a usable catalogue.</summary>
    public bool EveryDiscoveryAttemptFailed =>
        AnyDiscoveryAttempted && Outcomes.All(outcome => !outcome.YieldedCatalogue);

    /// <summary>Creates a composed result.</summary>
    public static ModelCatalogueResult Create(
        IEnumerable<ModelCatalogueEntry> entries,
        IEnumerable<RuntimeDiscoveryOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(outcomes);

        return new ModelCatalogueResult
        {
            Entries = new ReadOnlyCollection<ModelCatalogueEntry>([.. entries]),
            Outcomes = new ReadOnlyCollection<RuntimeDiscoveryOutcome>([.. outcomes]),
        };
    }
}

/// <summary>
/// What consulting one runtime's catalogue produced (docs/SPECIFICATION.md FR-HEALTH-004).
/// </summary>
public sealed record RuntimeDiscoveryOutcome
{
    private RuntimeDiscoveryOutcome()
    {
    }

    /// <summary>The runtime that was consulted.</summary>
    public RuntimeEndpointId Runtime { get; private init; }

    /// <summary>Health as this attempt observed it.</summary>
    public RuntimeHealthStatus Status { get; private init; }

    /// <summary>True when a catalogue was available, whether fresh or stale.</summary>
    public bool YieldedCatalogue { get; private init; }

    /// <summary>True when the catalogue served was past its refresh window.</summary>
    public bool ServedFromStaleCache { get; private init; }

    /// <summary>How the attempt failed, or <c>null</c> when it did not.</summary>
    public UpstreamFailure? Failure { get; private init; }

    /// <summary>Describes one consulted runtime.</summary>
    public static RuntimeDiscoveryOutcome From(RuntimeCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return new RuntimeDiscoveryOutcome
        {
            Runtime = catalogue.Runtime,
            YieldedCatalogue = catalogue.IsAvailable,
            ServedFromStaleCache = catalogue.IsStale,
            Failure = catalogue.Failure,
            Status = Classify(catalogue),
        };
    }

    /// <summary>Describes a runtime whose provider module is missing.</summary>
    /// <remarks>
    /// A configuration defect rather than an availability problem, so it is reported as an
    /// incompatible response rather than as unreachable: the runtime was never contacted.
    /// </remarks>
    public static RuntimeDiscoveryOutcome ProviderMissing(RuntimeEndpointId runtime) =>
        new()
        {
            Runtime = runtime,
            Status = RuntimeHealthStatus.IncompatibleResponse,
            YieldedCatalogue = false,
        };

    private static RuntimeHealthStatus Classify(RuntimeCatalogue catalogue)
    {
        if (catalogue.Failure is { } failure)
        {
            return failure.HealthStatus;
        }

        // A runtime that answers with no models is reachable but unusable, which a naive health
        // check reports as healthy and an agent client experiences as a broken deployment.
        return catalogue.Models.Count == 0 ? RuntimeHealthStatus.NoModels : RuntimeHealthStatus.Healthy;
    }
}
