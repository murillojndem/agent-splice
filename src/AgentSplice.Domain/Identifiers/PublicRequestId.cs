namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Client-facing correlation token returned as <c>x-agentsplice-request-id</c>
/// (docs/API.md, FR-CHAT-009).
/// </summary>
/// <remarks>
/// A client may supply its own <c>x-request-id</c>; the bound length matches the OpenAPI draft
/// so that an oversized or non-printable client value can never reach a response header.
/// </remarks>
public readonly record struct PublicRequestId
{
    /// <summary>Maximum accepted length, matching <c>x-request-id</c> in openapi/agentsplice-openapi.yaml.</summary>
    public const int MaxLength = 128;

    private PublicRequestId(string value) => Value = value;

    /// <summary>The token as it will appear on the wire.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Creates a token derived from an exchange identity, used when the client supplies none.</summary>
    public static PublicRequestId FromExchangeId(ExchangeId exchangeId) =>
        new(exchangeId.ToString());

    /// <summary>
    /// Creates a fresh correlation token for a request that is not a completion exchange.
    /// </summary>
    /// <remarks>
    /// Model discovery is correlatable but is not an exchange, so minting an
    /// <see cref="ExchangeId"/> for it would put an identifier in a response header that names a
    /// record which does not exist.
    /// </remarks>
    public static PublicRequestId New() =>
        new(Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Validates and normalises a caller-supplied correlation token.</summary>
    public static PublicRequestId Create(string value) =>
        new(IdentifierText.RequireCorrelationToken(value, MaxLength, nameof(value)));

    /// <summary>Attempts to accept a caller-supplied correlation token without throwing.</summary>
    public static bool TryCreate(string? value, out PublicRequestId requestId)
    {
        try
        {
            requestId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            requestId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
