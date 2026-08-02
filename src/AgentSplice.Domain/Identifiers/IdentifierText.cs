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

    /// <summary>What an opaque, externally owned identifier such as a model ID must satisfy.</summary>
    internal const string OpaqueRule =
        "non-blank text of bounded length containing no control characters";

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

    /// <summary>
    /// Validates an identifier that a third party owns and AgentSplice merely carries, such as a
    /// model ID.
    /// </summary>
    /// <remarks>
    /// Deliberately permissive. Model identifiers are opaque values chosen by runtimes, registries,
    /// and model authors; the set of punctuation they use is not AgentSplice's to decide, and
    /// rejecting an identifier a runtime would have accepted would make the gateway the source of a
    /// failure that does not exist downstream (P-002).
    ///
    /// The two rules that remain are the ones AgentSplice cannot carry a value without. Control
    /// characters would allow log and header injection, and a lone surrogate cannot be encoded as
    /// UTF-8, so it could never be forwarded, persisted, or exported.
    /// </remarks>
    internal static string RequireOpaqueText(string? value, int maxLength, string parameterName)
    {
        var trimmed = Require(value, maxLength, parameterName);

        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];

            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' must not contain control characters."),
                    parameterName);
            }

            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (!char.IsHighSurrogate(character)
                || index + 1 >= trimmed.Length
                || !char.IsLowSurrogate(trimmed[index + 1]))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"'{parameterName}' must be text that can be encoded as UTF-8."),
                    parameterName);
            }

            // The pair is well formed; skip its low half so it is not inspected on its own.
            index++;
        }

        return trimmed;
    }

    internal static string RequireCorrelationToken(string? value, int maxLength, string parameterName)
    {
        var trimmed = Require(value, maxLength, parameterName);

        foreach (var character in trimmed)
        {
            // Correlation tokens are echoed in response headers, so anything outside printable ASCII
            // would allow header injection. That is the whole of what this check buys, and it is
            // worth being exact about: printable ASCII is not safe text. A client is free to put
            // 128 characters of anything readable in x-request-id, so a correlation token must never
            // be written to a log, a metric dimension, or an export — ExchangeId is AgentSplice's own
            // identifier and is what those use.
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
