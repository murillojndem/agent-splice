namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// The RFC 9110 media-type grammar, as far as a response header needs it.
/// </summary>
/// <remarks>
/// Lives here rather than in a protocol module because two questions depend on it and only one of
/// them is protocol-specific. "Is this well-formed, and what media type does it name?" is HTTP;
/// "is that media type mine?" is the protocol's. Splitting them that way is what lets the parse
/// happen once, at the moment the header arrives, before any bound has had a chance to discard the
/// value it would have parsed (ADR 0012).
///
/// A runtime-supplied header is untrusted text, so nothing here throws and nothing here allocates
/// beyond the token it returns.
/// </remarks>
public static class MediaTypeGrammar
{
    private const string Whitespace = " \t";

    /// <summary>First character of RFC 9110 <c>obs-text</c>, <c>%x80-FF</c>.</summary>
    private const char ObsTextStart = (char)0x80;

    /// <summary>
    /// The media type a well-formed <c>Content-Type</c> names, lowercased, or <c>null</c> when the
    /// value is absent or does not conform.
    /// </summary>
    /// <remarks>
    /// The whole value is validated, parameters included, and only the media type is returned. A
    /// caller therefore cannot mistake "the text before the first semicolon" for proof that the
    /// header was valid — <c>text/event-stream; ===</c> yields <c>null</c>, not
    /// <c>text/event-stream</c> (ADR 0011).
    /// </remarks>
    public static string? Parse(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        // RFC 9110 section 5.5: a field value carries no leading or trailing whitespace of its own.
        var value = contentType.AsSpan().Trim(Whitespace);
        var separator = IndexOfParameterSeparator(value);

        // OWS may precede the semicolon, per `parameters = *( OWS ";" OWS [ parameter ] )`.
        var token = (separator < 0 ? value : value[..separator]).TrimEnd(Whitespace);

        if (!IsMediaType(token))
        {
            return null;
        }

        if (separator >= 0 && !AreValidParameters(value[(separator + 1)..]))
        {
            return null;
        }

        return token.ToString().ToLowerInvariant();
    }

    /// <summary><c>media-type = type "/" subtype</c>, with no whitespace anywhere inside it.</summary>
    private static bool IsMediaType(ReadOnlySpan<char> value)
    {
        var slash = value.IndexOf('/');

        return slash > 0 && IsToken(value[..slash]) && IsToken(value[(slash + 1)..]);
    }

    /// <summary>
    /// <c>parameters = *( OWS ";" OWS [ parameter ] )</c>.
    /// </summary>
    /// <remarks>
    /// The <c>[ parameter ]</c> is optional in the grammar, deliberately, so a trailing or empty
    /// semicolon is conforming and is accepted here. Rejecting <c>text/event-stream;</c> would refuse
    /// a sloppy but legal sender — the very class of failure this method exists to stop.
    /// </remarks>
    private static bool AreValidParameters(ReadOnlySpan<char> value)
    {
        while (true)
        {
            var separator = IndexOfParameterSeparator(value);
            var parameter = (separator < 0 ? value : value[..separator]).Trim(Whitespace);

            if (!parameter.IsEmpty && !IsParameter(parameter))
            {
                return false;
            }

            if (separator < 0)
            {
                return true;
            }

            value = value[(separator + 1)..];
        }
    }

    /// <summary><c>parameter = parameter-name "=" parameter-value</c>, with no whitespace around the equals.</summary>
    private static bool IsParameter(ReadOnlySpan<char> value)
    {
        var equals = value.IndexOf('=');

        if (equals <= 0 || equals == value.Length - 1)
        {
            return false;
        }

        var name = value[..equals];
        var parameterValue = value[(equals + 1)..];

        return IsToken(name)
            && (parameterValue[0] == '"' ? IsQuotedString(parameterValue) : IsToken(parameterValue));
    }

    /// <summary>
    /// Finds the semicolon that ends a parameter, ignoring one inside a quoted value.
    /// </summary>
    /// <remarks>
    /// <c>text/plain; note="a;b"</c> carries one parameter, not two. Splitting on every semicolon
    /// would cut a legal quoted string in half and report the header as malformed.
    /// </remarks>
    private static int IndexOfParameterSeparator(ReadOnlySpan<char> value)
    {
        var quoted = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (quoted)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (character == '"')
            {
                quoted = true;
            }
            else if (character == ';')
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary><c>quoted-string = DQUOTE *( qdtext / quoted-pair ) DQUOTE</c>.</summary>
    private static bool IsQuotedString(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[^1] != '"')
        {
            return false;
        }

        var inner = value[1..^1];

        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];

            if (character == '\\')
            {
                if (++index == inner.Length || !IsQuotedPair(inner[index]))
                {
                    return false;
                }

                continue;
            }

            if (!IsQuotedText(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>token = 1*tchar</c>.</summary>
    private static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*'
            or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary><c>qdtext = HTAB / SP / %x21 / %x23-5B / %x5D-7E / obs-text</c>.</summary>
    private static bool IsQuotedText(char character) =>
        character is '\t' or ' ' or '!'
        || character is >= '#' and <= '['
        || character is >= ']' and <= '~'
        || character >= ObsTextStart;

    /// <summary><c>quoted-pair = "\" ( HTAB / SP / VCHAR / obs-text )</c>.</summary>
    private static bool IsQuotedPair(char character) =>
        character is '\t' or ' '
        || character is >= '!' and <= '~'
        || character >= ObsTextStart;
}
