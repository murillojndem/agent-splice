namespace AgentSplice.Infrastructure.Persistence.Rows;

/// <summary>
/// One persisted measurement (docs/SPECIFICATION.md section 13.5, FR-OBS-003, FR-OBS-010).
/// </summary>
/// <remarks>
/// <see cref="Provenance"/> is not nullable and has no "unspecified" member, so a value cannot be
/// stored without stating where it came from. That is the whole point of the type: a clock reading,
/// an upstream-reported token count, and a gateway estimate must never be comparable as if they were
/// equally trustworthy (P-008).
///
/// A phase that was never observed produces no row at all. There is deliberately no "value unknown"
/// encoding, because a zero in a numeric column reads as "it happened and measured nothing"
/// (FR-TRACE-006, FR-OBS-004).
/// </remarks>
internal sealed class ExchangeMeasurementRow
{
    /// <summary>Identity of this measurement.</summary>
    public Guid MeasurementId { get; set; }

    /// <summary>The exchange this measurement belongs to.</summary>
    public Guid ExchangeId { get; set; }

    /// <summary>Stable measurement name from <see cref="Domain.Measurements.MeasurementNames"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value. Always finite; the domain refuses to construct anything else.</summary>
    public double Value { get; set; }

    /// <summary>The unit of <see cref="Value"/>.</summary>
    public int Unit { get; set; }

    /// <summary>Where the value came from.</summary>
    public int Provenance { get; set; }

    /// <summary>Confidence in [0,1], when the provenance is weaker than a direct observation.</summary>
    public double? Confidence { get; set; }

    /// <summary>Start of the interval the value describes, when applicable.</summary>
    public long? StartedAtTicks { get; set; }

    /// <summary>End of the interval the value describes, when applicable.</summary>
    public long? EndedAtTicks { get; set; }

    /// <summary>The owning exchange.</summary>
    public ExchangeRow? Exchange { get; set; }
}
