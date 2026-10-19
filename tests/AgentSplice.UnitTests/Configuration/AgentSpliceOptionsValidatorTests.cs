using System.Globalization;
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
    public void A_key_pasted_in_place_of_its_variables_name_is_refused_without_being_repeated()
    {
        // The failure quoted the setting, so the one startup error an operator was sure to read
        // printed the key they had pasted by mistake.
        const string Key = "sk-lm-SENTINEL-KEY-0123456789";

        var options = Valid();
        options.Runtimes[0].ApiKeyEnvironmentVariable = Key;

        var failures = Validate(options).Failures ?? [];

        Assert.NotEmpty(failures);
        Assert.DoesNotContain(failures, failure => failure.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public void The_administrative_tokens_variable_is_held_to_the_same_rule()
    {
        // It was not validated at all, so a token pasted there went unnoticed until a network binding
        // refused to start, and that refusal quoted it.
        const string Token = "a3f9-SENTINEL-ADMIN-TOKEN";

        var options = Valid();
        options.Administration.ApiKeyEnvironmentVariable = Token;

        var failures = Validate(options).Failures ?? [];

        Assert.Contains(failures, failure => failure.Contains("agentsplice:administration:apiKeyEnvironmentVariable", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, failure => failure.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_administrative_variable_name_is_accepted()
    {
        var options = Valid();
        options.Administration.ApiKeyEnvironmentVariable = "AGENTSPLICE_ADMIN_API_KEY";

        Assert.True(Validate(options).Succeeded);
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
    [InlineData("firstByte")]
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
            case "firstByte":
                timeouts.FirstByte = TimeSpan.Zero;
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
    public void The_first_byte_budget_may_not_exceed_the_total_budget()
    {
        // A first-byte budget past the total could never be the one that fired, so the phase
        // reported for a stream that never started would always be the total (FR-CHAT-008).
        var options = Valid();
        options.Runtimes[0].Timeouts.Total = TimeSpan.FromSeconds(10);
        options.Runtimes[0].Timeouts.ResponseHeaders = TimeSpan.FromSeconds(10);
        options.Runtimes[0].Timeouts.FirstByte = TimeSpan.FromSeconds(11);

        AssertFailure(options, "agentsplice:runtimes[0]:timeouts:firstByte must not exceed timeouts:total.");
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("responseHeaders")]
    [InlineData("firstByte")]
    [InlineData("idleStream")]
    public void A_phase_as_long_as_the_total_is_accepted(string phase)
    {
        // Equal is allowed: the shipped defaults put two phases at five minutes inside a ten-minute
        // total, and a deployment that shortens the total to five minutes must still start.
        var options = Valid();
        var timeouts = options.Runtimes[0].Timeouts;
        timeouts.Total = TimeSpan.FromMinutes(5);
        timeouts.Connect = TimeSpan.FromSeconds(5);
        timeouts.ResponseHeaders = TimeSpan.FromMinutes(1);
        timeouts.FirstByte = TimeSpan.FromMinutes(1);
        timeouts.IdleStream = TimeSpan.FromMinutes(1);

        switch (phase)
        {
            case "connect":
                timeouts.Connect = timeouts.Total;
                break;
            case "responseHeaders":
                timeouts.ResponseHeaders = timeouts.Total;
                break;
            case "firstByte":
                timeouts.FirstByte = timeouts.Total;
                break;
            default:
                timeouts.IdleStream = timeouts.Total;
                break;
        }

        Assert.True(Validate(options).Succeeded);
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
    public void An_identity_alias_is_not_a_resolution_cycle()
    {
        // Stage 0 rejected this, reasoning about a resolver that chains alias to alias. The resolver
        // that now exists does not chain: an alias resolves to a runtime and an upstream model in
        // one step and stops, so an identity alias terminates immediately.
        //
        // It is also the only way an operator can pin a model to a chosen runtime when two runtimes
        // both offer it, so rejecting it would remove the sole deterministic override of the
        // FR-MOD-004 tie-break.
        var options = Valid();
        options.Aliases.Add(Alias("qwen3.6-27b-mtp", "lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_alias_requires_a_valid_upstream_model_identifier()
    {
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "model" + (char)0x00 + "name"));

        AssertFailure(options, "is not a valid model identifier");
    }

    [Fact]
    public void An_alias_requires_a_non_blank_upstream_model_identifier()
    {
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "   "));

        AssertFailure(options, "is not a valid model identifier");
    }

    [Fact]
    public void An_alias_accepts_the_opaque_punctuation_real_model_names_use()
    {
        // Model identifiers belong to the runtime, not to AgentSplice. An operator must be able to
        // configure any name the runtime will answer to, including one with spaces (P-002).
        var options = Valid();
        options.Aliases.Add(Alias("local-coder", "lmstudio-local", "Meta Llama 3.1 8B Instruct"));

        Assert.True(Validate(options).Succeeded);
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
    public void Content_capture_is_refused_by_a_build_that_cannot_sanitise()
    {
        // It used to be accepted whenever a store was configured, which told an operator that
        // prompts and model output were being retained under a sanitiser that does not exist. The
        // flag becomes a real setting in the slice that implements sanitisation, authorization, and
        // independent retention (FR-DATA-005, FR-DATA-006, ADR 0004).
        var options = Valid();
        options.Capture.ContentEnabled = true;

        AssertFailure(options, "contentEnabled must be false");
    }

    [Fact]
    public void An_unimplemented_persistence_mode_is_refused_rather_than_served_by_sqlite()
    {
        // FR-DATA-003 commits to PostgreSQL through the same contracts and the model is kept
        // provider-neutral for it, but no provider ships. A deployment that asked for PostgreSQL and
        // silently got a local file would find out by going looking for its data.
        var options = Valid();
        options.Persistence.Mode = PersistenceMode.Postgres;

        AssertFailure(options, "has no provider in this build");
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:00.0005")]
    [InlineData("49.00:00:00.001")]
    [InlineData("60.00:00:00")]
    public void The_retention_sweep_interval_must_be_between_a_millisecond_and_49_days(string interval)
    {
        // Not merely positive. The timer that drives the sweep refuses less than a millisecond and more
        // than about 49 days and 17 hours, and since .NET 10 it refuses after startup rather than during
        // it: the gateway reports itself started and then exits with code 0 (ADR 0024).
        var options = Valid();
        options.Capture.Retention.SweepInterval = TimeSpan.Parse(interval, CultureInfo.InvariantCulture);

        AssertFailure(options, "sweepInterval must be at least one millisecond and at most 49 days");
    }

    [Theory]
    [InlineData("metadata", "00:00:00")]
    [InlineData("metadata", "36500.00:00:00.001")]
    [InlineData("metadata", "3650000.00:00:00")]
    [InlineData("content", "00:00:00")]
    [InlineData("content", "36500.00:00:00.001")]
    public void Retention_windows_must_be_positive_and_at_most_a_hundred_years(string window, string length)
    {
        // Bounded above because a window is subtracted from the current time to find what expired, and
        // one reaching back past the year 1 throws at the startup sweep with an error that names nothing.
        var options = Valid();
        var value = TimeSpan.Parse(length, CultureInfo.InvariantCulture);

        if (window == "metadata")
        {
            options.Capture.Retention.Metadata = value;
        }
        else
        {
            options.Capture.Retention.Content = value;
        }

        AssertFailure(options, $"retention:{window} must be greater than zero and at most 36500 days");
    }

    [Fact]
    public void Retention_settings_at_their_bounds_are_accepted()
    {
        var options = Valid();
        options.Capture.Retention.Metadata = TimeSpan.FromDays(36500);
        options.Capture.Retention.Content = TimeSpan.FromDays(36500);
        options.Capture.Retention.SweepInterval = TimeSpan.FromDays(49);

        Assert.True(Validate(options).Succeeded);

        options.Capture.Retention.SweepInterval = TimeSpan.FromMilliseconds(1);

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Storing_bodies_is_refused_because_nothing_in_this_build_stores_one()
    {
        var options = Valid();
        options.Diagnostics.StoreBodies = true;

        AssertFailure(options, "agentsplice:diagnostics:storeBodies must be false");
    }

    [Theory]
    [InlineData(HeaderCaptureMode.Allowlist)]
    [InlineData(HeaderCaptureMode.All)]
    public void Retaining_headers_is_refused_because_nothing_in_this_build_retains_one(HeaderCaptureMode mode)
    {
        var options = Valid();
        options.Diagnostics.StoreHeaders = mode;

        AssertFailure(options, "agentsplice:diagnostics:storeHeaders must be None");
    }

    [Fact]
    public void An_unrecognised_header_capture_mode_fails()
    {
        var options = Valid();
        options.Diagnostics.StoreHeaders = (HeaderCaptureMode)99;

        AssertFailure(options, "not a recognised header capture mode");
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
    public void An_omitted_default_runtime_is_accepted()
    {
        var options = Valid();
        options.DefaultRuntimeId = null;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void A_default_runtime_naming_a_configured_enabled_runtime_is_accepted()
    {
        var options = Valid();
        options.DefaultRuntimeId = "lmstudio-local";

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void An_unknown_default_runtime_fails()
    {
        var options = Valid();
        options.DefaultRuntimeId = "not-configured";

        AssertFailure(options, "does not match any configured runtime");
    }

    [Fact]
    public void A_disabled_default_runtime_fails()
    {
        // Configured but switched off would make every unrecognised model fail at request time
        // against a runtime the operator believes is in use.
        var options = Valid();
        options.Runtimes[0].Enabled = false;
        options.DefaultRuntimeId = "lmstudio-local";

        AssertFailure(options, "is not enabled");
    }

    [Fact]
    public void A_malformed_default_runtime_identifier_fails()
    {
        var options = Valid();
        options.DefaultRuntimeId = "lm studio";

        AssertFailure(options, "is not a valid runtime identifier");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_request_body_limit_fails(long value)
    {
        var options = Valid();
        options.Limits.MaxRequestBodyBytes = value;

        AssertFailure(options, "maxRequestBodyBytes");
    }

    [Fact]
    public void A_non_positive_upstream_completion_limit_fails()
    {
        var options = Valid();
        options.Limits.MaxUpstreamCompletionBodyBytes = 0;

        AssertFailure(options, "maxUpstreamCompletionBodyBytes");
    }

    [Fact]
    public void A_non_positive_catalogue_limit_fails()
    {
        var options = Valid();
        options.Limits.MaxCatalogueBodyBytes = 0;

        AssertFailure(options, "maxCatalogueBodyBytes");
    }

    [Fact]
    public void The_default_limits_are_accepted()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void Enabling_telemetry_export_without_an_endpoint_fails()
    {
        // Never defaulted to the SDK's localhost, and never taken from OTEL_EXPORTER_OTLP_ENDPOINT.
        var options = Valid();
        options.Telemetry.Otlp.Enabled = true;

        AssertFailure(options, "agentsplice:telemetry:otlp:endpoint is required");
    }

    [Theory]
    [InlineData(TelemetryExportProtocol.HttpProtobuf, "http://127.0.0.1:4318")]
    [InlineData(TelemetryExportProtocol.HttpProtobuf, "https://collector.internal/otlp/")]
    [InlineData(TelemetryExportProtocol.Grpc, "http://otel-collector:4317")]
    [InlineData(TelemetryExportProtocol.Grpc, "http://otel-collector:4317/")]
    public void An_enabled_export_with_a_receiver_address_passes(TelemetryExportProtocol protocol, string endpoint)
    {
        var options = Valid();
        options.Telemetry.Otlp.Enabled = true;
        options.Telemetry.Otlp.Protocol = protocol;
        options.Telemetry.Otlp.Endpoint = endpoint;

        var result = Validate(options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("127.0.0.1:4318")]
    [InlineData("udp://127.0.0.1:4318")]
    public void A_telemetry_endpoint_must_be_an_absolute_http_url(string endpoint)
    {
        var options = Valid();
        options.Telemetry.Otlp.Endpoint = endpoint;

        AssertFailure(options, "absolute http or https URL");
    }

    [Fact]
    public void A_telemetry_endpoint_is_validated_even_while_export_is_off()
    {
        // Refused by the deployment that wrote it, not by the one that later flips the switch.
        var options = Valid();
        options.Telemetry.Otlp.Enabled = false;
        options.Telemetry.Otlp.Endpoint = "not a url";

        AssertFailure(options, "absolute http or https URL");
    }

    [Fact]
    public void A_telemetry_endpoint_must_not_embed_credentials()
    {
        // The endpoint is logged when export starts.
        var options = Valid();
        options.Telemetry.Otlp.Endpoint = "http://user:secret@127.0.0.1:4318";

        AssertFailure(options, "must not embed credentials");
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318/?token=abc")]
    [InlineData("http://127.0.0.1:4318/#fragment")]
    public void A_telemetry_endpoint_must_not_carry_a_query_or_fragment(string endpoint)
    {
        var options = Valid();
        options.Telemetry.Otlp.Endpoint = endpoint;

        AssertFailure(options, "must not carry a query or fragment");
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318/v1/traces")]
    [InlineData("http://127.0.0.1:4318/v1/metrics/")]
    [InlineData("http://127.0.0.1:4318/otlp/V1/LOGS")]
    public void An_http_telemetry_endpoint_must_name_the_receiver_not_a_signal(string endpoint)
    {
        // Pasting a signal URL would export to /v1/traces/v1/traces, and an exporter's 404 reaches
        // nothing an operator watches.
        var options = Valid();
        options.Telemetry.Otlp.Endpoint = endpoint;

        AssertFailure(options, "names a signal path");
    }

    [Fact]
    public void No_telemetry_endpoint_failure_repeats_an_embedded_credential()
    {
        // A signal path and a credential in one endpoint trip two rules. The credential rule refuses
        // without quoting; the signal-path rule must not quote the whole endpoint either, or the
        // startup error carries the secret the first rule was protecting.
        var options = Valid();
        options.Telemetry.Otlp.Endpoint = "http://user:SENTINEL-OTLP-SECRET@127.0.0.1:4318/v1/traces";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("must not embed credentials", StringComparison.Ordinal));
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("names a signal path", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures ?? [], failure => failure.Contains("SENTINEL-OTLP-SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void A_grpc_telemetry_endpoint_must_not_carry_a_path()
    {
        var options = Valid();
        options.Telemetry.Otlp.Protocol = TelemetryExportProtocol.Grpc;
        options.Telemetry.Otlp.Endpoint = "http://127.0.0.1:4317/otlp";

        AssertFailure(options, "must not carry a path when the protocol is 'Grpc'");
    }

    [Fact]
    public void An_unrecognised_telemetry_protocol_fails()
    {
        var options = Valid();
        options.Telemetry.Otlp.Protocol = (TelemetryExportProtocol)42;

        AssertFailure(options, "agentsplice:telemetry:otlp:protocol is not a recognised protocol");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_dashboard_root_path_fails(string? rootPath)
    {
        // A blank path would resolve to the content root itself, and serve whatever happens to be
        // there. Turning the dashboard off is what the enabled switch is for.
        var options = Valid();
        options.Dashboard.RootPath = rootPath!;

        AssertFailure(options, "agentsplice:dashboard:rootPath must name the directory");
    }

    [Fact]
    public void A_dashboard_root_path_is_validated_even_while_the_dashboard_is_disabled()
    {
        // As with a disabled export endpoint: a value that would be refused the moment someone flips
        // the switch is better refused now, by the deployment that wrote it.
        var options = Valid();
        options.Dashboard.Enabled = false;
        options.Dashboard.RootPath = " ";

        AssertFailure(options, "agentsplice:dashboard:rootPath must name the directory");
    }

    [Fact]
    public void A_dashboard_root_path_that_does_not_exist_is_not_a_validation_failure()
    {
        // A gateway run from source has no build until someone runs pnpm build, and the dashboard is
        // optional (FR-DASH-002). A missing build is reported when the host starts, not refused here.
        var options = Valid();
        options.Dashboard.RootPath = Path.Combine(Path.GetTempPath(), "agentsplice-no-such-dashboard", Guid.NewGuid().ToString("N"));

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
