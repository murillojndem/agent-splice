using System.Reflection;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Models;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Runtimes;
using Microsoft.Extensions.Options;

namespace AgentSplice.Application.Administration;

/// <summary>
/// The diagnostic half of the administrative surface (FR-HEALTH-001 to FR-HEALTH-005, FR-MOD-009).
/// </summary>
/// <remarks>
/// Health is <em>derived from discovery</em> rather than probed separately. A second prober would
/// double the load a gateway puts on every configured runtime and could disagree with what routing
/// sees, which is the worse of the two failures: a health page that says a runtime is fine while
/// completions to it are failing is a page an operator will believe.
///
/// The consequence is that a runtime nothing has consulted reports
/// <see cref="RuntimeHealthStatus.Unknown"/> with no timestamp, and that is correct. Reporting it as
/// healthy would be a claim from no evidence (FR-TRACE-006).
/// </remarks>
public sealed class DiagnosticsService
{
    private readonly Runtimes.RuntimeRegistry runtimes;
    private readonly ModelCatalogueService catalogue;
    private readonly IAdministrativeEnvelopeWriter writer;
    private readonly IOptions<AgentSpliceOptions> options;

    /// <summary>Creates the service.</summary>
    public DiagnosticsService(
        Runtimes.RuntimeRegistry runtimes,
        ModelCatalogueService catalogue,
        IAdministrativeEnvelopeWriter writer,
        IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        this.runtimes = runtimes;
        this.catalogue = catalogue;
        this.writer = writer;
        this.options = options;
    }

    /// <summary>Build and module information.</summary>
    public GatewayResponse Describe(PublicRequestId requestId)
    {
        var view = new SystemInfoView
        {
            Version = Version(),
            Stage = "1C",
            EnabledModules = Modules(),
            ContentRetentionEnabled = options.Value.Capture.ContentEnabled,
            MetadataRetentionEnabled = options.Value.Persistence.Mode != PersistenceMode.None
                && options.Value.Capture.MetadataEnabled,
        };

        return GatewayResponse.Success(200, writer.MediaType, writer.Write(view), requestId);
    }

    /// <summary>Configured runtimes with their connection detail redacted.</summary>
    public GatewayResponse ListRuntimes(PublicRequestId requestId)
    {
        var views = runtimes.All.Select(runtime => new RuntimeSummaryView
        {
            RuntimeId = runtime.Id.Value,
            Provider = runtime.ProviderKey,
            BaseUrl = runtime.BaseAddress.AbsoluteUri,
            ApiKeyEnvironmentVariable = runtime.ApiKeyEnvironmentVariable,
            Enabled = runtime.Enabled,
            DiscoveryEnabled = runtime.Discovery.Enabled,
        }).ToList();

        return GatewayResponse.Success(200, writer.MediaType, writer.Write(views), requestId);
    }

    /// <summary>The administrative model catalogue, with provenance and reachability.</summary>
    public async Task<GatewayResponse> ListModelsAsync(
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        var composed = await catalogue.ComposeAsync(cancellationToken).ConfigureAwait(false);

        var views = composed.Entries.Select(entry => new CatalogModelView
        {
            ClientModelId = entry.ClientModel.Value,
            RuntimeId = entry.Runtime.Value,
            UpstreamModelId = entry.UpstreamModel.Value,
            AliasId = entry.Alias?.Value,
            Source = entry.Source,
            // Null when nothing has consulted the owning runtime, rather than a boolean asserted
            // from an attempt that never happened.
            Reachable = runtimes.Find(entry.Runtime)?.Discovery.Enabled == true ? entry.Reachable : null,
            CapabilityProvenance = entry.CapabilityProvenance,
            Created = entry.Created,
        }).ToList();

        return GatewayResponse.Success(200, writer.MediaType, writer.Write(views), requestId);
    }

    /// <summary>Runtime health, as the last discovery attempt observed it.</summary>
    public async Task<GatewayResponse> ListRuntimeHealthAsync(
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        var views = await HealthAsync(cancellationToken).ConfigureAwait(false);

        return GatewayResponse.Success(200, writer.MediaType, writer.Write(views), requestId);
    }

    /// <summary>
    /// Whether the gateway is ready to serve.
    /// </summary>
    /// <remarks>
    /// Readiness may require a reachable runtime and by default does not (FR-HEALTH-003). The default
    /// is off because a gateway whose runtime is down is still correctly configured and still the
    /// thing that should answer the request that proves it — reporting itself unready would make an
    /// orchestrator remove the one component able to report the outage.
    /// </remarks>
    public async Task<GatewayResponse> DescribeReadinessAsync(
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        var required = options.Value.Health.RequireReachableRuntime;
        var health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var reachable = health.Count(view => view.Status == RuntimeHealthStatus.Healthy);

        var view = new ReadinessView
        {
            Ready = !required || reachable > 0,
            RequiresReachableRuntime = required,
            ReachableRuntimes = reachable,
            EnabledRuntimes = runtimes.Enabled.Count,
        };

        return GatewayResponse.Success(
            view.Ready ? 200 : 503,
            writer.MediaType,
            writer.Write(view),
            requestId);
    }

    /// <summary>
    /// Health for every enabled runtime.
    /// </summary>
    /// <remarks>
    /// Reads the discovery cache through the same path routing uses, so the two cannot disagree. A
    /// runtime with discovery switched off has never been consulted and reports
    /// <see cref="RuntimeHealthStatus.Unknown"/> — it is not unhealthy, and it is not known to be
    /// healthy either.
    /// </remarks>
    private async Task<IReadOnlyList<RuntimeHealthView>> HealthAsync(CancellationToken cancellationToken)
    {
        var views = new List<RuntimeHealthView>(runtimes.Enabled.Count);

        foreach (var runtime in runtimes.Enabled)
        {
            var described = await catalogue.DescribeAsync(runtime, cancellationToken).ConfigureAwait(false);

            if (described is null)
            {
                views.Add(new RuntimeHealthView
                {
                    RuntimeId = runtime.Id.Value,
                    Status = RuntimeHealthStatus.Unknown,
                    ServedFromStaleCache = false,
                });

                continue;
            }

            var outcome = RuntimeDiscoveryOutcome.From(described);

            views.Add(new RuntimeHealthView
            {
                RuntimeId = runtime.Id.Value,
                Status = outcome.Status,
                CheckedAt = described.RetrievedAt,
                ServedFromStaleCache = outcome.ServedFromStaleCache,
            });
        }

        return views;
    }

    /// <summary>The modules this host composed, named rather than described.</summary>
    private static IReadOnlyList<string> Modules() =>
    [
        "AgentSplice.Protocols.OpenAI",
        "AgentSplice.Providers.LmStudio",
        "AgentSplice.Observability",
        "AgentSplice.Infrastructure.Persistence",
    ];

    private static string Version() =>
        typeof(DiagnosticsService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DiagnosticsService).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
