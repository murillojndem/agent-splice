using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Runtimes;

namespace AgentSplice.Application.Models;

/// <summary>
/// One client-visible model, with the provenance of everything claimed about it
/// (docs/SPECIFICATION.md FR-MOD-002, FR-MOD-007).
/// </summary>
/// <remarks>
/// <see cref="Created"/> stays <c>null</c> when nothing reported it. It is a Unix timestamp, so zero
/// is the claim "created on 1970-01-01", not "unknown" — the substitution the OpenAI schema forces
/// happens at the envelope and nowhere else (FR-DASH-006).
/// </remarks>
public sealed record ModelCatalogueEntry
{
    private ModelCatalogueEntry()
    {
    }

    /// <summary>The identifier a client sees and sends.</summary>
    public ClientModelId ClientModel { get; private init; }

    /// <summary>The runtime that serves it.</summary>
    public RuntimeEndpointId Runtime { get; private init; }

    /// <summary>The identifier sent upstream.</summary>
    public UpstreamModelId UpstreamModel { get; private init; }

    /// <summary>How this entry came to exist.</summary>
    public ModelResolutionSource Source { get; private init; }

    /// <summary>The alias that produced it, when one did.</summary>
    public ModelAliasId? Alias { get; private init; }

    /// <summary>Creation time as reported, or <c>null</c> when nothing reported one.</summary>
    public long? Created { get; private init; }

    /// <summary>Owner as reported, or <c>null</c> when nothing reported one.</summary>
    public string? OwnedBy { get; private init; }

    /// <summary>How this entry's capability claims were established.</summary>
    public CapabilityProvenance CapabilityProvenance { get; private init; }

    /// <summary>Whether the owning runtime answered the most recent discovery attempt.</summary>
    public bool Reachable { get; private init; }

    /// <summary>Configuration position of the owning runtime; the duplicate tie-break (FR-MOD-004).</summary>
    public int RuntimeOrdinal { get; private init; }

    /// <summary>Alias priority, or <see cref="int.MaxValue"/> for a discovered entry.</summary>
    public int AliasPriority { get; private init; }

    /// <summary>Creates an entry produced by a configured alias.</summary>
    public static ModelCatalogueEntry FromAlias(
        ConfiguredAlias alias,
        int runtimeOrdinal,
        bool reachable,
        long? created = null,
        string? ownedBy = null)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentOutOfRangeException.ThrowIfNegative(runtimeOrdinal);

        return new ModelCatalogueEntry
        {
            ClientModel = ClientModelId.Create(alias.Id.Value),
            Runtime = alias.RuntimeId,
            UpstreamModel = alias.UpstreamModel,
            Source = ModelResolutionSource.ConfiguredAlias,
            Alias = alias.Id,
            Created = created,
            OwnedBy = ownedBy,

            // An operator declared the mapping, so the claim that this identifier routes somewhere
            // is configured. Nothing here establishes what the model can do.
            CapabilityProvenance = CapabilityProvenance.Configured,
            Reachable = reachable,
            RuntimeOrdinal = runtimeOrdinal,
            AliasPriority = alias.Priority,
        };
    }

    /// <summary>Creates an entry produced by a runtime's own catalogue.</summary>
    public static ModelCatalogueEntry FromDiscovery(
        Runtimes.DiscoveredModel model,
        RuntimeEndpointId runtime,
        int runtimeOrdinal,
        bool reachable)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfNegative(runtimeOrdinal);

        return new ModelCatalogueEntry
        {
            ClientModel = ClientModelId.Create(model.Id.Value),
            Runtime = runtime,
            UpstreamModel = model.Id,
            Source = ModelResolutionSource.Discovered,
            Created = model.Created,
            OwnedBy = model.OwnedBy,
            CapabilityProvenance = CapabilityProvenance.Discovered,
            Reachable = reachable,
            RuntimeOrdinal = runtimeOrdinal,
            AliasPriority = int.MaxValue,
        };
    }
}
