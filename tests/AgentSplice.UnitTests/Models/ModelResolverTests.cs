using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using Xunit;

namespace AgentSplice.UnitTests.Models;

/// <summary>
/// Deterministic resolution of a client-visible model identifier
/// (docs/SPECIFICATION.md FR-MOD-005, FR-TRACE-007).
/// </summary>
public sealed class ModelResolverTests
{
    [Fact]
    public async Task An_enabled_alias_resolves_to_its_runtime_and_upstream_model()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "local-coder");

        Assert.True(outcome.Succeeded);
        Assert.Equal(ModelResolutionSource.ConfiguredAlias, outcome.Resolution?.Source);
        Assert.Equal("lmstudio-local", outcome.Resolution?.Runtime.Value);
        Assert.Equal("qwen3.6-27b-mtp", outcome.Resolution?.UpstreamModel.Value);
        Assert.Equal("local-coder", outcome.Resolution?.Alias?.Value);
    }

    [Fact]
    public async Task An_alias_resolves_without_contacting_any_runtime()
    {
        // An alias-only deployment must never pay for discovery on the request path.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        await Resolve(fixture, "local-coder");

        Assert.Equal(0, fixture.Provider.CallCount);
    }

    [Fact]
    public async Task An_alias_that_renames_the_model_requires_a_body_rewrite()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "local-coder");

        Assert.True(outcome.RequiresBodyRewrite);
        Assert.True(outcome.RoutingWasApplied);
    }

    [Fact]
    public async Task An_alias_that_does_not_rename_still_counts_as_a_routing_decision()
    {
        // The identifier is unchanged, so no byte of the body moves, but AgentSplice still chose the
        // destination and FR-TRACE-007 requires that to be visible.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("qwen3.6-27b-mtp", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.False(outcome.RequiresBodyRewrite);
        Assert.True(outcome.RoutingWasApplied);
    }

    [Fact]
    public async Task An_alias_records_its_identifier_and_runtime_as_safe_detail()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "local-coder");

        Assert.Equal("local-coder", outcome.Details.Values["alias.id"]);
        Assert.Equal("lmstudio-local", outcome.Details.Values["runtime.id"]);
        Assert.Equal("configured_alias", outcome.Details.Values["resolution.source"]);
    }

    [Fact]
    public async Task Resolution_is_case_sensitive()
    {
        // Clients echo back the exact string GET /v1/models handed them.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("Local-Coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Offers("lmstudio-local")
            .Create();

        Assert.False((await Resolve(fixture, "local-coder")).Succeeded);
        Assert.True((await Resolve(fixture, "Local-Coder")).Succeeded);
    }

    [Fact]
    public async Task A_disabled_alias_does_not_resolve()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp", enabled: false)
            .Offers("lmstudio-local")
            .Create();

        var outcome = await Resolve(fixture, "local-coder");

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureClass.ModelNotFound, outcome.Failure);
        Assert.Equal("disabled", outcome.Details.Values["alias.skipped"]);
    }

    [Fact]
    public async Task A_disabled_alias_does_not_shadow_a_discovered_model_of_the_same_name()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("shared-name", "lmstudio-local", "something-else", enabled: false)
            .Offers("lmstudio-local", "shared-name")
            .Create();

        var outcome = await Resolve(fixture, "shared-name");

        Assert.True(outcome.Succeeded);
        Assert.Equal(ModelResolutionSource.Discovered, outcome.Resolution?.Source);
        Assert.Equal("disabled", outcome.Details.Values["alias.skipped"]);
    }

    [Fact]
    public async Task An_alias_pointing_at_a_disabled_runtime_does_not_resolve()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local", enabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "local-coder");

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureClass.ModelNotFound, outcome.Failure);
        Assert.Equal("runtime_disabled", outcome.Details.Values["alias.skipped"]);
    }

    [Fact]
    public async Task An_alias_wins_over_a_discovered_model_with_the_same_identifier()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("shared-name", "lmstudio-local", "renamed-target")
            .Offers("lmstudio-local", "shared-name")
            .Create();

        var outcome = await Resolve(fixture, "shared-name");

        Assert.Equal(ModelResolutionSource.ConfiguredAlias, outcome.Resolution?.Source);
        Assert.Equal("renamed-target", outcome.Resolution?.UpstreamModel.Value);
    }

    [Fact]
    public async Task A_discovered_model_resolves_when_no_alias_matches()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.Equal(ModelResolutionSource.Discovered, outcome.Resolution?.Source);
        Assert.False(outcome.RequiresBodyRewrite);
        Assert.False(outcome.RoutingWasApplied);
    }

    [Fact]
    public async Task A_duplicate_discovered_identifier_resolves_to_the_first_configured_runtime()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Offers("first", "shared-model")
            .Offers("second", "shared-model")
            .Create();

        var outcome = await Resolve(fixture, "shared-model");

        Assert.Equal("first", outcome.Resolution?.Runtime.Value);
    }

    [Fact]
    public async Task A_duplicate_discovered_identifier_records_the_ambiguity_as_a_routing_decision()
    {
        // Choosing between two runtimes is a decision the client did not make.
        var fixture = CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Offers("first", "shared-model")
            .Offers("second", "shared-model")
            .Create();

        var outcome = await Resolve(fixture, "shared-model");

        Assert.True(outcome.RoutingWasApplied);
        Assert.False(outcome.RequiresBodyRewrite);
        Assert.Equal("true", outcome.Details.Values["resolution.ambiguous"]);
        Assert.Equal("2", outcome.Details.Values["resolution.candidates"]);
    }

    [Fact]
    public async Task An_unambiguous_discovery_match_records_no_ambiguity()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Offers("first", "only-here")
            .Offers("second", "elsewhere")
            .Create();

        var outcome = await Resolve(fixture, "only-here");

        Assert.False(outcome.Details.Values.ContainsKey("resolution.ambiguous"));
    }

    [Fact]
    public async Task A_discovery_disabled_runtime_contributes_no_discovered_models()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Offers("lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, fixture.Provider.CallCount);
    }

    [Fact]
    public async Task A_discovery_disabled_runtime_still_resolves_its_aliases()
    {
        // The recommended posture for a stable deployment: route by alias, leave discovery off.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        Assert.True((await Resolve(fixture, "local-coder")).Succeeded);
    }

    [Fact]
    public async Task An_unknown_identifier_does_not_resolve_when_no_default_runtime_is_configured()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        var outcome = await Resolve(fixture, "never-heard-of-it");

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureClass.ModelNotFound, outcome.Failure);
    }

    [Fact]
    public async Task Resolution_blocked_by_a_failed_discovery_is_not_reported_as_model_not_found()
    {
        // "The model does not exist" and "AgentSplice could not ask" are different facts, and
        // reporting the first when the second is true is the misleading evidence this product exists
        // to remove.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Fails("lmstudio-local")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureClass.RuntimeUnavailable, outcome.Failure);
        Assert.Equal("discovery_unavailable", outcome.Details.Values["resolution.blocked"]);
    }

    [Fact]
    public async Task A_reachable_runtime_that_offers_nothing_reports_the_model_as_absent()
    {
        // The catalogue was consulted successfully; the model genuinely is not there.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.Equal(FailureClass.ModelNotFound, outcome.Failure);
    }

    [Fact]
    public async Task One_reachable_runtime_is_enough_to_report_a_model_as_absent()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("healthy")
            .Runtime("broken")
            .Offers("healthy", "something-else")
            .Fails("broken")
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.Equal(FailureClass.ModelNotFound, outcome.Failure);
    }

    [Fact]
    public async Task A_stale_catalogue_still_resolves_and_records_that_it_was_stale()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local", cacheDuration: TimeSpan.FromSeconds(30))
            .Offers("lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        await Resolve(fixture, "qwen3.6-27b-mtp");
        fixture.Provider.Fails("lmstudio-local");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.True(outcome.Succeeded);
        Assert.Equal("true", outcome.Details.Values["discovery.stale"]);
    }

    [Fact]
    public async Task An_unknown_identifier_passes_through_to_the_configured_default_runtime()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local")
            .DefaultRuntime("lmstudio-local")
            .Create();

        var outcome = await Resolve(fixture, "some-local-gguf");

        Assert.True(outcome.Succeeded);
        Assert.Equal(ModelResolutionSource.PassThrough, outcome.Resolution?.Source);
        Assert.Equal("some-local-gguf", outcome.Resolution?.UpstreamModel.Value);
        Assert.False(outcome.RequiresBodyRewrite);
    }

    [Fact]
    public async Task Pass_through_is_recorded_as_a_routing_decision()
    {
        // The client named no runtime, so AgentSplice picked one.
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local")
            .DefaultRuntime("lmstudio-local")
            .Create();

        var outcome = await Resolve(fixture, "some-local-gguf");

        Assert.True(outcome.RoutingWasApplied);
        Assert.Equal("pass_through", outcome.Details.Values["resolution.source"]);
    }

    [Fact]
    public async Task Pass_through_does_not_override_an_alias_or_a_discovered_model()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Alias("aliased", "first", "renamed")
            .Offers("first", "discovered-here")
            .Offers("second")
            .DefaultRuntime("second")
            .Create();

        Assert.Equal(ModelResolutionSource.ConfiguredAlias, (await Resolve(fixture, "aliased")).Resolution?.Source);
        Assert.Equal(ModelResolutionSource.Discovered, (await Resolve(fixture, "discovered-here")).Resolution?.Source);
    }

    [Fact]
    public async Task A_runtime_whose_provider_module_is_missing_contributes_nothing()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .WithNoProviderModule()
            .Create();

        var outcome = await Resolve(fixture, "qwen3.6-27b-mtp");

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task An_empty_requested_identifier_is_a_programming_error_rather_than_a_resolution_failure()
    {
        var fixture = CatalogueFixture.Build().Runtime("lmstudio-local").Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Resolver.ResolveAsync(default, CancellationToken.None));
    }

    private static Task<Application.Models.ModelResolutionOutcome> Resolve(
        CatalogueFixture fixture,
        string requested) =>
        fixture.Resolver.ResolveAsync(ClientModelId.Create(requested), CancellationToken.None);
}
