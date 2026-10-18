using System.Text;
using AgentSplice.ContractTests.Documents;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Runtimes;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// Keeps the OpenAPI draft, docs/API.md, and the domain enums aligned.
/// </summary>
/// <remarks>
/// Three kinds of drift are worth failing a build over: an endpoint that exists in one document and
/// not the other, an enum whose members diverge from the domain (so a client cannot exhaustively
/// handle a status), and a content endpoint appearing before content retention, sanitisation, and
/// authorization exist (docs/API.md).
/// </remarks>
public sealed class OpenApiContractTests
{
    private const string Stage1AdministrativeHeading = "## Stage 1 administrative endpoints";
    private const string Stage2Heading = "## Stage 2 replay and conformance endpoints";
    private const string Stage3Heading = "## Stage 3 evaluation endpoints";

    [Fact]
    public void The_openai_compatibility_endpoints_are_declared()
    {
        var paths = OpenApiDocument.Load().Paths();

        Assert.Contains("/v1/models", paths);
        Assert.Contains("/v1/chat/completions", paths);
    }

    [Fact]
    public void Every_stage_1_administrative_endpoint_in_the_api_document_is_declared_in_the_openapi_draft()
    {
        var declared = OpenApiDocument.Load()
            .Paths()
            .Select(NormalisePathParameters)
            .ToHashSet(StringComparer.Ordinal);

        var documented = MarkdownLists
            .HttpBlockRequests(ApiDocument(), Stage1AdministrativeHeading, Stage2Heading)
            .Select(request => NormalisePathParameters(request.Path))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(documented);

        var missing = documented.Except(declared, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "The OpenAPI draft does not declare: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_openapi_draft_declares_no_administrative_endpoint_that_the_api_document_omits()
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);

        foreach (var heading in new[] { Stage1AdministrativeHeading, Stage2Heading })
        {
            foreach (var request in MarkdownLists.HttpBlockRequests(
                ApiDocument(),
                heading,
                heading == Stage1AdministrativeHeading ? Stage2Heading : Stage3Heading))
            {
                documented.Add(NormalisePathParameters(request.Path));
            }
        }

        var undocumented = OpenApiDocument.Load()
            .Paths()
            .Where(path => path.StartsWith("/api/v1", StringComparison.Ordinal))
            .Select(NormalisePathParameters)
            .Where(path => !documented.Contains(path))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "The OpenAPI draft declares undocumented administrative endpoints: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void Exactly_the_stage_2_operations_are_marked_planned()
    {
        // This build answers 404 for them. Unmarked, they read as served to anyone generating a
        // client from the document (ADR 0027).
        var stage2 = MarkdownLists
            .HttpBlockRequests(ApiDocument(), Stage2Heading, Stage3Heading)
            .Select(request => NormalisePathParameters(request.Path))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(stage2);

        var document = OpenApiDocument.Load();

        foreach (var path in document.Paths())
        {
            foreach (var (verb, operation) in document.Operations(path))
            {
                Assert.True(
                    OpenApiDocument.IsPlanned(operation) == stage2.Contains(NormalisePathParameters(path)),
                    FormattableString.Invariant($"{verb} {path} must be marked x-agentsplice-planned exactly when docs/API.md lists it under Stage 2."));
            }
        }
    }

    [Fact]
    public void No_response_reference_carries_keys_a_reader_would_ignore()
    {
        // A $ref response object takes no siblings. Two Stage 2 operations once declared their 202
        // body under the 401 reference beside it, so the body was described nowhere a reader looks.
        var document = OpenApiDocument.Load();

        foreach (var path in document.Paths())
        {
            foreach (var (verb, operation) in document.Operations(path))
            {
                foreach (var (status, response) in OpenApiDocument.Responses(operation))
                {
                    Assert.False(
                        response.Children.ContainsKey(new YamlScalarNode("$ref")) && response.Children.Count > 1,
                        FormattableString.Invariant($"{verb} {path} {status} is a reference with sibling keys."));
                }
            }
        }
    }

    [Fact]
    public void No_content_endpoint_is_declared_before_retention_and_sanitisation_exist()
    {
        var contentPaths = OpenApiDocument.Load()
            .Paths()
            .Where(path => path.Contains("/content", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            contentPaths.Length == 0,
            "Content endpoints must not exist yet: " + string.Join(", ", contentPaths));
    }

    [Fact]
    public void The_content_retention_state_enum_matches_the_domain()
    {
        AssertEnumMatchesDomain<ContentRetentionState>("ExchangeSummary", "contentRetentionState");
    }

    [Fact]
    public void The_measurement_provenance_enum_matches_the_domain()
    {
        AssertEnumMatchesDomain<MeasurementProvenance>("Measurement", "provenance");
    }

    [Fact]
    public void The_upstream_outcome_enum_matches_the_domain()
    {
        // A client switches on these to tell a runtime's failure from its success; a member the
        // domain gains and the schema does not would be one no client can handle (ADR 0026).
        AssertEnumMatchesDomain<UpstreamOutcome>("ExchangeSummary", "upstreamOutcome");
    }

    [Fact]
    public void The_runtime_health_status_enum_matches_the_domain()
    {
        AssertEnumMatchesDomain<RuntimeHealthStatus>("RuntimeHealth", "status");
    }

    [Fact]
    public void The_model_resolution_source_enum_matches_the_domain()
    {
        AssertEnumMatchesDomain<ModelResolutionSource>("CatalogModel", "source");
    }

    [Fact]
    public void The_capability_provenance_enum_matches_the_domain()
    {
        AssertEnumMatchesDomain<CapabilityProvenance>("CatalogModel", "capabilityProvenance");
    }

    private static void AssertEnumMatchesDomain<TEnum>(string schemaName, string propertyName)
        where TEnum : struct, Enum
    {
        var declared = OpenApiDocument.Load()
            .SchemaPropertyEnum(schemaName, propertyName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(WireNames.Of<TEnum>(), declared);
    }

    /// <summary>
    /// Rewrites a path template so that <c>{id}</c> and <c>{exchangeId}</c> compare equal. The
    /// parameter name is a documentation choice; the shape of the route is the contract.
    /// </summary>
    private static string NormalisePathParameters(string path)
    {
        var builder = new StringBuilder(path.Length);
        var insideParameter = false;

        foreach (var character in path)
        {
            switch (character)
            {
                case '{':
                    insideParameter = true;
                    builder.Append("{}");
                    break;
                case '}':
                    insideParameter = false;
                    break;
                default:
                    if (!insideParameter)
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string ApiDocument() => RepositoryPaths.ReadText("docs", "API.md");
}
