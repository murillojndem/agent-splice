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
    public void The_configuration_section_name_is_stable()
    {
        Assert.Equal("agentsplice", AgentSpliceOptions.SectionName);
    }
}
