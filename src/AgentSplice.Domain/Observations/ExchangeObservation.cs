using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Domain.Observations;

/// <summary>
/// One immutable, sequence-ordered timeline entry (docs/SPECIFICATION.md FR-TRACE-004, section 13.4).
/// </summary>
/// <remarks>
/// Construction is funnelled through <see cref="Create"/> so that every instance is validated. The
/// primary constructor is private for the same reason: an unvalidated observation would let a
/// negative sequence, a confidence outside [0,1], or an inferred value without a confidence reach
/// persisted evidence.
/// </remarks>
public sealed record ExchangeObservation
{
    private ExchangeObservation()
    {
    }

    /// <summary>Identity of this observation.</summary>
    public ObservationId ObservationId { get; private init; }

    /// <summary>The exchange this observation belongs to.</summary>
    public ExchangeId ExchangeId { get; private init; }

    /// <summary>Zero-based position in the exchange timeline.</summary>
    public int Sequence { get; private init; }

    /// <summary>Which boundary was observed.</summary>
    public ObservationType Type { get; private init; }

    /// <summary>When the boundary was observed, from <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset Timestamp { get; private init; }

    /// <summary>Where the evidence came from.</summary>
    public ObservationSource Source { get; private init; }

    /// <summary>Elapsed time this observation represents, when the boundary has a duration.</summary>
    public TimeSpan? Duration { get; private init; }

    /// <summary>Confidence in [0,1]. Required when <see cref="Source"/> is not directly observed.</summary>
    public double? Confidence { get; private init; }

    /// <summary>Sanitised supporting detail. Never raw request or response content.</summary>
    public SafeDetails Details { get; private init; } = SafeDetails.Empty;

    /// <summary>Creates a validated observation.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The sequence, duration, or confidence is out of range.</exception>
    public static ExchangeObservation Create(
        ObservationId observationId,
        ExchangeId exchangeId,
        int sequence,
        ObservationType type,
        DateTimeOffset timestamp,
        ObservationSource source,
        TimeSpan? duration = null,
        double? confidence = null,
        SafeDetails? details = null)
    {
        if (observationId.IsEmpty)
        {
            throw new ArgumentException("An observation requires an identity.", nameof(observationId));
        }

        if (exchangeId.IsEmpty)
        {
            throw new ArgumentException("An observation requires an exchange identity.", nameof(exchangeId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown observation type.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown observation source.");
        }

        if (duration is { } elapsed && elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                elapsed,
                "An observed duration cannot be negative.");
        }

        if (confidence is { } certainty && (certainty < 0d || certainty > 1d || double.IsNaN(certainty)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                certainty,
                "Confidence must be a number in the inclusive range [0,1].");
        }

        return new ExchangeObservation
        {
            ObservationId = observationId,
            ExchangeId = exchangeId,
            Sequence = sequence,
            Type = type,
            Timestamp = timestamp,
            Source = source,
            Duration = duration,
            Confidence = confidence,
            Details = details ?? SafeDetails.Empty,
        };
    }
}
