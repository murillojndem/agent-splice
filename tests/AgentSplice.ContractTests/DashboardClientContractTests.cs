using System.Text.RegularExpressions;
using AgentSplice.ContractTests.Documents;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// Every path the dashboard calls is one the OpenAPI document publishes (ADR 0027).
/// </summary>
/// <remarks>
/// The dashboard's client is the one consumer of <c>/api/v1</c> shipped with the gateway, and a path it
/// called that the gateway did not serve would show as an empty screen rather than a failing build.
/// The client's paths are read from its source, so a path added there and not to the document fails
/// here.
/// </remarks>
public sealed partial class DashboardClientContractTests
{
    private static readonly string Client = RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "src", "api", "client.ts");

    [Fact]
    public void Every_path_the_dashboard_client_calls_is_a_published_path()
    {
        var published = OpenApiDocument.Load()
            .Paths()
            .Select(path => PathParameter().Replace(path, "{}"))
            .ToHashSet(StringComparer.Ordinal);

        var unpublished = ClientPaths().Where(path => !published.Contains(path)).ToArray();

        Assert.True(unpublished.Length == 0, "The dashboard calls unpublished paths: " + string.Join(", ", unpublished));
    }

    [Fact]
    public void Every_request_the_dashboard_client_makes_names_its_path_where_this_contract_can_read_it()
    {
        // A call whose path the pattern above cannot read would pass the first test unchecked.
        Assert.Equal(CallSite().Count(Client), ClientPaths().Count());
        Assert.NotEmpty(ClientPaths());
    }

    /// <summary>The paths client.ts requests, with each parameter written as <c>{}</c> and no query.</summary>
    private static IEnumerable<string> ClientPaths() =>
        RequestedPath()
            .Matches(Client)
            .Select(match => match.Groups["path"].Value)
            .Select(path => Interpolation().Replace(path, interpolation =>
                // A parameter follows a slash (`/${encodeURIComponent(id)}`); a query does not (`${suffix}`).
                interpolation.Index > 0 && path[interpolation.Index - 1] == '/' ? "{}" : string.Empty));

    [GeneratedRegex(@"(?<!function )\brequest<[^>]+>\(")]
    private static partial Regex CallSite();

    [GeneratedRegex(@"\brequest<[^>]+>\(\s*[`'](?<path>/api/v1[^`']*)[`']")]
    private static partial Regex RequestedPath();

    [GeneratedRegex(@"\$\{[^}]*\}")]
    private static partial Regex Interpolation();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex PathParameter();
}
