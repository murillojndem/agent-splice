using AgentSplice.Observability;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// OpenTelemetry names are what external collectors subscribe to, so the constants must match
/// docs/SPECIFICATION.md section 15.4 rather than merely resemble it.
/// </summary>
public sealed class TelemetryNameContractTests
{
    [Fact]
    public void The_meter_name_matches_the_specification()
    {
        Assert.Contains(
            "Proposed meter: `AgentSplice`",
            Specification(),
            StringComparison.Ordinal);

        Assert.Equal("AgentSplice", TelemetryNames.Meter);
    }

    [Fact]
    public void Every_declared_activity_source_appears_in_the_specification()
    {
        var specification = Specification();

        foreach (var source in TelemetryNames.Stage1ActivitySources)
        {
            Assert.Contains(source, specification, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_declared_set_matches_the_stage_1_activity_sources_exactly()
    {
        string[] expected =
        [
            "agentsplice.exchange",
            "agentsplice.provider.request",
            "agentsplice.stream",
            "agentsplice.persistence",
        ];

        Assert.Equal(
            expected.ToHashSet(StringComparer.Ordinal),
            TelemetryNames.Stage1ActivitySources.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Later_stage_activity_sources_are_not_declared_yet()
    {
        // Declaring a span name before anything emits it would let a dashboard show a permanently
        // empty panel and read as a capability that exists.
        string[] laterStages =
        [
            "agentsplice.replay",
            "agentsplice.conformance.case",
            "agentsplice.evaluation.run",
            "agentsplice.adapter",
        ];

        foreach (var source in laterStages)
        {
            Assert.False(
                TelemetryNames.Stage1ActivitySources.Contains(source),
                FormattableString.Invariant($"'{source}' belongs to a later stage and must not be declared yet."));
        }
    }

    private static string Specification() => RepositoryPaths.ReadText("docs", "SPECIFICATION.md");
}
