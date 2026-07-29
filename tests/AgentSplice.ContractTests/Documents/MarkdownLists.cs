using System.Text.RegularExpressions;

namespace AgentSplice.ContractTests.Documents;

/// <summary>
/// Extracts the declarative lists that specification documents use, so code constants can be
/// compared against the document that publishes them.
/// </summary>
internal static partial class MarkdownLists
{
    /// <summary>
    /// Returns the inline-code items of a bullet list that follows <paramref name="afterHeading"/> and
    /// stops at <paramref name="beforeHeading"/>.
    /// </summary>
    internal static IReadOnlyList<string> InlineCodeBullets(
        string markdown,
        string afterHeading,
        string? beforeHeading = null)
    {
        var section = Section(markdown, afterHeading, beforeHeading);

        return InlineCodeRegex()
            .Matches(section)
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    /// <summary>
    /// Returns the request lines of a fenced <c>http</c> block that follows
    /// <paramref name="afterHeading"/>, as "METHOD path" pairs.
    /// </summary>
    internal static IReadOnlyList<(string Method, string Path)> HttpBlockRequests(
        string markdown,
        string afterHeading,
        string? beforeHeading = null)
    {
        var section = Section(markdown, afterHeading, beforeHeading);
        var requests = new List<(string Method, string Path)>();

        foreach (var line in section.Split('\n'))
        {
            var match = HttpRequestRegex().Match(line.Trim());

            if (match.Success)
            {
                requests.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        return requests;
    }

    private static string Section(string markdown, string afterHeading, string? beforeHeading)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var start = markdown.IndexOf(afterHeading, StringComparison.Ordinal);

        if (start < 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Expected to find '{afterHeading}' in the document."));
        }

        start += afterHeading.Length;

        if (beforeHeading is null)
        {
            return markdown[start..];
        }

        var end = markdown.IndexOf(beforeHeading, start, StringComparison.Ordinal);

        if (end < 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Expected to find '{beforeHeading}' after '{afterHeading}' in the document."));
        }

        return markdown[start..end];
    }

    [GeneratedRegex(@"^\s*-\s+`([^`]+)`\s*$", RegexOptions.Multiline)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^(GET|POST|PUT|PATCH|DELETE)\s+(/\S+)$")]
    private static partial Regex HttpRequestRegex();
}
