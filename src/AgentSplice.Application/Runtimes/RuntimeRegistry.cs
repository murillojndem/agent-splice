using System.Collections.Frozen;
using System.Collections.ObjectModel;
using AgentSplice.Application.Configuration;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// The configured runtimes, projected once into validated domain types and ordered as configuration
/// declares them.
/// </summary>
/// <remarks>
/// Projection happens once at construction rather than per request, because the ordinal each runtime
/// receives here is what makes the FR-MOD-004 duplicate tie-break deterministic: it is the only
/// operator-visible preference signal, and it must not depend on dictionary enumeration order.
///
/// Startup validation has already rejected a malformed identifier or base URL, so a violation
/// reaching this point is a broken invariant rather than bad input, and it fails loudly at startup.
/// </remarks>
public sealed class RuntimeRegistry
{
    private readonly FrozenDictionary<string, RuntimeTarget> byId;

    /// <summary>Projects the validated configuration tree.</summary>
    public RuntimeRegistry(IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        var targets = new List<RuntimeTarget>(value.Runtimes.Count);

        for (var ordinal = 0; ordinal < value.Runtimes.Count; ordinal++)
        {
            targets.Add(Project(value.Runtimes[ordinal], ordinal));
        }

        All = new ReadOnlyCollection<RuntimeTarget>(targets);
        Enabled = new ReadOnlyCollection<RuntimeTarget>([.. targets.Where(target => target.Enabled)]);
        byId = targets.ToFrozenDictionary(target => target.Id.Value, StringComparer.Ordinal);

        DefaultRuntime = value.DefaultRuntimeId is { } defaultRuntimeId
            && byId.TryGetValue(RuntimeEndpointId.Create(defaultRuntimeId).Value, out var target)
            && target.Enabled
                ? target
                : null;
    }

    /// <summary>Every configured runtime, in configuration order.</summary>
    public IReadOnlyList<RuntimeTarget> All { get; }

    /// <summary>Every enabled runtime, in configuration order.</summary>
    public IReadOnlyList<RuntimeTarget> Enabled { get; }

    /// <summary>
    /// The pass-through target, or <c>null</c> when none is configured
    /// (<see cref="Domain.Exchanges.ModelResolutionSource.PassThrough"/>).
    /// </summary>
    public RuntimeTarget? DefaultRuntime { get; }

    /// <summary>Finds a runtime by identifier, whether or not it is enabled.</summary>
    public RuntimeTarget? Find(RuntimeEndpointId id) =>
        byId.TryGetValue(id.Value, out var target) ? target : null;

    private static RuntimeTarget Project(RuntimeEndpointOptions runtime, int ordinal)
    {
        if (!RuntimeEndpointId.TryCreate(runtime.Id, out var id))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:runtimes[{ordinal}]:id '{runtime.Id}' is not a valid runtime identifier. Startup validation should have rejected it."));
        }

        if (!Uri.TryCreate(runtime.BaseUrl, UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:runtimes[{ordinal}]:baseUrl is not an absolute URL. Startup validation should have rejected it."));
        }

        return RuntimeTarget.Create(
            id,
            runtime.Provider,
            baseAddress,
            RuntimeDiscoveryPolicy.Create(
                runtime.Discovery.Enabled,
                runtime.Discovery.CacheDuration,
                runtime.Discovery.ServeStaleOnFailure,
                runtime.Discovery.CapabilityProbingEnabled),
            RuntimeTimeouts.Create(
                runtime.Timeouts.Connect,
                runtime.Timeouts.ResponseHeaders,
                runtime.Timeouts.IdleStream,
                runtime.Timeouts.Total),
            ordinal,
            runtime.Enabled,
            runtime.ApiKeyEnvironmentVariable);
    }
}
