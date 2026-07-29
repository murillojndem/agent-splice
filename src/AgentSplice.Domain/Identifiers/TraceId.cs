namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// W3C trace context trace identifier, surfaced as <c>x-agentsplice-trace-id</c>
/// (docs/API.md, FR-OBS-001).
/// </summary>
/// <remarks>
/// AgentSplice never invents a trace identifier. When no OpenTelemetry activity exists the value
/// is absent, which is what FR-TRACE-006 requires of missing evidence.
/// </remarks>
public readonly record struct TraceId
{
    /// <summary>Length of the lowercase hexadecimal W3C trace identifier.</summary>
    public const int Length = 32;

    private const string AllZero = "00000000000000000000000000000000";

    private TraceId(string value) => Value = value;

    /// <summary>The 32-character lowercase hexadecimal identifier.</summary>
    public string Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Validates a W3C trace identifier.</summary>
    public static TraceId Create(string value)
    {
        var normalised = IdentifierText.RequireLowerHex(value, Length, nameof(value));

        if (string.Equals(normalised, AllZero, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An all-zero trace identifier is invalid under the W3C trace context specification.",
                nameof(value));
        }

        return new TraceId(normalised);
    }

    /// <summary>Attempts to accept a trace identifier without throwing.</summary>
    public static bool TryCreate(string? value, out TraceId traceId)
    {
        try
        {
            traceId = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            traceId = default;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
