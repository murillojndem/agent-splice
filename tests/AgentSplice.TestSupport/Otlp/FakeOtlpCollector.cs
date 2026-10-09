using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSplice.TestSupport.Otlp;

/// <summary>
/// A real OTLP receiver that accepts every export and records exactly what arrived.
/// </summary>
/// <remarks>
/// A real Kestrel listener for the same reason the fake upstream is one: what is under test is what
/// crosses the wire, including the transport the exporter negotiated. An in-memory exporter would
/// prove the SDK saw a span and nothing about what a collector receives.
///
/// Payloads are kept as raw protobuf and searched as bytes. Every string OTLP carries — span names,
/// attribute keys and values, metric names — is encoded as plain UTF-8 inside the message, so a
/// value that is absent from the bytes is absent from the export. That is the direction the privacy
/// assertions need, and it avoids decoding with a schema the gateway does not itself depend on.
/// </remarks>
public sealed class FakeOtlpCollector : IAsyncDisposable
{
    /// <summary>The OTLP/HTTP path spans are posted to.</summary>
    public const string HttpTracesPath = "/v1/traces";

    /// <summary>The OTLP/HTTP path metrics are posted to.</summary>
    public const string HttpMetricsPath = "/v1/metrics";

    /// <summary>The gRPC method spans are exported through.</summary>
    public const string GrpcTracesPath = "/opentelemetry.proto.collector.trace.v1.TraceService/Export";

    /// <summary>The gRPC method metrics are exported through.</summary>
    public const string GrpcMetricsPath = "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export";

    private readonly WebApplication application;
    private readonly OtlpReceiverTransport transport;
    private readonly List<ReceivedOtlpExport> received = [];
    private readonly object receivedGate = new();

    private FakeOtlpCollector(WebApplication application, OtlpReceiverTransport transport)
    {
        this.application = application;
        this.transport = transport;
    }

    /// <summary>The loopback base address the receiver listens on, with a trailing slash.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Every export received so far, in arrival order.</summary>
    public IReadOnlyList<ReceivedOtlpExport> Received
    {
        get
        {
            lock (receivedGate)
            {
                return received.ToArray();
            }
        }
    }

    /// <summary>Starts a receiver on an ephemeral loopback port.</summary>
    /// <param name="transport">
    /// HTTP/1.1 for OTLP/HTTP, or cleartext HTTP/2 with prior knowledge for gRPC — which is what an
    /// <c>http://</c> gRPC endpoint means, and what a collector's port 4317 speaks.
    /// </param>
    /// <param name="cancellationToken">Cancels startup.</param>
    public static async Task<FakeOtlpCollector> StartAsync(
        OtlpReceiverTransport transport,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.ConfigureEndpointDefaults(listen =>
            listen.Protocols = transport == OtlpReceiverTransport.Grpc
                ? HttpProtocols.Http2
                : HttpProtocols.Http1));

        var application = builder.Build();
        var collector = new FakeOtlpCollector(application, transport);

        application.Run(collector.HandleAsync);

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The fake OTLP collector did not report a bound address.");

        collector.BaseAddress = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        return collector;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await application.StopAsync().ConfigureAwait(false);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);

        lock (receivedGate)
        {
            received.Add(new ReceivedOtlpExport(
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol,
                context.Request.ContentType,
                body.ToArray()));
        }

        if (transport == OtlpReceiverTransport.Grpc)
        {
            // An empty Export*ServiceResponse, framed the way gRPC frames every message: a
            // compression flag and a four-byte big-endian length, all zero. The status travels in a
            // trailer, and without it a gRPC client treats the call as failed.
            context.Response.ContentType = "application/grpc";
            await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted).ConfigureAwait(false);
            context.Response.AppendTrailer("grpc-status", "0");
            return;
        }

        // An empty Export*ServiceResponse is zero bytes of protobuf.
        context.Response.ContentType = "application/x-protobuf";
    }
}
