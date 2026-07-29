using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Domain.Measurements;

/// <summary>
/// A single reported value with mandatory provenance (docs/SPECIFICATION.md section 13.5).
/// </summary>
/// <remarks>
/// Construction is funnelled through the factory methods so that no measurement can exist without
/// a source, and so that <c>NaN</c> or infinite values cannot enter evidence: a division that could
/// not be performed must be absent, not <c>NaN</c>.
/// </remarks>
public sealed record Measurement
{
    private Measurement()
    {
    }

    /// <summary>Identity of this measurement.</summary>
    public MeasurementId MeasurementId { get; private init; }

    /// <summary>The exchange this measurement belongs to, when it is exchange-scoped.</summary>
    public ExchangeId? ExchangeId { get; private init; }

    /// <summary>Stable measurement name. See <see cref="MeasurementNames"/>.</summary>
    public string Name { get; private init; } = string.Empty;

    /// <summary>The value. Always finite.</summary>
    public double Value { get; private init; }

    /// <summary>The unit of <see cref="Value"/>.</summary>
    public MeasurementUnit Unit { get; private init; }

    /// <summary>Where the value came from.</summary>
    public MeasurementProvenance Provenance { get; private init; }

    /// <summary>Confidence in [0,1], when the provenance is weaker than a direct observation.</summary>
    public double? Confidence { get; private init; }

    /// <summary>Start of the interval the value describes, when applicable.</summary>
    public DateTimeOffset? StartedAt { get; private init; }

    /// <summary>End of the interval the value describes, when applicable.</summary>
    public DateTimeOffset? EndedAt { get; private init; }

    /// <summary>True when this value must be displayed with an explicit estimate label (FR-OBS-010).</summary>
    public bool RequiresExplicitLabel => MeasurementProvenanceRules.RequiresExplicitLabel(Provenance);

    /// <summary>Creates a validated measurement.</summary>
    public static Measurement Create(
        MeasurementId measurementId,
        string name,
        double value,
        MeasurementUnit unit,
        MeasurementProvenance provenance,
        ExchangeId? exchangeId = null,
        double? confidence = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        if (measurementId.IsEmpty)
        {
            throw new ArgumentException("A measurement requires an identity.", nameof(measurementId));
        }

        var normalisedName = MeasurementNameGuard.Require(name, nameof(name));

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A measurement value must be finite; an unavailable value must be absent rather than NaN or infinity.");
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown measurement unit.");
        }

        if (!Enum.IsDefined(provenance))
        {
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Unknown provenance.");
        }

        if (confidence is { } certainty && (double.IsNaN(certainty) || certainty < 0d || certainty > 1d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                certainty,
                "Confidence must be a number in the inclusive range [0,1].");
        }

        if (startedAt is { } start && endedAt is { } end && end < start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAt),
                end,
                "A measurement interval cannot end before it starts.");
        }

        return new Measurement
        {
            MeasurementId = measurementId,
            ExchangeId = exchangeId,
            Name = normalisedName,
            Value = value,
            Unit = unit,
            Provenance = provenance,
            Confidence = confidence,
            StartedAt = startedAt,
            EndedAt = endedAt,
        };
    }

    /// <summary>Creates a duration measurement observed directly by AgentSplice.</summary>
    public static Measurement Duration(
        string name,
        TimeSpan duration,
        ExchangeId exchangeId,
        MeasurementProvenance provenance = MeasurementProvenance.Measured,
        double? confidence = null)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "A duration measurement cannot be negative.");
        }

        return Create(
            MeasurementId.New(),
            name,
            duration.TotalMilliseconds,
            MeasurementUnit.Milliseconds,
            provenance,
            exchangeId,
            confidence);
    }

    /// <summary>Creates a token-count measurement carrying the provenance of the count itself.</summary>
    public static Measurement Tokens(string name, TokenCount tokens, ExchangeId exchangeId) =>
        Create(
            MeasurementId.New(),
            name,
            tokens.Value,
            MeasurementUnit.Tokens,
            tokens.Provenance,
            exchangeId);

    /// <summary>Creates a dimensionless count measurement observed directly by AgentSplice.</summary>
    public static Measurement Count(string name, long value, ExchangeId exchangeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        return Create(
            MeasurementId.New(),
            name,
            value,
            MeasurementUnit.Count,
            MeasurementProvenance.Measured,
            exchangeId);
    }

    /// <summary>Creates a byte-count measurement observed directly by AgentSplice.</summary>
    public static Measurement Bytes(string name, long value, ExchangeId exchangeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        return Create(
            MeasurementId.New(),
            name,
            value,
            MeasurementUnit.Bytes,
            MeasurementProvenance.Measured,
            exchangeId);
    }
}
