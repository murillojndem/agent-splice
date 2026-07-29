using AgentSplice.Application.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Configuration;

/// <summary>
/// Configuration validation (docs/SPECIFICATION.md FR-MOD-006, NFR 14.2).
/// </summary>
/// <remarks>
/// Each case here is a misconfiguration that would otherwise surface as a confusing runtime failure:
/// order-dependent routing, an alias that resolves nowhere, content capture with no store, or an
/// adapter flag an operator believes is active.
/// </remarks>
public sealed class AgentSpliceOptionsValidatorTests
{
    private readonly AgentSpliceOptionsValidator validator = new();

    [Fact]
    public void A_minimal_valid_configuration_passes()
    {
        var result = Validate(Valid());

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void A_configuration_with_no_runtimes_passes_so_the_gateway_can_start_unconfigured()
    {
        var options = Valid();
        options.Runtimes.Clear();

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Duplicate_runtime_identifiers_fail_because_routing_would_become_order_dependent()
    {
        var options = Valid();
        options.Runtimes.Add(Runtime("lmstudio-local"));

        AssertFailure(options, "declared more than once");
    }

    [Fact]
    public void Runtime_identifiers_are_compared_after_case_normalisation()
    {
        var options = Valid();
        options.Runtimes.Add(Runtime("LMSTUDIO-LOCAL"));

        AssertFailure(options, "declared more than once");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/v1")]
    [InlineData("ftp://127.0.0.1/v1")]
    public void A_runtime_base_url_must_be_an_absolute_http_url(string baseUrl)
    {
        var options = Valid();
        options.Runtimes[0].BaseUrl = baseUrl;

        AssertFailure(options, "absolute http or https URL");
    }

    [Fact]
    public void A_runtime_base_url_must_not_embed_credentials()
    {
        var options = Valid();
        options.Runtimes[0].BaseUrl = "http://user:secret@127.0.0.1:1234/v1";

        AssertFailure(options, "must not embed credentials");
    }

    [Fact]
    public void A_runtime_requires_a_provider()
    {
        var options = Valid();
        options.Runtimes[0].Provider = "  ";

        AssertFailure(options, "provider is required");
    }

    [Fact]
    public void An_api_key_setting_must_name_an_environment_variable_not_hold_a_key()
    {
        var options = Valid();
        options.Runtimes[0].ApiKeyEnvironmentVariable = "sk-not-a-variable-name";

        AssertFailure(options, "never the key itself");
    }

    [Fact]
    public void A_valid_environment_variable_name_is_accepted()
    {
        var options = Valid();
        options.Runtimes[0].ApiKeyEnvironmentVariable = "LM_STUDIO_API_KEY";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_omitted_api_key_reference_is_accepted()
    {
        var options = Valid();
        options.Runtimes[0].ApiKeyEnvironmentVariable = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("responseHeaders")]
    [InlineData("idleStream")]
    [InlineData("total")]
    public void Every_timeout_phase_must_be_positive(string phase)
    {
        var options = Valid();
        var timeouts = options.Runtimes[0].Timeouts;

        switch (phase)
        {
            case "connect":
                timeouts.Connect = TimeSpan.Zero;
                break;
            case "responseHeaders":
                timeouts.ResponseHeaders = TimeSpan.Zero;
                break;
            case "idleStream":
                timeouts.IdleStream = TimeSpan.Zero;
                break;
            default:
                timeouts.Total = TimeSpan.Zero;
                break;
        }

        AssertFailure(options, phase + " must be greater than zero");
    }

    [Fact]
    public void A_phase_timeout_may_not_exceed_the_total_budget()
    {
        var options = Valid();
        options.Runtimes[0].Timeouts.Total = TimeSpan.FromSeconds(10);
        options.Runtimes[0].Timeouts.ResponseHeaders = TimeSpan.FromSeconds(30);

        AssertFailure(options, "responseHeaders must not exceed timeouts:total");
    }

    [Fact]
    public void A_negative_discovery_cache_duration_fails()
    {
        var options = Valid();
        options.Runtimes[0].Discovery.CacheDuration = TimeSpan.FromSeconds(-1);

        AssertFailure(options, "cacheDuration must not be negative");
    }

    [Fact]
    public void An_alias_must_reference_a_configured_runtime()
    {
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "does-not-exist", "qwen3.6-27b-mtp"));

        AssertFailure(options, "does not match any configured runtime");
    }

    [Fact]
    public void Duplicate_alias_identifiers_fail_validation()
    {
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp"));
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "other-model"));

        AssertFailure(options, "FR-MOD-006");
    }

    [Fact]
    public void An_alias_cycle_fails_validation()
    {
        var options = Valid();
        options.Aliases.Add(Alias("first", "lmstudio-local", "second"));
        options.Aliases.Add(Alias("second", "lmstudio-local", "first"));

        AssertFailure(options, "resolution cycle");
    }

    [Fact]
    public void A_self_referencing_alias_fails_validation()
    {
        var options = Valid();
        options.Aliases.Add(Alias("loop", "lmstudio-local", "loop"));

        AssertFailure(options, "resolution cycle");
    }

    [Fact]
    public void A_longer_alias_cycle_fails_validation()
    {
        var options = Valid();
        options.Aliases.Add(Alias("a", "lmstudio-local", "b"));
        options.Aliases.Add(Alias("b", "lmstudio-local", "c"));
        options.Aliases.Add(Alias("c", "lmstudio-local", "a"));

        AssertFailure(options, "resolution cycle");
    }

    [Fact]
    public void An_alias_chain_that_terminates_at_a_real_model_is_accepted()
    {
        var options = Valid();
        options.Aliases.Add(Alias("friendly-name", "lmstudio-local", "team-default"));
        options.Aliases.Add(Alias("team-default", "lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_alias_requires_a_valid_upstream_model_identifier()
    {
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "model with spaces"));

        AssertFailure(options, "is not a valid model identifier");
    }

    [Fact]
    public void Adapters_may_not_be_enabled_in_the_current_stage()
    {
        var options = Valid();
        options.Adapters.Enabled = true;

        AssertFailure(options, "Stage 4 capability");
    }

    [Fact]
    public void A_persistence_mode_other_than_none_requires_a_connection_string()
    {
        var options = Valid();
        options.Persistence.Mode = PersistenceMode.Postgres;
        options.Persistence.ConnectionString = null;

        AssertFailure(options, "connectionString is required");
    }

    [Fact]
    public void Persistence_mode_none_needs_no_connection_string()
    {
        var options = Valid();
        options.Persistence.Mode = PersistenceMode.None;
        options.Persistence.ConnectionString = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void The_metadata_queue_must_be_bounded_and_non_empty()
    {
        var options = Valid();
        options.Persistence.MetadataQueueCapacity = 0;

        AssertFailure(options, "metadataQueueCapacity must be greater than zero");
    }

    [Fact]
    public void Content_capture_without_a_store_fails()
    {
        var options = Valid();
        options.Persistence.Mode = PersistenceMode.None;
        options.Persistence.ConnectionString = null;
        options.Capture.ContentEnabled = true;

        AssertFailure(options, "content capture requires a configured store");
    }

    [Fact]
    public void Content_capture_without_metadata_capture_fails()
    {
        var options = Valid();
        options.Capture.ContentEnabled = true;
        options.Capture.MetadataEnabled = false;

        AssertFailure(options, "not attributable");
    }

    [Fact]
    public void Content_capture_with_a_store_and_metadata_is_accepted_as_an_explicit_opt_in()
    {
        var options = Valid();
        options.Capture.ContentEnabled = true;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Retention_windows_must_be_positive()
    {
        var options = Valid();
        options.Capture.Retention.Metadata = TimeSpan.Zero;

        AssertFailure(options, "retention:metadata must be greater than zero");
    }

    [Fact]
    public void A_blank_allowlisted_header_fails()
    {
        var options = Valid();
        options.Diagnostics.HeaderAllowlist.Add("   ");

        AssertFailure(options, "must not be blank");
    }

    [Fact]
    public void The_header_allowlist_is_not_checked_when_headers_are_not_stored_by_allowlist()
    {
        var options = Valid();
        options.Diagnostics.StoreHeaders = HeaderCaptureMode.None;
        options.Diagnostics.HeaderAllowlist.Add("   ");

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_invalid_public_base_url_fails()
    {
        var options = Valid();
        options.PublicBaseUrl = "localhost:5280";

        AssertFailure(options, "publicBaseUrl");
    }

    [Fact]
    public void An_omitted_public_base_url_is_accepted()
    {
        var options = Valid();
        options.PublicBaseUrl = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Every_failure_is_reported_rather_than_only_the_first()
    {
        var options = Valid();
        options.Adapters.Enabled = true;
        options.Runtimes[0].BaseUrl = "not a url";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.True(result.Failures?.Count() >= 2);
    }

    [Fact]
    public void Validate_rejects_a_null_options_instance()
    {
        Assert.Throws<ArgumentNullException>(() => validator.Validate(null, null!));
    }

    private static AgentSpliceOptions Valid() => new()
    {
        PublicBaseUrl = "http://127.0.0.1:5280",
        Persistence = new PersistenceOptions
        {
            Mode = PersistenceMode.Sqlite,
            ConnectionString = "Data Source=agentsplice.db",
        },
        Runtimes = { Runtime("lmstudio-local") },
    };

    private static RuntimeEndpointOptions Runtime(string id) => new()
    {
        Id = id,
        Provider = "lmstudio",
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiKeyEnvironmentVariable = "LM_STUDIO_API_KEY",
    };

    private static ModelAliasOptions Alias(string id, string runtimeId, string upstreamModelId) => new()
    {
        Id = id,
        RuntimeId = runtimeId,
        UpstreamModelId = upstreamModelId,
    };

    private ValidateOptionsResult Validate(AgentSpliceOptions options) =>
        validator.Validate(Options.DefaultName, options);

    private void AssertFailure(AgentSpliceOptions options, string expectedFragment)
    {
        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(expectedFragment, StringComparison.Ordinal));
    }
}
