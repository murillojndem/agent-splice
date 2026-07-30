using AgentSplice.Application.Errors;
using AgentSplice.ContractTests.Documents;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// The <c>error.type</c> vocabulary is published in docs/API.md, so code and document must not drift.
/// </summary>
/// <remarks>
/// The specification supplies exactly one of these by example, so Stage 1A defined the rest. A
/// declared set that the document does not list is an unannounced contract change: clients branch on
/// <c>type</c>, and a category appearing without notice is indistinguishable from a bug on their
/// side.
/// </remarks>
public sealed class ErrorTypeContractTests
{
    [Fact]
    public void The_declared_error_types_are_exactly_those_published_in_the_api_document()
    {
        var documented = MarkdownLists
            .InlineCodeBullets(ApiDocument(), "## Stable error types", "## Error status mapping")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, ErrorTypes.All.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_error_type_is_lower_snake_case()
    {
        foreach (var type in ErrorTypes.All)
        {
            Assert.All(type, character =>
                Assert.True(
                    char.IsAsciiLetterLower(character) || character == '_',
                    FormattableString.Invariant($"'{type}' contains an unexpected character '{character}'.")));
        }
    }

    [Fact]
    public void The_type_named_by_the_specification_is_declared()
    {
        // docs/SPECIFICATION.md section 10.3 gives this one by example, so it is normative rather
        // than a Stage 1A invention.
        Assert.Contains("upstream_protocol_error", ErrorTypes.All, StringComparer.Ordinal);
    }

    [Fact]
    public void Client_validation_reuses_the_openai_category()
    {
        // So that an SDK branching on type keeps working against AgentSplice.
        Assert.Equal("invalid_request_error", ErrorTypes.InvalidRequest);
    }

    private static string ApiDocument() => RepositoryPaths.ReadText("docs", "API.md");
}
