using AgentSplice.Application.Errors;
using AgentSplice.ContractTests.Documents;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// The administrative surface's published contract: what it is protected by, and what it can answer
/// with (FR-DASH-001, FR-HEALTH-006).
/// </summary>
/// <remarks>
/// A route that produces a status the document does not declare is a client that cannot handle it,
/// and an authenticated route the document shows as open is worse: an integrator reads the contract
/// and builds something that works only from loopback.
/// </remarks>
public sealed class AdministrativeSurfaceContractTests
{
    private const string Scheme = "AdministrationBearer";

    [Fact]
    public void The_bearer_scheme_is_declared()
    {
        Assert.Contains(Scheme, OpenApiDocument.Load().SecuritySchemeNames(), StringComparer.Ordinal);
    }

    [Fact]
    public void Every_administrative_operation_requires_the_bearer_scheme()
    {
        foreach (var (path, operation) in AdministrativeOperations())
        {
            Assert.Contains(
                Scheme,
                OpenApiDocument.SecuritySchemes(operation).ToArray(),
                StringComparer.Ordinal);

            Assert.Contains(
                "401",
                OpenApiDocument.ResponseStatuses(operation).ToArray(),
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Every_evidence_read_declares_the_answers_it_can_actually_give()
    {
        // These three read the store, so each can meet a deployment that retains nothing and an
        // identifier that is not there.
        string[] reads =
        [
            "/api/v1/exchanges/{exchangeId}",
            "/api/v1/exchanges/{exchangeId}/timeline",
            "/api/v1/exchanges/{exchangeId}/observations",
        ];

        foreach (var path in reads)
        {
            var statuses = OpenApiDocument.ResponseStatuses(Get(path)).ToArray();

            Assert.Contains("404", statuses, StringComparer.Ordinal);
            Assert.Contains("503", statuses, StringComparer.Ordinal);
        }

        var list = OpenApiDocument.ResponseStatuses(Get("/api/v1/exchanges")).ToArray();

        // The list takes filters, so it is the one that can refuse a query.
        Assert.Contains("400", list, StringComparer.Ordinal);
        Assert.Contains("503", list, StringComparer.Ordinal);
    }

    [Fact]
    public void The_health_probes_stay_outside_administrative_authentication()
    {
        // A probe that failed closed on a misconfigured token would take a healthy gateway out of
        // rotation for a problem it has no part in (FR-HEALTH-002).
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            Assert.Empty(OpenApiDocument.SecuritySchemes(Get(path)));
        }
    }

    [Fact]
    public void Every_administrative_error_code_the_document_names_is_declared_in_code()
    {
        var document = AgentSplice.TestSupport.RepositoryPaths.ReadText("openapi", "agentsplice-openapi.yaml");

        foreach (var code in ErrorCodes.Administration)
        {
            Assert.Contains(code, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_timeline_and_observation_routes_are_documented_as_the_same_evidence()
    {
        // They return byte-for-byte the same body in Stage 1. The document used to say the second
        // returned "repeated boundaries" the first did not, which described a projection that has
        // never existed.
        var document = AgentSplice.TestSupport.RepositoryPaths.ReadText("openapi", "agentsplice-openapi.yaml");

        Assert.DoesNotContain("Unlike the timeline projection", document, StringComparison.Ordinal);
        Assert.Contains("what the timeline route returns", document, StringComparison.Ordinal);
    }

    private static YamlMappingNode Get(string path) => OpenApiDocument.Load().Operations(path)["get"];

    private static IEnumerable<(string Path, YamlMappingNode Operation)> AdministrativeOperations()
    {
        var document = OpenApiDocument.Load();

        foreach (var path in document.Paths().Where(candidate => candidate.StartsWith("/api/v1", StringComparison.Ordinal)))
        {
            foreach (var operation in document.Operations(path))
            {
                yield return (path, operation.Value);
            }
        }
    }
}
