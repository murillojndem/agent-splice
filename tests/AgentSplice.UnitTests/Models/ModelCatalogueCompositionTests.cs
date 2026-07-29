using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Runtimes;
using Xunit;

namespace AgentSplice.UnitTests.Models;

/// <summary>
/// Composition of the client-visible catalogue (docs/SPECIFICATION.md FR-MOD-002, FR-MOD-004,
/// FR-MOD-007).
/// </summary>
public sealed class ModelCatalogueCompositionTests
{
    [Fact]
    public async Task Configured_aliases_and_discovered_models_are_combined()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp", "phi-4"));

        Assert.Equal(
            ["local-coder", "qwen3.6-27b-mtp", "phi-4"],
            result.Entries.Select(entry => entry.ClientModel.Value));
    }

    [Fact]
    public async Task A_disabled_alias_is_not_offered()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("hidden", "lmstudio-local", "qwen3.6-27b-mtp", enabled: false)
            .Offers("lmstudio-local"));

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task A_disabled_runtime_contributes_nothing()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local", enabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.Empty(result.Entries);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public async Task A_discovery_disabled_runtime_still_offers_its_aliases()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.Equal(["local-coder"], result.Entries.Select(entry => entry.ClientModel.Value));
    }

    [Fact]
    public async Task A_discovery_disabled_runtime_is_not_reported_as_a_discovery_failure()
    {
        // Otherwise a deliberately discovery-free deployment would look like a total outage and the
        // endpoint would answer 502 instead of listing its aliases.
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.False(result.AnyDiscoveryAttempted);
        Assert.False(result.EveryDiscoveryAttemptFailed);
    }

    [Fact]
    public async Task A_duplicate_client_visible_identifier_appears_once()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Offers("first", "shared-model")
            .Offers("second", "shared-model"));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("shared-model", entry.ClientModel.Value);
        Assert.Equal("first", entry.Runtime.Value);
    }

    [Fact]
    public async Task The_client_visible_identifier_is_never_a_composite_of_runtime_and_model()
    {
        // FR-MOD-004 disambiguates internally. A composite identifier would be a value AgentSplice
        // invented, and a client copying it back into "model" would send something no runtime knows.
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Offers("first", "shared-model")
            .Offers("second", "shared-model"));

        Assert.All(
            result.Entries,
            entry => Assert.DoesNotContain('/', entry.ClientModel.Value));
    }

    [Fact]
    public async Task An_alias_shadows_a_discovered_model_with_the_same_identifier()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("shared-name", "lmstudio-local", "renamed-target")
            .Offers("lmstudio-local", "shared-name"));

        var entry = Assert.Single(result.Entries, candidate => candidate.ClientModel.Value == "shared-name");
        Assert.Equal(ModelResolutionSource.ConfiguredAlias, entry.Source);
    }

    [Fact]
    public async Task A_configured_alias_carries_configured_capability_provenance()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .Offers("lmstudio-local"));

        Assert.Equal(CapabilityProvenance.Configured, result.Entries.Single().CapabilityProvenance);
    }

    [Fact]
    public async Task A_discovered_model_carries_discovered_capability_provenance()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.Equal(CapabilityProvenance.Discovered, result.Entries.Single().CapabilityProvenance);
    }

    [Fact]
    public async Task A_discovered_model_passes_through_the_creation_time_the_runtime_reported()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .OffersWithMetadata("lmstudio-local", "qwen3.6-27b-mtp", created: 1_700_000_000, ownedBy: "organization_owner"));

        var entry = result.Entries.Single();
        Assert.Equal(1_700_000_000, entry.Created);
        Assert.Equal("organization_owner", entry.OwnedBy);
    }

    [Fact]
    public async Task An_unreported_creation_time_stays_unknown_rather_than_becoming_zero()
    {
        // Zero is a Unix timestamp meaning 1970-01-01, which would be a fabricated fact. The
        // substitution the OpenAI schema forces happens at the envelope and nowhere else.
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .OffersWithMetadata("lmstudio-local", "qwen3.6-27b-mtp", created: null, ownedBy: null));

        Assert.Null(result.Entries.Single().Created);
    }

    [Fact]
    public async Task An_alias_inherits_the_creation_evidence_of_the_model_it_targets()
    {
        // The runtime reported when that model was created, and the alias points at it, so the
        // value is real evidence rather than an invention.
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp")
            .OffersWithMetadata("lmstudio-local", "qwen3.6-27b-mtp", created: 1_700_000_000, ownedBy: "org"));

        var alias = result.Entries.Single(entry => entry.ClientModel.Value == "local-coder");
        Assert.Equal(1_700_000_000, alias.Created);
        Assert.Equal("org", alias.OwnedBy);
    }

    [Fact]
    public async Task An_alias_with_no_discoverable_target_reports_no_creation_evidence()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Alias("local-coder", "lmstudio-local", "qwen3.6-27b-mtp"));

        var alias = result.Entries.Single();
        Assert.Null(alias.Created);
        Assert.Null(alias.OwnedBy);
    }

    [Fact]
    public async Task A_partial_discovery_failure_still_yields_what_is_known()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("healthy")
            .Runtime("broken")
            .Offers("healthy", "reachable-model")
            .Fails("broken"));

        Assert.Equal(["reachable-model"], result.Entries.Select(entry => entry.ClientModel.Value));
        Assert.True(result.AnyDiscoveryAttempted);
        Assert.False(result.EveryDiscoveryAttemptFailed);
    }

    [Fact]
    public async Task A_total_discovery_failure_is_reported_as_such()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("first")
            .Runtime("second")
            .Fails("first")
            .Fails("second"));

        Assert.Empty(result.Entries);
        Assert.True(result.EveryDiscoveryAttemptFailed);
    }

    [Fact]
    public async Task An_empty_configuration_reports_no_discovery_attempt()
    {
        // Nothing configured is an operator fact, not an upstream outage.
        var result = await Compose(CatalogueFixture.Build());

        Assert.Empty(result.Entries);
        Assert.False(result.AnyDiscoveryAttempted);
        Assert.False(result.EveryDiscoveryAttemptFailed);
    }

    [Fact]
    public async Task A_runtime_that_answers_with_no_models_is_reachable_but_reported_as_offering_none()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local"));

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(RuntimeHealthStatus.NoModels, outcome.Status);
        Assert.True(outcome.YieldedCatalogue);
    }

    [Fact]
    public async Task A_healthy_runtime_is_reported_as_healthy()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Offers("lmstudio-local", "qwen3.6-27b-mtp"));

        Assert.Equal(RuntimeHealthStatus.Healthy, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task An_authentication_failure_is_reported_distinctly_from_unreachability()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .Fails("lmstudio-local", Application.Runtimes.UpstreamFailureReason.AuthenticationRejected));

        Assert.Equal(RuntimeHealthStatus.AuthenticationFailed, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task A_runtime_whose_provider_module_is_missing_is_reported_as_incompatible()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local")
            .WithNoProviderModule());

        Assert.Equal(RuntimeHealthStatus.IncompatibleResponse, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public async Task Aliases_are_listed_in_priority_then_declaration_order()
    {
        var result = await Compose(CatalogueFixture.Build()
            .Runtime("lmstudio-local", discoveryEnabled: false)
            .Alias("third", "lmstudio-local", "model-c", priority: 10)
            .Alias("first", "lmstudio-local", "model-a", priority: 1)
            .Alias("second", "lmstudio-local", "model-b", priority: 1));

        Assert.Equal(["first", "second", "third"], result.Entries.Select(entry => entry.ClientModel.Value));
    }

    [Fact]
    public async Task A_stale_catalogue_marks_its_models_unreachable()
    {
        var fixture = CatalogueFixture.Build()
            .Runtime("lmstudio-local", cacheDuration: TimeSpan.FromSeconds(30))
            .Offers("lmstudio-local", "qwen3.6-27b-mtp")
            .Create();

        await fixture.Catalogue.ComposeAsync(CancellationToken.None);
        fixture.Provider.Fails("lmstudio-local");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await fixture.Catalogue.ComposeAsync(CancellationToken.None);

        Assert.False(result.Entries.Single().Reachable);
        Assert.True(Assert.Single(result.Outcomes).ServedFromStaleCache);
    }

    private static Task<Application.Models.ModelCatalogueResult> Compose(CatalogueFixture.Builder builder) =>
        builder.Create().Catalogue.ComposeAsync(CancellationToken.None);
}
