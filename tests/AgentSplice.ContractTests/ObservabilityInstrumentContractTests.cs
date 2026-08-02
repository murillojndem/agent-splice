using AgentSplice.ContractTests.Documents;
using AgentSplice.Observability;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// Instrument and dimension names are what external collectors and dashboards subscribe to, so they
/// are bound to docs/OBSERVABILITY.md (FR-OBS-002, FR-OBS-006).
/// </summary>
public sealed class ObservabilityInstrumentContractTests
{
    [Fact]
    public void Every_live_instrument_is_published_in_the_observability_document()
    {
        var document = ObservabilityDocument();

        foreach (var instrument in TelemetryNames.LiveInstruments)
        {
            Assert.Contains(instrument, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_bounded_attribute_is_published_in_the_observability_document()
    {
        var document = ObservabilityDocument();

        foreach (var attribute in TelemetryNames.LiveAttributes)
        {
            Assert.Contains(attribute, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_live_instruments_are_exactly_those_the_document_marks_as_live()
    {
        var documented = MarkdownLists
            .InlineCodeBullets(
                ObservabilityDocument(),
                "### Live instruments",
                "### Live dimensions")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, TelemetryNames.LiveInstruments.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void The_live_dimensions_are_exactly_those_the_document_marks_as_live()
    {
        var documented = MarkdownLists
            .InlineCodeBullets(
                ObservabilityDocument(),
                "### Live dimensions",
                "### Tracing")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, TelemetryNames.LiveAttributes.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void No_prompt_throughput_instrument_is_declared()
    {
        // Not deferred — absent by design. Nothing AgentSplice can observe marks the end of prompt
        // processing, so the only interval available is time to first token, which measures the
        // prompt, the queue, and the network together. Publishing that under a prompt-throughput
        // name is the exact conflation FR-OBS-005 exists to prevent, and no stage fixes it without
        // runtime-log evidence.
        Assert.DoesNotContain(
            "agentsplice.prompt.tokens_per_second",
            TelemetryNames.LiveInstruments,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Every_activity_source_the_listener_subscribes_to_has_something_that_writes_to_it()
    {
        // A source nothing writes to is a permanently empty panel on a dashboard, which reads as a
        // capability that exists and produced nothing. Every Stage 1 source now has a producer:
        // agentsplice.persistence gained one with the metadata writer.
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                TelemetryNames.ActivitySources.Exchange,
                TelemetryNames.ActivitySources.ProviderRequest,
                TelemetryNames.ActivitySources.Stream,
                TelemetryNames.ActivitySources.Persistence,
            },
            TelemetryNames.LiveActivitySources.ToHashSet(StringComparer.Ordinal));

        Assert.Subset(
            TelemetryNames.Stage1ActivitySources.ToHashSet(StringComparer.Ordinal),
            TelemetryNames.LiveActivitySources.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void No_dimension_carries_a_model_identifier_or_a_request_identifier()
    {
        // Both are unbounded and client-supplied, so either would let one caller multiply the
        // cardinality of every series without limit.
        foreach (var attribute in TelemetryNames.LiveAttributes)
        {
            Assert.DoesNotContain("model", attribute, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request.id", attribute, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request_id", attribute, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_declared_instrument_name_uses_the_agentsplice_prefix()
    {
        foreach (var instrument in TelemetryNames.LiveInstruments)
        {
            Assert.StartsWith("agentsplice.", instrument, StringComparison.Ordinal);
        }
    }

    private static string ObservabilityDocument() => RepositoryPaths.ReadText("docs", "OBSERVABILITY.md");
}
