using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// Projection of a configured runtime into the shape the request path uses.
/// </summary>
public sealed class RuntimeTargetTests
{
    [Fact]
    public void A_base_address_without_a_trailing_slash_gains_one()
    {
        // Without this, Uri resolution treats "/v1" as a document name and replaces it, so every
        // discovery and completion request would be sent to the wrong path.
        var target = Target("http://127.0.0.1:1234/v1");

        Assert.Equal("http://127.0.0.1:1234/v1/", target.BaseAddress.AbsoluteUri);
    }

    [Fact]
    public void A_base_address_that_already_ends_in_a_slash_is_unchanged()
    {
        Assert.Equal("http://127.0.0.1:1234/v1/", Target("http://127.0.0.1:1234/v1/").BaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("/models")]
    public void A_relative_path_resolves_beneath_the_configured_prefix(string relativePath)
    {
        var target = Target("http://127.0.0.1:1234/v1");

        Assert.Equal("http://127.0.0.1:1234/v1/models", target.ResolvePath(relativePath).AbsoluteUri);
    }

    [Fact]
    public void A_nested_prefix_is_preserved_when_resolving_a_path()
    {
        var target = Target("http://gateway.internal/proxy/lmstudio/v1");

        Assert.Equal(
            "http://gateway.internal/proxy/lmstudio/v1/chat/completions",
            target.ResolvePath("chat/completions").AbsoluteUri);
    }

    [Fact]
    public void A_target_carries_the_api_key_variable_name_and_never_a_value()
    {
        var target = Target("http://127.0.0.1:1234/v1", apiKeyEnvironmentVariable: "LM_STUDIO_API_KEY");

        Assert.Equal("LM_STUDIO_API_KEY", target.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void A_relative_base_address_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RuntimeTarget.Create(
            RuntimeEndpointId.Create("lmstudio-local"),
            "lmstudio",
            new Uri("/v1", UriKind.Relative),
            Discovery(),
            Timeouts(),
            ordinal: 0));
    }

    [Fact]
    public void A_negative_ordinal_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeTarget.Create(
            RuntimeEndpointId.Create("lmstudio-local"),
            "lmstudio",
            new Uri("http://127.0.0.1:1234/v1", UriKind.Absolute),
            Discovery(),
            Timeouts(),
            ordinal: -1));
    }

    internal static RuntimeTarget Target(
        string baseUrl,
        string id = "lmstudio-local",
        int ordinal = 0,
        bool enabled = true,
        string? apiKeyEnvironmentVariable = null,
        RuntimeDiscoveryPolicy? discovery = null) =>
        RuntimeTarget.Create(
            RuntimeEndpointId.Create(id),
            "lmstudio",
            new Uri(baseUrl, UriKind.Absolute),
            discovery ?? Discovery(),
            Timeouts(),
            ordinal,
            enabled,
            apiKeyEnvironmentVariable);

    internal static RuntimeDiscoveryPolicy Discovery(
        bool enabled = true,
        TimeSpan? cacheDuration = null,
        bool serveStaleOnFailure = true) =>
        RuntimeDiscoveryPolicy.Create(
            enabled,
            cacheDuration ?? TimeSpan.FromSeconds(30),
            serveStaleOnFailure);

    internal static RuntimeTimeouts Timeouts() => RuntimeTimeouts.Create(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(10));
}
