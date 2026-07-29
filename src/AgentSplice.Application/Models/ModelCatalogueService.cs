using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Models;

/// <summary>
/// Composes the client-visible model catalogue from configured aliases and discovered models
/// (docs/SPECIFICATION.md FR-MOD-002, FR-MOD-004).
/// </summary>
/// <remarks>
/// Duplicate client-visible identifiers are resolved to a single entry here, and the winner is
/// chosen deterministically. FR-MOD-004 requires duplicates to be disambiguated <em>internally</em>
/// by runtime endpoint ID, which is why the key used for reasoning is the runtime/model pair while
/// the identifier a client sees stays the bare model name. A composite <c>runtime/model</c>
/// identifier would be a value AgentSplice invented, and every client that copies a listed
/// identifier straight back into <c>model</c> would then send something no runtime recognises.
/// </remarks>
public sealed class ModelCatalogueService
{
    private readonly RuntimeRegistry runtimes;
    private readonly ModelAliasRegistry aliases;
    private readonly ModelRuntimeProviderRegistry providers;
    private readonly ModelDiscoveryCache cache;

    /// <summary>Creates the composer.</summary>
    public ModelCatalogueService(
        RuntimeRegistry runtimes,
        ModelAliasRegistry aliases,
        ModelRuntimeProviderRegistry providers,
        ModelDiscoveryCache cache)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(cache);

        this.runtimes = runtimes;
        this.aliases = aliases;
        this.providers = providers;
        this.cache = cache;
    }

    /// <summary>Builds the catalogue a client sees.</summary>
    public async Task<ModelCatalogueResult> ComposeAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<ModelCatalogueEntry>();
        var outcomes = new List<RuntimeDiscoveryOutcome>();

        foreach (var runtime in runtimes.Enabled)
        {
            var catalogue = await DescribeAsync(runtime, outcomes, cancellationToken).ConfigureAwait(false);

            // Aliases are configuration, so they are offered whether or not discovery ran. A runtime
            // with discovery switched off is still fully usable through its aliases.
            AddAliasEntries(runtime, catalogue, candidates);

            if (catalogue is not null)
            {
                AddDiscoveredEntries(runtime, catalogue, candidates);
            }
        }

        return ModelCatalogueResult.Create(Deduplicate(candidates), outcomes);
    }

    /// <summary>
    /// Returns a runtime's catalogue, refreshing it if needed, or <c>null</c> when discovery is
    /// switched off for that runtime or no provider module serves it.
    /// </summary>
    public async Task<RuntimeCatalogue?> DescribeAsync(RuntimeTarget runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtime.Discovery.Enabled)
        {
            return null;
        }

        var provider = providers.Find(runtime);

        return provider is null
            ? null
            : await cache.GetAsync(runtime, provider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeCatalogue?> DescribeAsync(
        RuntimeTarget runtime,
        List<RuntimeDiscoveryOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (!runtime.Discovery.Enabled)
        {
            // Not consulted at all, so there is no outcome to report. Recording one would let a
            // deliberately discovery-free deployment look like a total discovery failure.
            return null;
        }

        if (providers.Find(runtime) is null)
        {
            outcomes.Add(RuntimeDiscoveryOutcome.ProviderMissing(runtime.Id));
            return null;
        }

        var catalogue = await DescribeAsync(runtime, cancellationToken).ConfigureAwait(false);

        if (catalogue is not null)
        {
            outcomes.Add(RuntimeDiscoveryOutcome.From(catalogue));
        }

        return catalogue;
    }

    private void AddAliasEntries(
        RuntimeTarget runtime,
        RuntimeCatalogue? catalogue,
        List<ModelCatalogueEntry> candidates)
    {
        foreach (var alias in aliases.ForRuntime(runtime.Id))
        {
            if (!alias.IsRoutable)
            {
                continue;
            }

            // When the alias targets a model the runtime actually reported, its creation time and
            // owner are real evidence and are inherited. Otherwise they stay unknown rather than
            // being invented.
            var target = Find(catalogue, alias.UpstreamModel);

            candidates.Add(ModelCatalogueEntry.FromAlias(
                alias,
                runtime.Ordinal,
                catalogue?.IsAvailable is not false,
                target?.Created,
                target?.OwnedBy));
        }
    }

    private static void AddDiscoveredEntries(
        RuntimeTarget runtime,
        RuntimeCatalogue catalogue,
        List<ModelCatalogueEntry> candidates)
    {
        foreach (var model in catalogue.Models)
        {
            candidates.Add(ModelCatalogueEntry.FromDiscovery(
                model,
                runtime.Id,
                runtime.Ordinal,

                // A stale catalogue describes models we cannot currently confirm are loadable.
                reachable: !catalogue.IsStale));
        }
    }

    private static DiscoveredModel? Find(RuntimeCatalogue? catalogue, UpstreamModelId model)
    {
        if (catalogue is null)
        {
            return null;
        }

        foreach (var candidate in catalogue.Models)
        {
            if (string.Equals(candidate.Id.Value, model.Value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Collapses candidates that share a client-visible identifier, deterministically.
    /// </summary>
    /// <remarks>
    /// The order is: a configured alias beats a discovered model, because an operator's explicit
    /// mapping is a stronger statement than a runtime's inventory; then earlier configuration order,
    /// which is the only operator-visible preference signal that exists and is stable across
    /// restarts; then alias priority and declaration order as final tie-breaks. Nothing here depends
    /// on discovery timing or on dictionary enumeration order.
    /// </remarks>
    private static List<ModelCatalogueEntry> Deduplicate(List<ModelCatalogueEntry> candidates)
    {
        var winners = new Dictionary<string, ModelCatalogueEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var candidate in candidates)
        {
            var key = candidate.ClientModel.Value;

            if (!winners.TryGetValue(key, out var incumbent))
            {
                winners.Add(key, candidate);
                order.Add(key);
                continue;
            }

            if (Prefer(candidate, incumbent) < 0)
            {
                winners[key] = candidate;
            }
        }

        return [.. order.Select(key => winners[key])];
    }

    private static int Prefer(ModelCatalogueEntry candidate, ModelCatalogueEntry incumbent)
    {
        var bySource = Rank(candidate.Source).CompareTo(Rank(incumbent.Source));

        if (bySource != 0)
        {
            return bySource;
        }

        var byRuntime = candidate.RuntimeOrdinal.CompareTo(incumbent.RuntimeOrdinal);

        if (byRuntime != 0)
        {
            return byRuntime;
        }

        var byPriority = candidate.AliasPriority.CompareTo(incumbent.AliasPriority);

        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(candidate.Runtime.Value, incumbent.Runtime.Value);
    }

    private static int Rank(Domain.Exchanges.ModelResolutionSource source) => source switch
    {
        Domain.Exchanges.ModelResolutionSource.ConfiguredAlias => 0,
        Domain.Exchanges.ModelResolutionSource.Discovered => 1,
        _ => 2,
    };
}
