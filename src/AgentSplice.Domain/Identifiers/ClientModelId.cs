namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// The model identifier a client asked for, before alias resolution.
/// </summary>
/// <remarks>
/// Kept distinct from <see cref="UpstreamModelId"/> on purpose: conflating the two is how a routing
/// change becomes invisible, which FR-TRACE-007 forbids.
/// </remarks>
public readonly record struct ClientModelId
{
    /// <summary>Maximum accepted length.</summary>
    public const int MaxLength = 256;

    private ClientModelId(string value) => Value = value;

    /// <summary>The requested model identifier, unmodified.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Validates a client-supplied model identifier.</summary>
    /// <remarks>
    /// A model identifier is an opaque third-party value, so validation bounds its length and
    /// rejects only what AgentSplice cannot carry. It does not constrain punctuation: a value the
    /// runtime would have accepted must not fail here (<see cref="IdentifierText.OpaqueRule"/>).
    /// </remarks>
    public static ClientModelId Create(string value) =>
        new(IdentifierText.RequireOpaqueText(value, MaxLength, nameof(value)));

    /// <summary>Attempts to accept a client-supplied model identifier without throwing.</summary>
    public static bool TryCreate(string? value, out ClientModelId clientModelId)
    {
        try
        {
            clientModelId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            clientModelId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
