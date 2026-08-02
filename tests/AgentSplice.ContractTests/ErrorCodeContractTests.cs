using AgentSplice.Application.Errors;
using AgentSplice.ContractTests.Documents;
using AgentSplice.Domain.Exchanges;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// The stable error codes are a published contract (docs/API.md). Clients, conformance reports, and
/// issue templates match on the exact strings, so code and document must not drift apart.
/// </summary>
public sealed class ErrorCodeContractTests
{
    [Fact]
    public void The_declared_core_error_codes_are_exactly_those_published_in_the_api_document()
    {
        var documented = MarkdownLists
            .InlineCodeBullets(ApiDocument(), "## Stable error codes\n\nCore:", "Administrative:")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, ErrorCodes.Core.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void The_declared_administrative_error_codes_are_exactly_those_published()
    {
        var documented = MarkdownLists
            .InlineCodeBullets(ApiDocument(), "Administrative:", "Later stages:")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented, ErrorCodes.Administration.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void The_two_vocabularies_do_not_overlap()
    {
        // A code that meant one thing on the completion path and another on the administrative one
        // would make the published contract ambiguous exactly where a client switches on it.
        Assert.Empty(ErrorCodes.Core.Intersect(ErrorCodes.Administration, StringComparer.Ordinal));
        Assert.Equal(ErrorCodes.Core.Count + ErrorCodes.Administration.Count, ErrorCodes.All.Count);
    }

    [Fact]
    public void Every_core_error_code_uses_the_agentsplice_prefix()
    {
        foreach (var code in ErrorCodes.All)
        {
            Assert.StartsWith("agentsplice_", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_core_error_code_is_lower_snake_case()
    {
        foreach (var code in ErrorCodes.All)
        {
            Assert.All(code, character =>
                Assert.True(
                    char.IsAsciiLetterLower(character) || character == '_',
                    FormattableString.Invariant($"'{code}' contains an unexpected character '{character}'.")));
        }
    }

    [Fact]
    public void Every_failure_class_has_a_core_error_code_to_translate_into()
    {
        // The mapping itself belongs to the Stage 1A error translation slice, but the two
        // vocabularies must stay the same size or a failure class would have no client-facing code.
        var failureClasses = Enum.GetValues<FailureClass>().Length;

        Assert.Equal(failureClasses, ErrorCodes.Core.Count);
    }

    private static string ApiDocument() => RepositoryPaths.ReadText("docs", "API.md");
}
