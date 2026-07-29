using AgentSplice.Application.Configuration;
using AgentSplice.IntegrationTests.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// Host startup and configuration binding.
/// </summary>
/// <remarks>
/// Stage 0 exposes no HTTP surface, so what these tests prove is narrower and more important than
/// "the app responds": the shipped defaults bind to the intended values, and an invalid deployment
/// refuses to start instead of failing later on a client request (NFR 14.2).
/// </remarks>
public sealed class HostStartupTests
{
    [Fact]
    public void The_host_starts_with_the_shipped_configuration()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var options = Resolve(factory);

        Assert.NotNull(options);
    }

    [Fact]
    public void The_shipped_configuration_keeps_content_capture_disabled()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var options = Resolve(factory);

        Assert.False(options.Capture.ContentEnabled);
        Assert.False(options.Diagnostics.StoreBodies);
        Assert.True(options.Capture.MetadataEnabled);
    }

    [Fact]
    public void The_shipped_configuration_keeps_adapters_and_capability_probing_disabled()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var options = Resolve(factory);

        Assert.False(options.Adapters.Enabled);
        Assert.All(options.Runtimes, runtime => Assert.False(runtime.Discovery.CapabilityProbingEnabled));
    }

    [Fact]
    public void The_shipped_configuration_binds_the_local_lm_studio_runtime()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var runtime = Assert.Single(Resolve(factory).Runtimes);

        Assert.Equal("lmstudio-local", runtime.Id);
        Assert.Equal("lmstudio", runtime.Provider);
        Assert.Equal("http://127.0.0.1:1234/v1", runtime.BaseUrl);
        Assert.Equal("LM_STUDIO_API_KEY", runtime.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void The_shipped_configuration_binds_all_four_timeout_phases()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var timeouts = Assert.Single(Resolve(factory).Runtimes).Timeouts;

        Assert.Equal(TimeSpan.FromSeconds(5), timeouts.Connect);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.ResponseHeaders);
        Assert.Equal(TimeSpan.FromMinutes(1), timeouts.IdleStream);
        Assert.Equal(TimeSpan.FromMinutes(10), timeouts.Total);
    }

    [Fact]
    public void The_shipped_configuration_binds_the_retention_windows()
    {
        using var factory = new AgentSpliceApplicationFactory();

        var retention = Resolve(factory).Capture.Retention;

        Assert.Equal(TimeSpan.FromDays(30), retention.Metadata);
        Assert.Equal(TimeSpan.FromDays(1), retention.Content);
    }

    [Fact]
    public void The_shipped_configuration_binds_loopback_only_defaults()
    {
        using var factory = new AgentSpliceApplicationFactory();

        Assert.Equal("http://127.0.0.1:5280", Resolve(factory).PublicBaseUrl);
    }

    [Fact]
    public void A_time_provider_is_registered_so_timing_logic_never_reads_the_clock_statically()
    {
        using var factory = new AgentSpliceApplicationFactory();

        Assert.NotNull(factory.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Enabling_adapters_prevents_startup()
    {
        AssertStartupFails(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:adapters:enabled"] = "true",
            },
            "Stage 4 capability");
    }

    [Fact]
    public void An_alias_pointing_at_an_unconfigured_runtime_prevents_startup()
    {
        AssertStartupFails(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:aliases:0:id"] = "local-coder",
                ["agentsplice:aliases:0:runtimeId"] = "does-not-exist",
                ["agentsplice:aliases:0:upstreamModelId"] = "qwen3.6-27b-mtp",
            },
            "does not match any configured runtime");
    }

    [Fact]
    public void An_alias_cycle_prevents_startup()
    {
        AssertStartupFails(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:aliases:0:id"] = "first",
                ["agentsplice:aliases:0:runtimeId"] = "lmstudio-local",
                ["agentsplice:aliases:0:upstreamModelId"] = "second",
                ["agentsplice:aliases:1:id"] = "second",
                ["agentsplice:aliases:1:runtimeId"] = "lmstudio-local",
                ["agentsplice:aliases:1:upstreamModelId"] = "first",
            },
            "resolution cycle");
    }

    [Fact]
    public void A_runtime_base_url_with_embedded_credentials_prevents_startup()
    {
        AssertStartupFails(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:runtimes:0:baseUrl"] = "http://user:secret@127.0.0.1:1234/v1",
            },
            "must not embed credentials");
    }

    [Fact]
    public void A_valid_alias_chain_does_not_prevent_startup()
    {
        using var factory = new AgentSpliceApplicationFactory(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:aliases:0:id"] = "local-coder",
                ["agentsplice:aliases:0:runtimeId"] = "lmstudio-local",
                ["agentsplice:aliases:0:upstreamModelId"] = "qwen3.6-27b-mtp",
            });

        var alias = Assert.Single(Resolve(factory).Aliases);

        Assert.Equal("local-coder", alias.Id);
    }

    private static AgentSpliceOptions Resolve(AgentSpliceApplicationFactory factory)
    {
        // Creating a client boots the host, which is what triggers startup validation.
        using var client = factory.CreateClient();

        return factory.Services.GetRequiredService<IOptions<AgentSpliceOptions>>().Value;
    }

    private static void AssertStartupFails(
        IReadOnlyDictionary<string, string?> overrides,
        string expectedFragment)
    {
        using var factory = new AgentSpliceApplicationFactory(overrides);

        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);

        var messages = Flatten(exception).ToArray();

        Assert.Contains(
            messages,
            message => message.Contains(expectedFragment, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Flatten(Exception? exception)
    {
        while (exception is not null)
        {
            yield return exception.Message;

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }

            exception = exception.InnerException;
        }
    }
}
