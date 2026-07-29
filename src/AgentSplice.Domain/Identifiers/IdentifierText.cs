using System.Globalization;

namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Shared validation for the string-shaped identifiers AgentSplice accepts from clients,
/// configuration, and upstream runtimes.
/// </summary>
/// <remarks>
/// Identifiers reach logs, trace attributes, metric dimensions, and response headers. Rejecting
/// control characters and unbounded lengths here is what keeps
/// docs/SPECIFICATION.md FR-OBS-006 (bounded metric dimensions) and the "no content in headers"
/// rule in docs/API.md enforceable at a single place.
/// </remarks>
internal static class IdentifierText
{
    /// <summary>Characters permitted in slug-shaped identifiers such as runtime endpoint IDs.</summary>
    internal const string SlugCharacters = "letters, digits, '-', '_', and '.'";

    /// <summary>Characters permitted in model-shaped identifiers, which commonly contain paths and colons.</summary>
    internal const string ModelCharacters = "letters, digits, '-', '_', '.', ':', '/', and '@'";

    internal static string RequireSlug(string? value, int maxLength, string parameterName)
    {
        var trimmed = Require(value, maxLength, parameterName);

        foreach (var character in trimmed)
        {
            if (!IsSlugCharacter(character))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' may only contain {SlugCharacters}."),
                    parameterName);
            }
        }

        return trimmed.ToLowerInvariant();
    }

    internal static string RequireModelIdentifier(string? value, int maxLength, string parameterName)
    {
        var trimmed = Require(value, maxLength, parameterName);

        foreach (var character in trimmed)
        {
            if (!IsModelCharacter(character))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' may only contain {ModelCharacters}."),
                    parameterName);
            }
        }

        return trimmed;
    }

    internal static string RequireCorrelationToken(string? value, int maxLength, string parameterName)
    {
        var trimmed = Require(value, maxLength, parameterName);

        foreach (var character in trimmed)
        {
            // Correlation tokens are echoed in response headers. Anything outside printable
            // ASCII would allow header injection or smuggle content into observability output.
            if (character is < ' ' or > '~')
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' may only contain printable ASCII characters."),
                    parameterName);
            }
        }

        return trimmed;
    }

    internal static string RequireLowerHex(string? value, int exactLength, string parameterName)
    {
        var trimmed = Require(value, exactLength, parameterName);

        if (trimmed.Length != exactLength)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"'{parameterName}' must be exactly {exactLength} characters long."),
                parameterName);
        }

        foreach (var character in trimmed)
        {
            if (!IsLowerHexCharacter(character))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' must be lowercase hexadecimal."),
                    parameterName);
            }
        }

        return trimmed;
    }

    internal static bool IsSlugCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';

    internal static bool IsModelCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/' or '@';

    private static bool IsLowerHexCharacter(char character) =>
        char.IsAsciiDigit(character) || character is >= 'a' and <= 'f';

    private static string Require(string? value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' must be {1} characters or fewer.",
                    parameterName,
                    maxLength),
                parameterName);
        }

        return trimmed;
    }
}
