using AgentSplice.Application.Configuration;
using AgentSplice.Application.Models;
using AgentSplice.Application.Runtimes;
using AgentSplice.UnitTests.Observability;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentSplice.UnitTests.Models;

/// <summary>
/// Assembles the real catalogue and resolution graph over a scripted provider.
/// </summary>
/// <remarks>
/// The registries, the cache, the composer, and the resolver are the production types. Only the
/// provider is a double, so the precedence rules under test are the ones that actually run.
/// </remarks>
internal sealed class CatalogueFixture
{
    private CatalogueFixture(
        ModelResolver resolver,
        ModelCatalogueService catalogue,
        RecordingModelRuntimeProvider provider,
        FakeTimeProvider clock)
    {
        Resolver = resolver;
        Catalogue = catalogue;
        Provider = provider;
        Clock = clock;
    }

    internal ModelResolver Resolver { get; }

    internal ModelCatalogueService Catalogue { get; }

    internal RecordingModelRuntimeProvider Provider { get; }

    internal FakeTimeProvider Clock { get; }

    internal static Builder Build() => new();

    internal sealed class Builder
    {
        private readonly AgentSpliceOptions options = new();
        private readonly RecordingModelRuntimeProvider provider = new();
        private bool registerProvider = true;

        internal Builder Runtime(
            string id,
            bool enabled = true,
            bool discoveryEnabled = true,
            bool serveStaleOnFailure = true,
            TimeSpan? cacheDuration = null)
        {
            options.Runtimes.Add(new RuntimeEndpointOptions
            {
                Id = id,
                Provider = "lmstudio",
                BaseUrl = "http://127.0.0.1:1234/v1",
                Enabled = enabled,
                Discovery = new DiscoveryOptions
                {
                    Enabled = discoveryEnabled,
                    ServeStaleOnFailure = serveStaleOnFailure,
                    CacheDuration = cacheDuration ?? TimeSpan.FromSeconds(30),
                },
            });

            return this;
        }

        internal Builder Alias(
            string id,
            string runtimeId,
            string upstreamModelId,
            bool enabled = true,
            int priority = 0)
        {
            options.Aliases.Add(new ModelAliasOptions
            {
                Id = id,
                RuntimeId = runtimeId,
                UpstreamModelId = upstreamModelId,
                Enabled = enabled,
                Priority = priority,
            });

            return this;
        }

        internal Builder DefaultRuntime(string runtimeId)
        {
            options.DefaultRuntimeId = runtimeId;
            return this;
        }

        internal Builder Offers(string runtimeId, params string[] modelIds)
        {
            provider.ForRuntime(
                runtimeId,
                RuntimeModelListResult.Success(
                    modelIds.Select(id => RecordingModelRuntimeProvider.Model(id)).ToArray()));

            return this;
        }

        internal Builder OffersWithMetadata(string runtimeId, string modelId, long? created, string? ownedBy)
        {
            provider.ForRuntime(
                runtimeId,
                RuntimeModelListResult.Success([RecordingModelRuntimeProvider.Model(modelId, created, ownedBy)]));

            return this;
        }

        internal Builder Fails(string runtimeId, UpstreamFailureReason reason = UpstreamFailureReason.Unreachable)
        {
            provider.ForRuntime(runtimeId, RuntimeModelListResult.Failed(UpstreamFailure.Create(reason)));
            return this;
        }

        internal Builder WithNoProviderModule()
        {
            registerProvider = false;
            return this;
        }

        internal CatalogueFixture Create()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
            var accessor = Options.Create(options);
            var runtimes = new RuntimeRegistry(accessor);
            var aliases = new ModelAliasRegistry(accessor, runtimes);
            var providers = new ModelRuntimeProviderRegistry(
                registerProvider ? [provider] : Array.Empty<IModelRuntimeProvider>());
            var catalogue = new ModelCatalogueService(
                runtimes,
                aliases,
                providers,
                new ModelDiscoveryCache(clock, new RecordingExchangeTelemetry()));

            return new CatalogueFixture(
                new ModelResolver(runtimes, aliases, catalogue),
                catalogue,
                provider,
                clock);
        }
    }
}
