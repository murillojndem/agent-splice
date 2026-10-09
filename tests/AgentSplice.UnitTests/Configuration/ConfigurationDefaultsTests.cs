using AgentSplice.Application.Configuration;
using Xunit;

namespace AgentSplice.UnitTests.Configuration;

/// <summary>
/// Privacy and safety defaults. These assertions exist so that a future change which flips a default
/// has to change a test that says why the default was chosen
/// (docs/SPECIFICATION.md FR-DATA-005, FR-MOD-008, ADR 0004).
/// </summary>
public sealed class ConfigurationDefaultsTests
{
    [Fact]
    public void Content_capture_is_disabled_by_default()
    {
        Assert.False(new CaptureOptions().ContentEnabled);
    }

    [Fact]
    public void Metadata_capture_is_enabled_by_default()
    {
        Assert.True(new CaptureOptions().MetadataEnabled);
    }

    [Fact]
    public void Body_storage_is_disabled_by_default()
    {
        Assert.False(new DiagnosticsOptions().StoreBodies);
    }

    [Fact]
    public void Headers_are_captured_by_allowlist_by_default()
    {
        Assert.Equal(HeaderCaptureMode.Allowlist, new DiagnosticsOptions().StoreHeaders);
    }

    [Fact]
    public void The_default_header_allowlist_contains_no_credential_bearing_header()
    {
        string[] credentialHeaders = ["authorization", "cookie", "set-cookie", "proxy-authorization", "api-key"];

        foreach (var header in new DiagnosticsOptions().HeaderAllowlist)
        {
            Assert.DoesNotContain(header.ToLowerInvariant(), credentialHeaders);
        }
    }

    [Fact]
    public void Capability_probing_is_disabled_by_default()
    {
        Assert.False(new DiscoveryOptions().CapabilityProbingEnabled);
    }

    [Fact]
    public void Adapters_are_disabled_by_default()
    {
        Assert.False(new AdapterOptions().Enabled);
    }

    [Fact]
    public void Sqlite_is_the_default_persistence_mode()
    {
        Assert.Equal(PersistenceMode.Sqlite, new PersistenceOptions().Mode);
    }

    [Fact]
    public void The_metadata_queue_is_bounded_by_default()
    {
        Assert.True(new PersistenceOptions().MetadataQueueCapacity > 0);
    }

    [Fact]
    public void Content_is_retained_for_less_time_than_metadata_by_default()
    {
        var retention = new RetentionOptions();

        Assert.True(retention.Content < retention.Metadata);
    }

    [Fact]
    public void Every_timeout_phase_has_a_positive_default()
    {
        var timeouts = new TimeoutOptions();

        Assert.True(timeouts.Connect > TimeSpan.Zero);
        Assert.True(timeouts.ResponseHeaders > TimeSpan.Zero);
        Assert.True(timeouts.IdleStream > TimeSpan.Zero);
        Assert.True(timeouts.Total > TimeSpan.Zero);
    }

    [Fact]
    public void No_phase_timeout_default_exceeds_the_total_default()
    {
        var timeouts = new TimeoutOptions();

        Assert.True(timeouts.Connect <= timeouts.Total);
        Assert.True(timeouts.ResponseHeaders <= timeouts.Total);
        Assert.True(timeouts.IdleStream <= timeouts.Total);
    }

    [Fact]
    public void A_new_runtime_is_enabled_and_discovers_models_by_default()
    {
        var runtime = new RuntimeEndpointOptions();

        Assert.True(runtime.Enabled);
        Assert.True(runtime.Discovery.Enabled);
        Assert.True(runtime.Discovery.ServeStaleOnFailure);
    }

    [Fact]
    public void Pass_through_routing_is_disabled_by_default()
    {
        // Without a default runtime an unrecognised model is rejected rather than forwarded, so the
        // strict posture is what a deployment gets without asking for it.
        Assert.Null(new AgentSpliceOptions().DefaultRuntimeId);
    }

    [Fact]
    public void Every_body_limit_has_a_positive_default()
    {
        var limits = new LimitsOptions();

        Assert.True(limits.MaxRequestBodyBytes > 0);
        Assert.True(limits.MaxUpstreamCompletionBodyBytes > 0);
        Assert.True(limits.MaxCatalogueBodyBytes > 0);
    }

    [Fact]
    public void A_model_catalogue_is_bounded_more_tightly_than_a_completion_by_default()
    {
        // A catalogue is a small, predictable document. A runtime answering model discovery with a
        // completion-sized payload is a defect worth failing on early.
        var limits = new LimitsOptions();

        Assert.True(limits.MaxCatalogueBodyBytes < limits.MaxUpstreamCompletionBodyBytes);
    }

    [Fact]
    public void Telemetry_export_is_disabled_and_has_no_destination_by_default()
    {
        // Exported telemetry describes every exchange. Sending that off the process is a decision, so
        // a deployment that never mentioned export exports nothing, and one that turns it on must say
        // where (ADR 0014).
        var otlp = new AgentSpliceOptions().Telemetry.Otlp;

        Assert.False(otlp.Enabled);
        Assert.Null(otlp.Endpoint);
    }

    [Fact]
    public void Telemetry_is_exported_over_http_protobuf_unless_told_otherwise()
    {
        // The OpenTelemetry specification's recommended default, and not the .NET SDK's: the SDK
        // defaults to gRPC, which is why this is stated rather than inherited.
        Assert.Equal(TelemetryExportProtocol.HttpProtobuf, new OtlpExportOptions().Protocol);
    }

    [Fact]
    public void The_dashboard_is_served_by_default_from_the_directory_the_image_uses()
    {
        // On, because the alpha is installed to be looked at, and served only when a build is present,
        // so the default costs a gateway run from source nothing. "dashboard" relative to the content
        // root is /app/dashboard in the image, which is where the Dockerfile copies the build
        // (ADR 0015; PackagingContractTests pins the two together).
        var dashboard = new AgentSpliceOptions().Dashboard;

        Assert.True(dashboard.Enabled);
        Assert.Equal("dashboard", dashboard.RootPath);
    }

    [Fact]
    public void The_configuration_section_name_is_stable()
    {
        Assert.Equal("agentsplice", AgentSpliceOptions.SectionName);
    }
}
