using System.Text.RegularExpressions;
using AgentSplice.Application.Administration;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// The stage <c>GET /api/v1/system</c> reports is the one the roadmap says is in progress.
/// </summary>
/// <remarks>
/// The value was a literal in the response and stayed "1C" through every Stage 1D change, until the
/// first trace report read it (ADR 0023). A stage that moves on in the roadmap now fails this test
/// until the build says so too.
/// </remarks>
public sealed partial class ImplementedStageContractTests
{
    [Fact]
    public void The_build_reports_the_stage_the_roadmap_marks_in_progress()
    {
        var roadmap = RepositoryPaths.ReadText("docs", "ROADMAP.md").Replace("\r\n", "\n", StringComparison.Ordinal);

        var inProgress = StageInProgress().Matches(roadmap).Select(match => match.Groups["stage"].Value).ToArray();

        Assert.Equal(DiagnosticsService.ImplementedStage, Assert.Single(inProgress));
    }

    [GeneratedRegex(@"^#{2,3} Stage (?<stage>[0-9][A-Z]?) — [^\n]*\n\nStatus: in progress", RegexOptions.Multiline)]
    private static partial Regex StageInProgress();
}
