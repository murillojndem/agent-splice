using AgentSplice.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// The loopback-binding fallback (docs/SPECIFICATION.md NFR 14.3).
/// </summary>
/// <remarks>
/// This decision was originally expressed as a <c>Urls</c> value in <c>appsettings.json</c>, which
/// layers over the host configuration carrying <c>ASPNETCORE_URLS</c> and therefore overrode it. The
/// container bound to loopback inside itself and the published port mapping was unreachable. These
/// cases pin the fallback semantics so the same inversion cannot come back.
/// </remarks>
public sealed class LoopbackBindingDefaultTests
{
    [Fact]
    public void With_nothing_configured_the_loopback_default_applies()
    {
        Assert.True(LoopbackBindingDefault.ShouldApply(Configuration()));
    }

    [Fact]
    public void The_loopback_default_binds_only_the_loopback_interface()
    {
        Assert.Equal("http://127.0.0.1:5280", LoopbackBindingDefault.Urls);
        Assert.DoesNotContain("0.0.0.0", LoopbackBindingDefault.Urls, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", LoopbackBindingDefault.Urls, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_urls_value_suppresses_the_default()
    {
        Assert.False(LoopbackBindingDefault.ShouldApply(
            Configuration(("urls", "http://0.0.0.0:5280"))));
    }

    [Fact]
    public void A_container_http_ports_value_suppresses_the_default()
    {
        // The .NET 8 ASP.NET base image sets ASPNETCORE_HTTP_PORTS. Honouring it is what lets a
        // container bind its own interface while a bare local run stays on loopback.
        Assert.False(LoopbackBindingDefault.ShouldApply(
            Configuration(("HTTP_PORTS", "8080"))));
    }

    [Fact]
    public void An_https_ports_value_suppresses_the_default()
    {
        Assert.False(LoopbackBindingDefault.ShouldApply(
            Configuration(("HTTPS_PORTS", "8081"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_urls_value_does_not_count_as_configuration(string urls)
    {
        Assert.True(LoopbackBindingDefault.ShouldApply(Configuration(("urls", urls))));
    }

    [Fact]
    public void The_shipped_settings_file_declares_no_binding_of_its_own()
    {
        // A Urls or HTTP_PORTS key here would defeat the fallback and re-create the container defect.
        var settings = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Null(settings["urls"]);
        Assert.Null(settings["HTTP_PORTS"]);
        Assert.Null(settings["HTTPS_PORTS"]);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
    {
        var entries = values.ToDictionary(
            entry => entry.Key,
            entry => (string?)entry.Value,
            StringComparer.OrdinalIgnoreCase);

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }
}
