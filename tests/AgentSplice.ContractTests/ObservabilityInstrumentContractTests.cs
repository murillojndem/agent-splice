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

        foreach (var instrument in TelemetryNames.Stage1AInstruments)
        {
            Assert.Contains(instrument, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_bounded_attribute_is_published_in_the_observability_document()
    {
        var document = ObservabilityDocument();

        foreach (var attribute in TelemetryNames.Stage1AAttributes)
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
                "### Stage 1A instruments",
                "### Stage 1A dimensions")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, TelemetryNames.Stage1AInstruments.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void No_streaming_instrument_is_declared_yet()
    {
        // A non-streamed exchange offers no boundary to measure these against, and emitting a zero
        // would read as "this happened, and it was none" (FR-OBS-004, FR-OBS-005).
        string[] deferred =
        [
            "agentsplice.stream.events",
            "agentsplice.stream.bytes",
            "agentsplice.time_to_first_byte",
            "agentsplice.time_to_first_semantic_event",
            "agentsplice.time_to_first_client_event",
            "agentsplice.prompt.tokens_per_second",
            "agentsplice.generation.tokens_per_second",
        ];

        foreach (var instrument in deferred)
        {
            Assert.DoesNotContain(instrument, TelemetryNames.Stage1AInstruments, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void No_dimension_carries_a_model_identifier_or_a_request_identifier()
    {
        // Both are unbounded and client-supplied, so either would let one caller multiply the
        // cardinality of every series without limit.
        foreach (var attribute in TelemetryNames.Stage1AAttributes)
        {
            Assert.DoesNotContain("model", attribute, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request.id", attribute, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request_id", attribute, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_declared_instrument_name_uses_the_agentsplice_prefix()
    {
        foreach (var instrument in TelemetryNames.Stage1AInstruments)
        {
            Assert.StartsWith("agentsplice.", instrument, StringComparison.Ordinal);
        }
    }

    private static string ObservabilityDocument() => RepositoryPaths.ReadText("docs", "OBSERVABILITY.md");
}
