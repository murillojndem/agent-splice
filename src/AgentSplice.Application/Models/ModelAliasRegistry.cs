using System.Collections.Frozen;
using System.Collections.ObjectModel;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace AgentSplice.Application.Models;

/// <summary>
/// The configured aliases, indexed for exact lookup and grouped by runtime.
/// </summary>
/// <remarks>
/// Lookup is ordinal and case-sensitive because a client echoes back the exact string
/// <c>GET /v1/models</c> gave it, and FR-MOD-005 requires that exact string to resolve
/// deterministically.
///
/// Disabled aliases are indexed too. A disabled alias must not resolve, but the resolver still needs
/// to know one exists in order to record why the request was not routed.
/// </remarks>
public sealed class ModelAliasRegistry
{
    private readonly FrozenDictionary<string, ConfiguredAlias> byId;
    private readonly FrozenDictionary<string, ReadOnlyCollection<ConfiguredAlias>> byRuntime;

    /// <summary>Projects the validated alias configuration.</summary>
    public ModelAliasRegistry(IOptions<AgentSpliceOptions> options, RuntimeRegistry runtimes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimes);

        var configured = options.Value.Aliases;
        var projected = new List<ConfiguredAlias>(configured.Count);

        for (var ordinal = 0; ordinal < configured.Count; ordinal++)
        {
            projected.Add(Project(configured[ordinal], ordinal, runtimes));
        }

        All = new ReadOnlyCollection<ConfiguredAlias>(projected);

        byId = projected
            .DistinctBy(alias => alias.Id.Value, StringComparer.Ordinal)
            .ToFrozenDictionary(alias => alias.Id.Value, StringComparer.Ordinal);

        byRuntime = projected
            .GroupBy(alias => alias.RuntimeId.Value, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => new ReadOnlyCollection<ConfiguredAlias>([.. Order(group)]),
                StringComparer.Ordinal);
    }

    /// <summary>Every configured alias, in declaration order.</summary>
    public IReadOnlyList<ConfiguredAlias> All { get; }

    /// <summary>
    /// Finds an alias by the exact identifier a client sent, whether or not it is routable.
    /// </summary>
    /// <remarks>
    /// Startup validation rejects duplicate alias identifiers, so at most one can match and
    /// <see cref="ConfiguredAlias.Priority"/> never breaks a tie here.
    /// </remarks>
    public ConfiguredAlias? Find(ClientModelId requested) =>
        requested.IsEmpty ? null : byId.GetValueOrDefault(requested.Value);

    /// <summary>The aliases targeting a runtime, ordered by priority then declaration order.</summary>
    public IReadOnlyList<ConfiguredAlias> ForRuntime(RuntimeEndpointId runtime)
    {
        if (byRuntime.TryGetValue(runtime.Value, out var aliases))
        {
            return aliases;
        }

        return [];
    }

    private static IEnumerable<ConfiguredAlias> Order(IEnumerable<ConfiguredAlias> aliases) =>
        aliases.OrderBy(alias => alias.Priority).ThenBy(alias => alias.Ordinal);

    private static ConfiguredAlias Project(ModelAliasOptions alias, int ordinal, RuntimeRegistry runtimes)
    {
        if (!ModelAliasId.TryCreate(alias.Id, out var id))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:aliases[{ordinal}]:id '{alias.Id}' is not a valid alias identifier. Startup validation should have rejected it."));
        }

        if (!RuntimeEndpointId.TryCreate(alias.RuntimeId, out var runtimeId))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:aliases[{ordinal}]:runtimeId '{alias.RuntimeId}' is not a valid runtime identifier. Startup validation should have rejected it."));
        }

        if (!UpstreamModelId.TryCreate(alias.UpstreamModelId, out var upstreamModel))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"agentsplice:aliases[{ordinal}]:upstreamModelId '{alias.UpstreamModelId}' is not a valid model identifier. Startup validation should have rejected it."));
        }

        return ConfiguredAlias.Create(
            id,
            runtimeId,
            upstreamModel,
            alias.Enabled,
            runtimes.Find(runtimeId)?.Enabled ?? false,
            alias.Priority,
            ordinal);
    }
}
