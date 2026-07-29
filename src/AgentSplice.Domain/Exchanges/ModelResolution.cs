using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// The outcome of resolving a client-visible model to a runtime and upstream model
/// (docs/SPECIFICATION.md FR-MOD-005, FR-TRACE-007).
/// </summary>
/// <remarks>
/// <see cref="IsRoutingChange"/> is what makes alias substitution observable. A resolution that
/// changed the model identifier must produce a routing observation, because the durable core is
/// otherwise free to rewrite requests invisibly.
/// </remarks>
public sealed record ModelResolution
{
    private ModelResolution()
    {
    }

    /// <summary>What the client asked for.</summary>
    public ClientModelId ClientModel { get; private init; }

    /// <summary>The runtime endpoint the request will be sent to.</summary>
    public RuntimeEndpointId Runtime { get; private init; }

    /// <summary>The model identifier that will be sent upstream.</summary>
    public UpstreamModelId UpstreamModel { get; private init; }

    /// <summary>How the resolution was reached.</summary>
    public ModelResolutionSource Source { get; private init; }

    /// <summary>The alias that produced the resolution, when one did.</summary>
    public ModelAliasId? Alias { get; private init; }

    /// <summary>
    /// True when the upstream model identifier differs from what the client asked for, and the
    /// change therefore has to appear as an explicit observation.
    /// </summary>
    public bool IsRoutingChange =>
        !string.Equals(ClientModel.Value, UpstreamModel.Value, StringComparison.Ordinal);

    /// <summary>Creates a resolution produced by a configured alias.</summary>
    public static ModelResolution FromAlias(
        ClientModelId clientModel,
        ModelAliasId alias,
        RuntimeEndpointId runtime,
        UpstreamModelId upstreamModel) =>
        Create(clientModel, runtime, upstreamModel, ModelResolutionSource.ConfiguredAlias, alias);

    /// <summary>Creates a resolution that forwards the client's identifier unchanged.</summary>
    public static ModelResolution PassThrough(
        ClientModelId clientModel,
        RuntimeEndpointId runtime,
        UpstreamModelId upstreamModel) =>
        Create(clientModel, runtime, upstreamModel, ModelResolutionSource.PassThrough, alias: null);

    /// <summary>Creates a resolution against a model discovered from the runtime catalogue.</summary>
    public static ModelResolution FromDiscovery(
        ClientModelId clientModel,
        RuntimeEndpointId runtime,
        UpstreamModelId upstreamModel) =>
        Create(clientModel, runtime, upstreamModel, ModelResolutionSource.Discovered, alias: null);

    private static ModelResolution Create(
        ClientModelId clientModel,
        RuntimeEndpointId runtime,
        UpstreamModelId upstreamModel,
        ModelResolutionSource source,
        ModelAliasId? alias)
    {
        if (clientModel.IsEmpty)
        {
            throw new ArgumentException("A resolution requires the requested model.", nameof(clientModel));
        }

        if (runtime.IsEmpty)
        {
            throw new ArgumentException("A resolution requires a runtime endpoint.", nameof(runtime));
        }

        if (upstreamModel.IsEmpty)
        {
            throw new ArgumentException("A resolution requires an upstream model.", nameof(upstreamModel));
        }

        if (source == ModelResolutionSource.ConfiguredAlias && alias is null)
        {
            throw new ArgumentException(
                "An alias-sourced resolution must record which alias produced it.",
                nameof(alias));
        }

        return new ModelResolution
        {
            ClientModel = clientModel,
            Runtime = runtime,
            UpstreamModel = upstreamModel,
            Source = source,
            Alias = alias,
        };
    }
}
