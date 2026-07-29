using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Models;

/// <summary>
/// A client-visible alias, projected from configuration (docs/SPECIFICATION.md section 13.2).
/// </summary>
/// <remarks>
/// <see cref="Ordinal"/> preserves declaration order so that ordering is never left to dictionary
/// enumeration, and <see cref="RuntimeIsEnabled"/> is captured here so the resolver can tell "no
/// such alias" from "that alias points at a runtime that is switched off" — two situations an
/// operator needs to distinguish and a client sees as the same 404.
/// </remarks>
public sealed record ConfiguredAlias
{
    private ConfiguredAlias()
    {
    }

    /// <summary>The identifier clients see and send.</summary>
    public ModelAliasId Id { get; private init; }

    /// <summary>The runtime this alias routes to.</summary>
    public RuntimeEndpointId RuntimeId { get; private init; }

    /// <summary>The model identifier sent upstream.</summary>
    public UpstreamModelId UpstreamModel { get; private init; }

    /// <summary>Whether the alias is offered and resolvable.</summary>
    public bool Enabled { get; private init; }

    /// <summary>Whether the runtime this alias targets participates in routing.</summary>
    public bool RuntimeIsEnabled { get; private init; }

    /// <summary>Ordering hint among aliases. Lower sorts first.</summary>
    public int Priority { get; private init; }

    /// <summary>Position in <c>agentsplice:aliases</c>, used as the final ordering tie-break.</summary>
    public int Ordinal { get; private init; }

    /// <summary>True when this alias can currently resolve a request.</summary>
    public bool IsRoutable => Enabled && RuntimeIsEnabled;

    /// <summary>Creates a projected alias.</summary>
    public static ConfiguredAlias Create(
        ModelAliasId id,
        RuntimeEndpointId runtimeId,
        UpstreamModelId upstreamModel,
        bool enabled,
        bool runtimeIsEnabled,
        int priority,
        int ordinal)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("An alias requires an identifier.", nameof(id));
        }

        if (runtimeId.IsEmpty)
        {
            throw new ArgumentException("An alias requires a runtime.", nameof(runtimeId));
        }

        if (upstreamModel.IsEmpty)
        {
            throw new ArgumentException("An alias requires an upstream model.", nameof(upstreamModel));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        return new ConfiguredAlias
        {
            Id = id,
            RuntimeId = runtimeId,
            UpstreamModel = upstreamModel,
            Enabled = enabled,
            RuntimeIsEnabled = runtimeIsEnabled,
            Priority = priority,
            Ordinal = ordinal,
        };
    }
}
