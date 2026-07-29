using System.Collections.Concurrent;
using AgentSplice.Application.Runtimes;

namespace AgentSplice.Application.Models;

/// <summary>
/// Per-runtime model catalogue cache with a configurable window and stale-serve policy
/// (docs/SPECIFICATION.md FR-MOD-003).
/// </summary>
/// <remarks>
/// Two behaviours here are load-bearing rather than optimisations.
///
/// Refreshes are coalesced per runtime. Model resolution can trigger a refresh from the completion
/// path, so a burst of requests arriving on a cold cache would otherwise open one upstream
/// connection each. One in-flight refresh per runtime is what keeps a cache miss from turning into a
/// stampede against a local runtime that is already the bottleneck.
///
/// A failed refresh is remembered for the same window as a successful one. Without that, every
/// request naming an unknown model would wait out the connect timeout again while a runtime is down,
/// turning one unreachable endpoint into latency on every request. The cost is that a recovered
/// runtime is not noticed until the window elapses, which is the same trade the window already makes
/// for a changed catalogue.
/// </remarks>
public sealed class ModelDiscoveryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshGates = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    /// <summary>Creates a cache driven by the supplied clock.</summary>
    public ModelDiscoveryCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the runtime's catalogue, refreshing it when the current entry is outside its window.
    /// </summary>
    /// <param name="target">The runtime to describe.</param>
    /// <param name="provider">The provider that speaks to that runtime.</param>
    /// <param name="cancellationToken">Cancels a refresh; the caller's token is always the root.</param>
    public async Task<RuntimeCatalogue> GetAsync(
        RuntimeTarget target,
        IModelRuntimeProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        if (TryUseCurrentEntry(target, out var current))
        {
            return current;
        }

        var gate = refreshGates.GetOrAdd(target.Id.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A concurrent caller may have refreshed while this one waited, which is the whole
            // point of the gate.
            if (TryUseCurrentEntry(target, out var refreshedByAnother))
            {
                return refreshedByAnother;
            }

            return await RefreshAsync(target, provider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The catalogue held for a runtime without contacting it, or <c>null</c> when none is held.</summary>
    /// <remarks>
    /// Reports what is already known, so an administrative or health surface can describe the cache
    /// without provoking upstream traffic as a side effect of being observed.
    /// </remarks>
    public RuntimeCatalogue? PeekStored(RuntimeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return entries.TryGetValue(target.Id.Value, out var entry) && entry.Models is { } models
            ? RuntimeCatalogue.Fresh(target.Id, models, entry.RetrievedAt!.Value)
            : null;
    }

    private bool TryUseCurrentEntry(RuntimeTarget target, out RuntimeCatalogue catalogue)
    {
        catalogue = null!;

        if (!entries.TryGetValue(target.Id.Value, out var entry))
        {
            return false;
        }

        var window = target.Discovery.CacheDuration;

        // A zero window means "never reuse", so it must not be satisfiable by any entry.
        if (window <= TimeSpan.Zero)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        if (entry.Models is { } models && entry.RetrievedAt is { } retrievedAt && now - retrievedAt < window)
        {
            catalogue = RuntimeCatalogue.Fresh(target.Id, models, retrievedAt);
            return true;
        }

        if (entry.Failure is { } failure && entry.FailedAt is { } failedAt && now - failedAt < window)
        {
            catalogue = Degrade(target, entry, failure);
            return true;
        }

        return false;
    }

    private async Task<RuntimeCatalogue> RefreshAsync(
        RuntimeTarget target,
        IModelRuntimeProvider provider,
        CancellationToken cancellationToken)
    {
        var result = await provider.ListModelsAsync(target, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        if (result.Succeeded)
        {
            entries[target.Id.Value] = CacheEntry.Retrieved(result.Models, now);
            return RuntimeCatalogue.Fresh(target.Id, result.Models, now);
        }

        var failure = result.Failure!;

        // A cancelled refresh is not evidence about the runtime, so it must not overwrite a good
        // entry with a failure or start a negative-cache window on our own impatience.
        if (failure.Reason == UpstreamFailureReason.Cancelled)
        {
            return entries.TryGetValue(target.Id.Value, out var untouched) && untouched.Models is { } models
                ? RuntimeCatalogue.Stale(target.Id, models, untouched.RetrievedAt!.Value, failure)
                : RuntimeCatalogue.Unavailable(target.Id, failure);
        }

        var entry = entries.AddOrUpdate(
            target.Id.Value,
            _ => CacheEntry.Failed(failure, now),
            (_, existing) => existing.WithFailure(failure, now));

        return Degrade(target, entry, failure);
    }

    /// <summary>Decides what to serve once a refresh has failed.</summary>
    private static RuntimeCatalogue Degrade(RuntimeTarget target, CacheEntry entry, UpstreamFailure failure) =>
        target.Discovery.ServeStaleOnFailure && entry.Models is { } models && entry.RetrievedAt is { } retrievedAt
            ? RuntimeCatalogue.Stale(target.Id, models, retrievedAt, failure)
            : RuntimeCatalogue.Unavailable(target.Id, failure);

    /// <summary>
    /// What is remembered per runtime. Success and failure are held side by side so that a failed
    /// refresh never destroys the catalogue it failed to replace.
    /// </summary>
    private sealed record CacheEntry(
        IReadOnlyList<DiscoveredModel>? Models,
        DateTimeOffset? RetrievedAt,
        UpstreamFailure? Failure,
        DateTimeOffset? FailedAt)
    {
        internal static CacheEntry Retrieved(IReadOnlyList<DiscoveredModel> models, DateTimeOffset at) =>
            new(models, at, Failure: null, FailedAt: null);

        internal static CacheEntry Failed(UpstreamFailure failure, DateTimeOffset at) =>
            new(Models: null, RetrievedAt: null, failure, at);

        internal CacheEntry WithFailure(UpstreamFailure failure, DateTimeOffset at) =>
            this with { Failure = failure, FailedAt = at };
    }
}
