namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Client-visible model alias declared in configuration (docs/SPECIFICATION.md section 13.2).
/// </summary>
/// <remarks>
/// Aliases are compared case-sensitively because clients echo the exact string they were given by
/// <c>GET /v1/models</c>, and FR-MOD-005 requires deterministic resolution of that exact string.
/// </remarks>
public readonly record struct ModelAliasId
{
    /// <summary>Maximum accepted length.</summary>
    public const int MaxLength = 256;

    private ModelAliasId(string value) => Value = value;

    /// <summary>The alias exactly as clients will see and send it.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Validates a configured alias identifier.</summary>
    /// <remarks>
    /// Held to the same opaque rule as <see cref="ClientModelId"/>
    /// (<see cref="IdentifierText.OpaqueRule"/>), because an alias is the string a client sends back
    /// as its model: an alias an operator can configure but a client could not then send would be
    /// unusable.
    /// </remarks>
    public static ModelAliasId Create(string value) =>
        new(IdentifierText.RequireOpaqueText(value, MaxLength, nameof(value)));

    /// <summary>Attempts to accept an alias identifier without throwing.</summary>
    public static bool TryCreate(string? value, out ModelAliasId modelAliasId)
    {
        try
        {
            modelAliasId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            modelAliasId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
