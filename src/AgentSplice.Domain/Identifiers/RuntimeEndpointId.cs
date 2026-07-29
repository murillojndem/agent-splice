namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Administratively configured model runtime endpoint (docs/SPECIFICATION.md section 13.1).
/// </summary>
/// <remarks>
/// This identifier is a bounded metric and trace dimension, so it is normalised to lower case and
/// restricted to a slug charset (FR-OBS-006). It also disambiguates duplicate upstream model IDs
/// across runtimes (FR-MOD-004).
/// </remarks>
public readonly record struct RuntimeEndpointId
{
    /// <summary>Maximum accepted length.</summary>
    public const int MaxLength = 64;

    private RuntimeEndpointId(string value) => Value = value;

    /// <summary>The normalised lower-case identifier.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Validates and normalises a configured runtime endpoint identifier.</summary>
    public static RuntimeEndpointId Create(string value) =>
        new(IdentifierText.RequireSlug(value, MaxLength, nameof(value)));

    /// <summary>Attempts to accept a runtime endpoint identifier without throwing.</summary>
    public static bool TryCreate(string? value, out RuntimeEndpointId runtimeEndpointId)
    {
        try
        {
            runtimeEndpointId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            runtimeEndpointId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
