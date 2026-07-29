namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// The model identifier actually sent to a runtime after alias resolution.
/// </summary>
/// <remarks>
/// Kept distinct from <see cref="ClientModelId"/> so that every difference between what the client
/// asked for and what was forwarded is representable as an explicit routing observation
/// (FR-TRACE-007, FR-TRACE-008).
/// </remarks>
public readonly record struct UpstreamModelId
{
    /// <summary>Maximum accepted length.</summary>
    public const int MaxLength = 256;

    private UpstreamModelId(string value) => Value = value;

    /// <summary>The upstream model identifier, unmodified.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Validates an upstream model identifier.</summary>
    public static UpstreamModelId Create(string value) =>
        new(IdentifierText.RequireModelIdentifier(value, MaxLength, nameof(value)));

    /// <summary>Attempts to accept an upstream model identifier without throwing.</summary>
    public static bool TryCreate(string? value, out UpstreamModelId upstreamModelId)
    {
        try
        {
            upstreamModelId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            upstreamModelId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
