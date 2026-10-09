using System.Reflection;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentSplice.Observability;

/// <summary>Registers AgentSplice's spans and metrics and, when configured, their export.</summary>
public static class ObservabilityRegistration
{
    /// <summary>The <c>service.name</c> every exported span and metric carries.</summary>
    public const string ServiceName = "agentsplice";

    /// <summary>
    /// Registers the telemetry implementation and the OpenTelemetry SDK that subscribes to it.
    /// </summary>
    /// <remarks>
    /// The SDK is the only listener on the AgentSplice sources. It replaced a hand-registered
    /// <c>ActivityListener</c> rather than joining it (ADR 0014): two listeners on one source both
    /// take part in every sampling decision, and the one that did not export would be a second,
    /// silent opinion on what exists.
    ///
    /// The SDK runs whether or not anything is exported, because spans must exist either way:
    /// <c>StartActivity</c> returns null for a source nobody listens to, and
    /// <c>x-agentsplice-trace-id</c> is taken from the exchange span.
    ///
    /// Only traces and metrics. Logs are not routed through OpenTelemetry: the log pipeline is the one
    /// FR-OBS-007 constrains and its redaction is tested where it is written, and exporting it is a
    /// separate decision this slice did not take.
    /// </remarks>
    public static IServiceCollection AddAgentSpliceObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion()))
            .WithTracing(tracing => tracing
                .AddSource([.. TelemetryNames.LiveActivitySources])

                // Every exchange is sampled, including one whose caller propagated a trace context
                // marked unsampled. Sampling is a volume control for a hosted service; a local-first
                // gateway whose product is evidence must not let a client's header discard some of
                // it.
                .SetSampler(new AlwaysOnSampler()))
            .WithMetrics(metrics => metrics.AddMeter(TelemetryNames.Meter));

        services.ConfigureOpenTelemetryTracerProvider(AddTraceExport);
        services.ConfigureOpenTelemetryMeterProvider(AddMetricExport);

        services.AddSingleton<IExchangeTelemetry, ExchangeTelemetry>();

        return services;
    }

    /// <summary>
    /// Adds the span exporter when configuration asks for one.
    /// </summary>
    /// <remarks>
    /// Decided when the provider is built, from validated options, and not at registration: the host
    /// is still layering configuration while services are registered, and a test host adds its
    /// overrides after this method has run.
    ///
    /// The exporter is constructed here rather than through <c>AddOtlpExporter</c>, which reads its
    /// endpoint from the host's configuration and would therefore let <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// decide where evidence goes. Endpoint and protocol are always set from AgentSplice settings.
    /// </remarks>
    private static void AddTraceExport(IServiceProvider provider, TracerProviderBuilder builder)
    {
        if (Target(provider) is not { } target)
        {
            return;
        }

        builder.AddProcessor(
            new BatchActivityExportProcessor(
                new OtlpTraceExporter(ExporterOptions(target.Protocol, target.TracesEndpoint))));

        // Announced once, here, because the tracer provider is built exactly once per host and before
        // the first exchange. The endpoint is safe to log: validation refuses one carrying
        // credentials, a query, or a fragment.
        provider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ObservabilityRegistration).FullName!)
            .LogInformation(
                GatewayEventIds.TelemetryExportEnabled,
                "Exporting traces and metrics over OTLP {Protocol} to {Endpoint}.",
                target.Protocol,
                target.Endpoint.AbsoluteUri);
    }

    /// <summary>Adds the metric reader when configuration asks for one.</summary>
    private static void AddMetricExport(IServiceProvider provider, MeterProviderBuilder builder)
    {
        if (Target(provider) is not { } target)
        {
            return;
        }

        builder.AddReader(
            new PeriodicExportingMetricReader(
                new OtlpMetricExporter(ExporterOptions(target.Protocol, target.MetricsEndpoint))));
    }

    private static OtlpExportTarget? Target(IServiceProvider provider) =>
        OtlpExportTarget.From(provider.GetRequiredService<IOptions<AgentSpliceOptions>>().Value.Telemetry.Otlp);

    /// <summary>
    /// Exporter settings for one signal.
    /// </summary>
    /// <remarks>
    /// The SDK still reads the rest of its standard environment variables when these options are
    /// created — notably <c>OTEL_EXPORTER_OTLP_HEADERS</c>, which is how a collector credential is
    /// supplied without ever entering AgentSplice's configuration, and <c>OTEL_EXPORTER_OTLP_TIMEOUT</c>.
    /// Endpoint and protocol are overwritten here, so neither can be redirected from the environment.
    /// </remarks>
    private static OtlpExporterOptions ExporterOptions(TelemetryExportProtocol protocol, Uri endpoint) =>
        new()
        {
            Endpoint = endpoint,
            Protocol = protocol switch
            {
                TelemetryExportProtocol.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
                TelemetryExportProtocol.Grpc => OtlpExportProtocol.Grpc,
                _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Startup validation refuses an unrecognised protocol."),
            },
        };

    private static string ServiceVersion() =>
        typeof(ObservabilityRegistration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ObservabilityRegistration).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
