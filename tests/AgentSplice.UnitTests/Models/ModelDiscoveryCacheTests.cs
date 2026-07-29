using AgentSplice.Application.Models;
using AgentSplice.Application.Runtimes;
using AgentSplice.UnitTests.Runtimes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Models;

/// <summary>
/// Catalogue caching, the stale-serve policy, and refresh coalescing
/// (docs/SPECIFICATION.md FR-MOD-003).
/// </summary>
/// <remarks>
/// Every timing assertion is driven by <see cref="FakeTimeProvider"/>, so the window is tested
/// exactly rather than approximately, and the suite never sleeps.
/// </remarks>
public sealed class ModelDiscoveryCacheTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_cold_cache_contacts_the_runtime()
    {
        var (cache, provider, target) = Fixture();

        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.False(catalogue.IsStale);
        Assert.True(catalogue.IsAvailable);
        Assert.Equal(["qwen3.6-27b-mtp"], catalogue.Models.Select(model => model.Id.Value));
    }

    [Fact]
    public async Task A_second_request_inside_the_window_does_not_contact_the_runtime()
    {
        var (cache, provider, target) = Fixture(out var clock);

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window - TimeSpan.FromTicks(1));
        await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task A_request_at_the_window_boundary_refreshes()
    {
        var (cache, provider, target) = Fixture(out var clock);

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task A_zero_window_never_reuses_an_entry()
    {
        var (cache, provider, target) = Fixture(cacheDuration: TimeSpan.Zero);

        await cache.GetAsync(target, provider, CancellationToken.None);
        await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task A_failed_refresh_serves_the_previous_catalogue_when_stale_serving_is_enabled()
    {
        var (cache, provider, target) = Fixture(out var clock);
        provider.ThenAnswers(RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model("qwen3.6-27b-mtp")]))
            .Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)));

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.True(catalogue.IsStale);
        Assert.True(catalogue.IsAvailable);
        Assert.Equal(["qwen3.6-27b-mtp"], catalogue.Models.Select(model => model.Id.Value));
        Assert.Equal(UpstreamFailureReason.Unreachable, catalogue.Failure?.Reason);
    }

    [Fact]
    public async Task A_failed_refresh_yields_nothing_when_stale_serving_is_disabled()
    {
        var (cache, provider, target) = Fixture(out var clock, serveStaleOnFailure: false);
        provider.ThenAnswers(RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model("a")]))
            .Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)));

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.False(catalogue.IsAvailable);
        Assert.Empty(catalogue.Models);
    }

    [Fact]
    public async Task A_failure_with_no_previous_catalogue_is_unavailable_rather_than_empty()
    {
        // "Unavailable" and "answered with no models" must stay distinguishable: one is a 502 and
        // the other is an honest empty list.
        var (cache, provider, target) = Fixture();
        provider.Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)));

        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.False(catalogue.IsAvailable);
        Assert.NotNull(catalogue.Failure);
    }

    [Fact]
    public async Task An_empty_catalogue_is_available_rather_than_unavailable()
    {
        var (cache, provider, target) = Fixture();
        provider.Answers(RuntimeModelListResult.Success([]));

        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.True(catalogue.IsAvailable);
        Assert.Empty(catalogue.Models);
        Assert.Null(catalogue.Failure);
    }

    [Fact]
    public async Task A_failed_refresh_is_not_retried_inside_the_window()
    {
        // Otherwise every request naming an unknown model waits out the connect timeout again while
        // a runtime is down.
        var (cache, provider, target) = Fixture(out var clock);
        provider.Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)));

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window - TimeSpan.FromTicks(1));
        await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task A_recovered_runtime_is_noticed_once_the_window_elapses()
    {
        var (cache, provider, target) = Fixture(out var clock);
        provider.ThenAnswers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)))
            .Answers(RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model("recovered")]));

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.True(catalogue.IsAvailable);
        Assert.False(catalogue.IsStale);
        Assert.Equal(["recovered"], catalogue.Models.Select(model => model.Id.Value));
    }

    [Fact]
    public async Task A_failed_refresh_does_not_destroy_the_catalogue_it_failed_to_replace()
    {
        var (cache, provider, target) = Fixture(out var clock);
        provider.ThenAnswers(RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model("kept")]))
            .ThenAnswers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)))
            .Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Unreachable)));

        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        await cache.GetAsync(target, provider, CancellationToken.None);
        clock.Advance(Window);
        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(["kept"], catalogue.Models.Select(model => model.Id.Value));
    }

    [Fact]
    public async Task Concurrent_requests_on_a_cold_cache_trigger_one_refresh()
    {
        var (cache, provider, target) = Fixture();
        provider.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(target, provider, CancellationToken.None))
            .ToArray();

        provider.Gate.SetResult();
        var catalogues = await Task.WhenAll(requests);

        Assert.Equal(1, provider.CallCount);
        Assert.All(catalogues, catalogue => Assert.True(catalogue.IsAvailable));
    }

    [Fact]
    public async Task A_cancelled_refresh_does_not_start_a_negative_cache_window()
    {
        // Our own impatience is not evidence about the runtime, so the next caller must still try.
        var (cache, provider, target) = Fixture();
        provider.Answers(RuntimeModelListResult.Failed(UpstreamFailure.Create(UpstreamFailureReason.Cancelled)));

        await cache.GetAsync(target, provider, CancellationToken.None);
        provider.Answers(RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model("later")]));
        var catalogue = await cache.GetAsync(target, provider, CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(["later"], catalogue.Models.Select(model => model.Id.Value));
    }

    [Fact]
    public async Task Each_runtime_is_cached_independently()
    {
        var clock = new FakeTimeProvider(Origin);
        var cache = new ModelDiscoveryCache(clock);
        var provider = new RecordingModelRuntimeProvider();
        var first = RuntimeTargetTests.Target("http://127.0.0.1:1234/v1", "first");
        var second = RuntimeTargetTests.Target("http://127.0.0.1:5678/v1", "second", ordinal: 1);

        await cache.GetAsync(first, provider, CancellationToken.None);
        await cache.GetAsync(second, provider, CancellationToken.None);
        await cache.GetAsync(first, provider, CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public void Peeking_reports_nothing_before_a_runtime_has_been_contacted()
    {
        var (cache, _, target) = Fixture();

        Assert.Null(cache.PeekStored(target));
    }

    [Fact]
    public async Task Peeking_reports_a_stored_catalogue_without_contacting_the_runtime()
    {
        var (cache, provider, target) = Fixture();

        await cache.GetAsync(target, provider, CancellationToken.None);
        var peeked = cache.PeekStored(target);

        Assert.NotNull(peeked);
        Assert.Equal(1, provider.CallCount);
    }

    private static (ModelDiscoveryCache Cache, RecordingModelRuntimeProvider Provider, RuntimeTarget Target) Fixture(
        TimeSpan? cacheDuration = null,
        bool serveStaleOnFailure = true) =>
        Fixture(out _, cacheDuration, serveStaleOnFailure);

    private static (ModelDiscoveryCache Cache, RecordingModelRuntimeProvider Provider, RuntimeTarget Target) Fixture(
        out FakeTimeProvider clock,
        TimeSpan? cacheDuration = null,
        bool serveStaleOnFailure = true)
    {
        clock = new FakeTimeProvider(Origin);

        var target = RuntimeTargetTests.Target(
            "http://127.0.0.1:1234/v1",
            discovery: RuntimeTargetTests.Discovery(
                cacheDuration: cacheDuration ?? Window,
                serveStaleOnFailure: serveStaleOnFailure));

        return (new ModelDiscoveryCache(clock), new RecordingModelRuntimeProvider(), target);
    }
}
