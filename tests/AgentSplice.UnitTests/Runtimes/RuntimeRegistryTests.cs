using AgentSplice.Application.Configuration;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// Projection of the configuration tree into ordered runtime targets.
/// </summary>
/// <remarks>
/// The ordinal assigned here is what makes the FR-MOD-004 duplicate tie-break deterministic, so the
/// ordering assertions are load-bearing rather than incidental.
/// </remarks>
public sealed class RuntimeRegistryTests
{
    [Fact]
    public void Runtimes_keep_their_configuration_order_as_their_ordinal()
    {
        var registry = Registry(Runtime("first"), Runtime("second"), Runtime("third"));

        Assert.Equal(["first", "second", "third"], registry.All.Select(target => target.Id.Value));
        Assert.Equal([0, 1, 2], registry.All.Select(target => target.Ordinal));
    }

    [Fact]
    public void A_disabled_runtime_is_configured_but_does_not_participate_in_routing()
    {
        var registry = Registry(Runtime("first"), Runtime("second", enabled: false));

        Assert.Equal(2, registry.All.Count);
        Assert.Equal(["first"], registry.Enabled.Select(target => target.Id.Value));
    }

    [Fact]
    public void A_disabled_runtime_keeps_the_ordinal_of_its_configured_position()
    {
        // Ordinals index configuration, not the enabled subset: compacting them would make the
        // tie-break shift when an unrelated runtime is switched off.
        var registry = Registry(Runtime("first", enabled: false), Runtime("second"));

        Assert.Equal(1, registry.Enabled.Single().Ordinal);
    }

    [Fact]
    public void A_runtime_can_be_found_by_identifier_whether_or_not_it_is_enabled()
    {
        var registry = Registry(Runtime("first", enabled: false));

        Assert.NotNull(registry.Find(RuntimeEndpointId.Create("first")));
    }

    [Fact]
    public void An_unknown_identifier_finds_nothing()
    {
        var registry = Registry(Runtime("first"));

        Assert.Null(registry.Find(RuntimeEndpointId.Create("absent")));
    }

    [Fact]
    public void Identifier_lookup_uses_the_normalised_lower_case_form()
    {
        var registry = Registry(Runtime("LMStudio-Local"));

        Assert.NotNull(registry.Find(RuntimeEndpointId.Create("lmstudio-local")));
    }

    [Fact]
    public void No_default_runtime_is_exposed_when_none_is_configured()
    {
        Assert.Null(Registry(Runtime("first")).DefaultRuntime);
    }

    [Fact]
    public void A_configured_default_runtime_is_exposed()
    {
        var registry = Registry(new[] { Runtime("first"), Runtime("second") }, defaultRuntimeId: "second");

        Assert.Equal("second", registry.DefaultRuntime?.Id.Value);
    }

    [Fact]
    public void A_disabled_default_runtime_is_not_exposed()
    {
        // Startup validation rejects this configuration; the registry stays defensive so that a
        // pass-through can never be routed to a runtime that is switched off.
        var registry = Registry(new[] { Runtime("first", enabled: false) }, defaultRuntimeId: "first");

        Assert.Null(registry.DefaultRuntime);
    }

    [Fact]
    public void Discovery_and_timeout_policy_are_projected_from_configuration()
    {
        var runtime = Runtime("first");
        runtime.Discovery.CacheDuration = TimeSpan.FromSeconds(90);
        runtime.Discovery.ServeStaleOnFailure = false;
        runtime.Timeouts.Connect = TimeSpan.FromSeconds(3);

        var target = Registry(runtime).All.Single();

        Assert.Equal(TimeSpan.FromSeconds(90), target.Discovery.CacheDuration);
        Assert.False(target.Discovery.ServeStaleOnFailure);
        Assert.Equal(TimeSpan.FromSeconds(3), target.Timeouts.Connect);
    }

    [Fact]
    public void An_empty_configuration_produces_an_empty_registry()
    {
        var registry = Registry();

        Assert.Empty(registry.All);
        Assert.Empty(registry.Enabled);
        Assert.Null(registry.DefaultRuntime);
    }

    internal static RuntimeEndpointOptions Runtime(
        string id,
        bool enabled = true,
        bool discoveryEnabled = true,
        string baseUrl = "http://127.0.0.1:1234/v1") => new()
        {
            Id = id,
            Provider = "lmstudio",
            BaseUrl = baseUrl,
            Enabled = enabled,
            Discovery = new DiscoveryOptions { Enabled = discoveryEnabled },
        };

    internal static RuntimeRegistry Registry(params RuntimeEndpointOptions[] runtimes) =>
        Registry(runtimes, defaultRuntimeId: null);

    internal static RuntimeRegistry Registry(
        IEnumerable<RuntimeEndpointOptions> runtimes,
        string? defaultRuntimeId)
    {
        var options = new AgentSpliceOptions { DefaultRuntimeId = defaultRuntimeId };

        foreach (var runtime in runtimes)
        {
            options.Runtimes.Add(runtime);
        }

        return new RuntimeRegistry(Options.Create(options));
    }
}
