using System.Text;
using AgentSplice.ContractTests.Documents;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Runtimes;
using AgentSplice.TestSupport;
using Xunit;

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
                heading == Stage1AdministrativeHeading ? Stage2Heading : "## Stage 3 evaluation endpoints"))
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

        var domain = Enum.GetNames<TEnum>()
            .Select(ToSnakeCase)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(domain, declared);
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

    private static string ToSnakeCase(string pascalCase)
    {
        var builder = new StringBuilder(pascalCase.Length + 4);

        for (var index = 0; index < pascalCase.Length; index++)
        {
            var character = pascalCase[index];

            if (index > 0 && char.IsAsciiLetterUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string ApiDocument() => RepositoryPaths.ReadText("docs", "API.md");
}
