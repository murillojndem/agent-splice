using System.Globalization;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;

namespace AgentSplice.Application.Models;

/// <summary>
/// Resolves a client-visible model identifier to a runtime and upstream model
/// (docs/SPECIFICATION.md FR-MOD-005).
/// </summary>
/// <remarks>
/// Precedence is fixed and first-match-wins: an enabled alias, then a discovered model, then the
/// configured pass-through runtime. Every step is ordinal and case-sensitive, because a client
/// echoes back the exact string <c>GET /v1/models</c> gave it.
///
/// Two properties matter more than the ordering itself. Aliases resolve without any network call, so
/// an alias-only deployment never pays for discovery on the request path. And when discovery was
/// needed but could not be performed, the result is <see cref="FailureClass.RuntimeUnavailable"/>
/// rather than <see cref="FailureClass.ModelNotFound"/>: reporting "no such model" when the truth is
/// "AgentSplice could not ask" is precisely the misleading evidence this product exists to remove.
/// </remarks>
public sealed class ModelResolver
{
    private readonly RuntimeRegistry runtimes;
    private readonly ModelAliasRegistry aliases;
    private readonly ModelCatalogueService catalogue;

    /// <summary>Creates the resolver.</summary>
    public ModelResolver(RuntimeRegistry runtimes, ModelAliasRegistry aliases, ModelCatalogueService catalogue)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(catalogue);

        this.runtimes = runtimes;
        this.aliases = aliases;
        this.catalogue = catalogue;
    }

    /// <summary>Resolves the identifier a client sent.</summary>
    public async Task<ModelResolutionOutcome> ResolveAsync(
        ClientModelId requested,
        CancellationToken cancellationToken)
    {
        if (requested.IsEmpty)
        {
            throw new ArgumentException("Resolution requires a requested model identifier.", nameof(requested));
        }

        var skippedAlias = default(string);

        if (aliases.Find(requested) is { } alias)
        {
            if (alias.IsRoutable)
            {
                return ResolveByAlias(requested, alias);
            }

            // A matching but unroutable alias does not shadow discovery: a runtime may still offer a
            // model whose name happens to equal the alias. The reason is recorded so an operator can
            // see why their alias did nothing.
            skippedAlias = alias.Enabled ? "runtime_disabled" : "disabled";
        }

        var discovered = await ResolveByDiscoveryAsync(requested, skippedAlias, cancellationToken)
            .ConfigureAwait(false);

        if (discovered is not null)
        {
            return discovered;
        }

        return ResolveByDefaultRuntime(requested, skippedAlias)
            ?? Unresolved(requested, skippedAlias);
    }

    private static ModelResolutionOutcome ResolveByAliasCore(
        ClientModelId requested,
        ConfiguredAlias alias,
        RuntimeTarget runtime) =>
        ModelResolutionOutcome.Resolved(
            ModelResolution.FromAlias(requested, alias.Id, runtime.Id, alias.UpstreamModel),
            runtime,

            // Selecting a runtime is a routing decision even when the model name is unchanged.
            routingWasApplied: true,
            SafeDetails.Create(
            [
                new KeyValuePair<string, string?>("resolution.source", "configured_alias"),
                new KeyValuePair<string, string?>("alias.id", alias.Id.Value),
                new KeyValuePair<string, string?>("runtime.id", runtime.Id.Value),
            ]));

    private ModelResolutionOutcome ResolveByAlias(ClientModelId requested, ConfiguredAlias alias)
    {
        var runtime = runtimes.Find(alias.RuntimeId);

        // ModelAliasRegistry only marks an alias routable when its runtime is enabled, so a missing
        // target here would be a broken invariant rather than a configuration state.
        return runtime is null
            ? Unresolved(requested, skippedAlias: "runtime_missing")
            : ResolveByAliasCore(requested, alias, runtime);
    }

    private async Task<ModelResolutionOutcome?> ResolveByDiscoveryAsync(
        ClientModelId requested,
        string? skippedAlias,
        CancellationToken cancellationToken)
    {
        RuntimeTarget? winner = null;
        var matches = 0;
        var winnerWasStale = false;
        var anyCatalogueConsulted = false;
        var anyCatalogueAvailable = false;

        // Configuration order, so the tie-break is stable across restarts and independent of
        // discovery timing.
        foreach (var runtime in runtimes.Enabled)
        {
            var described = await catalogue.DescribeAsync(runtime, cancellationToken).ConfigureAwait(false);

            if (described is null)
            {
                continue;
            }

            anyCatalogueConsulted = true;
            anyCatalogueAvailable |= described.IsAvailable;

            if (!Offers(described, requested))
            {
                continue;
            }

            matches++;

            if (winner is null)
            {
                winner = runtime;
                winnerWasStale = described.IsStale;
            }
        }

        if (winner is not null)
        {
            return ResolveByDiscovery(requested, winner, matches, winnerWasStale, skippedAlias);
        }

        // Nothing offered it. Whether that means "does not exist" or "could not be asked" is decided
        // by the caller, which is why this returns null rather than a failure.
        return anyCatalogueConsulted && !anyCatalogueAvailable
            ? ModelResolutionOutcome.Unresolved(
                FailureClass.RuntimeUnavailable,
                Details(skippedAlias, ("resolution.blocked", "discovery_unavailable")))
            : null;
    }

    private static ModelResolutionOutcome ResolveByDiscovery(
        ClientModelId requested,
        RuntimeTarget winner,
        int matches,
        bool winnerWasStale,
        string? skippedAlias)
    {
        var details = new List<KeyValuePair<string, string?>>
        {
            new("resolution.source", "discovered"),
            new("runtime.id", winner.Id.Value),
        };

        if (matches > 1)
        {
            // Choosing between two runtimes that both offer the identifier is a routing decision the
            // client did not make, so it has to be visible (FR-TRACE-007).
            details.Add(new KeyValuePair<string, string?>("resolution.ambiguous", "true"));
            details.Add(new KeyValuePair<string, string?>(
                "resolution.candidates",
                matches.ToString(CultureInfo.InvariantCulture)));
        }

        if (winnerWasStale)
        {
            details.Add(new KeyValuePair<string, string?>("discovery.stale", "true"));
        }

        if (skippedAlias is not null)
        {
            details.Add(new KeyValuePair<string, string?>("alias.skipped", skippedAlias));
        }

        return ModelResolutionOutcome.Resolved(
            ModelResolution.FromDiscovery(requested, winner.Id, UpstreamModelId.Create(requested.Value)),
            winner,
            routingWasApplied: matches > 1,
            SafeDetails.Create(details));
    }

    private ModelResolutionOutcome? ResolveByDefaultRuntime(ClientModelId requested, string? skippedAlias)
    {
        if (runtimes.DefaultRuntime is not { } fallback)
        {
            return null;
        }

        return ModelResolutionOutcome.Resolved(
            ModelResolution.PassThrough(requested, fallback.Id, UpstreamModelId.Create(requested.Value)),
            fallback,

            // The client named no runtime, so AgentSplice picked one.
            routingWasApplied: true,
            Details(skippedAlias, ("resolution.source", "pass_through"), ("runtime.id", fallback.Id.Value)));
    }

    private static ModelResolutionOutcome Unresolved(ClientModelId requested, string? skippedAlias) =>
        ModelResolutionOutcome.Unresolved(FailureClass.ModelNotFound, Details(skippedAlias));

    private static bool Offers(RuntimeCatalogue catalogue, ClientModelId requested)
    {
        foreach (var model in catalogue.Models)
        {
            if (string.Equals(model.Id.Value, requested.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static SafeDetails Details(string? skippedAlias, params (string Key, string Value)[] entries)
    {
        var accumulated = entries
            .Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))
            .ToList();

        if (skippedAlias is not null)
        {
            accumulated.Add(new KeyValuePair<string, string?>("alias.skipped", skippedAlias));
        }

        return accumulated.Count == 0 ? SafeDetails.Empty : SafeDetails.Create(accumulated);
    }
}
