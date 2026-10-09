using AgentSplice.Application.Configuration;
using AgentSplice.Observability;
using Xunit;

namespace AgentSplice.UnitTests.Observability;

/// <summary>
/// Where each signal is sent (ADR 0014, docs/OBSERVABILITY.md "OpenTelemetry export").
/// </summary>
/// <remarks>
/// The SDK appends a signal path only to an endpoint it chose itself, and uses one set in code
/// verbatim. Every case here is one where getting the arithmetic wrong posts spans to a path the
/// collector answers 404 on, and an exporter failure is — by design — invisible to the request path.
/// </remarks>
public sealed class OtlpExportTargetTests
{
    [Fact]
    public void Nothing_is_exported_while_export_is_off_even_with_an_endpoint()
    {
        var options = new OtlpExportOptions { Enabled = false, Endpoint = "http://127.0.0.1:4318" };

        Assert.Null(OtlpExportTarget.From(options));
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318")]
    [InlineData("http://127.0.0.1:4318/")]
    public void An_http_receiver_gets_each_signal_at_its_own_path(string endpoint)
    {
        var target = Enabled(TelemetryExportProtocol.HttpProtobuf, endpoint);

        Assert.Equal(new Uri("http://127.0.0.1:4318/v1/traces"), target.TracesEndpoint);
        Assert.Equal(new Uri("http://127.0.0.1:4318/v1/metrics"), target.MetricsEndpoint);
    }

    [Theory]
    [InlineData("https://collector.internal/otlp")]
    [InlineData("https://collector.internal/otlp/")]
    public void An_http_receiver_behind_a_path_keeps_that_path(string endpoint)
    {
        // Without the trailing slash, relative resolution would replace "otlp" rather than append to
        // it, and spans would go to /v1/traces on the proxy instead of /otlp/v1/traces.
        var target = Enabled(TelemetryExportProtocol.HttpProtobuf, endpoint);

        Assert.Equal(new Uri("https://collector.internal/otlp/v1/traces"), target.TracesEndpoint);
        Assert.Equal(new Uri("https://collector.internal/otlp/v1/metrics"), target.MetricsEndpoint);
    }

    [Fact]
    public void A_grpc_receiver_is_addressed_as_configured_for_both_signals()
    {
        // gRPC names a service method, which the exporter supplies; a path here would be wrong.
        var target = Enabled(TelemetryExportProtocol.Grpc, "http://otel-collector:4317");

        Assert.Equal(new Uri("http://otel-collector:4317"), target.TracesEndpoint);
        Assert.Equal(new Uri("http://otel-collector:4317"), target.MetricsEndpoint);
    }

    [Fact]
    public void The_configured_endpoint_is_kept_as_given_for_reporting()
    {
        var target = Enabled(TelemetryExportProtocol.HttpProtobuf, "http://127.0.0.1:4318");

        Assert.Equal(TelemetryExportProtocol.HttpProtobuf, target.Protocol);
        Assert.Equal(new Uri("http://127.0.0.1:4318"), target.Endpoint);
    }

    [Fact]
    public void An_enabled_export_without_an_endpoint_is_a_composition_fault()
    {
        // Startup validation refuses this configuration, so reaching here means validation was
        // bypassed. Failing loudly beats exporting to the SDK's own localhost default.
        var options = new OtlpExportOptions { Enabled = true, Endpoint = null };

        Assert.Throws<InvalidOperationException>(() => OtlpExportTarget.From(options));
    }

    private static OtlpExportTarget Enabled(TelemetryExportProtocol protocol, string endpoint) =>
        OtlpExportTarget.From(new OtlpExportOptions { Enabled = true, Protocol = protocol, Endpoint = endpoint })
        ?? throw new InvalidOperationException("An enabled export resolved to no target.");
}
