namespace AgentSplice.Infrastructure.Persistence.Rows;

/// <summary>
/// One persisted timeline boundary (docs/SPECIFICATION.md section 13.4, FR-TRACE-004).
/// </summary>
/// <remarks>
/// Rows are immutable once written. Nothing updates an observation, because a boundary that could be
/// revised afterwards would stop being evidence.
///
/// <see cref="Sequence"/> is stored rather than inferred from <see cref="TimestampTicks"/>. Two
/// boundaries can share a timestamp at clock resolution, and a wall clock stepped backwards mid-
/// exchange can order two boundaries impossibly — the timeline keeps that visible on purpose, so the
/// order events were recorded in has to survive independently of the times they carry.
/// </remarks>
internal sealed class ExchangeObservationRow
{
    /// <summary>Identity of this observation.</summary>
    public Guid ObservationId { get; set; }

    /// <summary>The exchange this observation belongs to.</summary>
    public Guid ExchangeId { get; set; }

    /// <summary>Zero-based position in the exchange timeline.</summary>
    public int Sequence { get; set; }

    /// <summary>Which boundary was observed.</summary>
    public int Type { get; set; }

    /// <summary>When the boundary was observed, as UTC ticks.</summary>
    public long TimestampTicks { get; set; }

    /// <summary>Where the evidence came from.</summary>
    public int Source { get; set; }

    /// <summary>Elapsed time this observation represents, when the boundary has a duration.</summary>
    public long? DurationTicks { get; set; }

    /// <summary>Confidence in [0,1], present only when the source is not a direct observation.</summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// The sanitised detail map as a JSON object, or <c>null</c> when the observation carried none.
    /// </summary>
    /// <remarks>
    /// <see cref="Domain.Observations.SafeDetails"/> bounds entry count, key characters, and value
    /// length precisely so this column cannot become a channel for prompt content or tool arguments
    /// (FR-TRACE-003, docs/THREAT_MODEL.md).
    /// </remarks>
    public string? DetailsJson { get; set; }

    /// <summary>The owning exchange.</summary>
    public ExchangeRow? Exchange { get; set; }
}
