using System.Globalization;

namespace AgentSplice.Domain.Measurements;

/// <summary>
/// A token count that always carries its source (docs/SPECIFICATION.md FR-OBS-003, section 15.3).
/// </summary>
/// <remarks>
/// There is no way to construct a token count without stating where it came from. A bare integer
/// would let a client estimate and an upstream-reported value be compared as if they were equally
/// trustworthy.
/// </remarks>
public readonly record struct TokenCount
{
    private TokenCount(int value, MeasurementProvenance provenance)
    {
        Value = value;
        Provenance = provenance;
    }

    /// <summary>The number of tokens.</summary>
    public int Value { get; }

    /// <summary>Where the count came from.</summary>
    public MeasurementProvenance Provenance { get; }

    /// <summary>Creates a token count with explicit provenance.</summary>
    public static TokenCount Create(int value, MeasurementProvenance provenance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        if (!Enum.IsDefined(provenance))
        {
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Unknown provenance.");
        }

        return new TokenCount(value, provenance);
    }

    /// <summary>Creates a count reported by the upstream runtime.</summary>
    public static TokenCount FromUpstream(int value) =>
        Create(value, MeasurementProvenance.UpstreamReported);

    /// <summary>Creates a count produced by an AgentSplice estimator.</summary>
    public static TokenCount FromGatewayEstimate(int value) =>
        Create(value, MeasurementProvenance.Estimated);

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1})", Value, Provenance);
}
