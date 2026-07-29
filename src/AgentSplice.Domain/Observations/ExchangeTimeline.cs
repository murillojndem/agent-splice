using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Domain.Observations;

/// <summary>
/// The append-only, sequence-ordered timeline of one completion exchange
/// (docs/SPECIFICATION.md FR-TRACE-004, FR-TRACE-005).
/// </summary>
/// <remarks>
/// Two rules drive the design. First, observations are appended from the request path and read by
/// persistence and observability, possibly on different threads, so appends are serialised.
/// Second, a boundary that was never observed must stay absent: the query members return
/// <c>null</c> rather than a zero or a guessed value, satisfying FR-TRACE-006 and
/// "Unknown values remain unknown" in CLAUDE.md.
/// </remarks>
public sealed class ExchangeTimeline
{
    private readonly List<ExchangeObservation> observations = [];
    private readonly object gate = new();

    /// <summary>Creates an empty timeline for an exchange.</summary>
    public ExchangeTimeline(ExchangeId exchangeId)
    {
        if (exchangeId.IsEmpty)
        {
            throw new ArgumentException("A timeline requires an exchange identity.", nameof(exchangeId));
        }

        ExchangeId = exchangeId;
    }

    /// <summary>The exchange this timeline describes.</summary>
    public ExchangeId ExchangeId { get; }

    /// <summary>The observations recorded so far, in sequence order.</summary>
    public IReadOnlyList<ExchangeObservation> Observations
    {
        get
        {
            lock (gate)
            {
                return observations.ToArray();
            }
        }
    }

    /// <summary>Number of observations recorded so far.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return observations.Count;
            }
        }
    }

    /// <summary>Appends an observation and assigns it the next sequence number.</summary>
    /// <exception cref="InvalidOperationException">
    /// The boundary is single-occurrence and has already been recorded.
    /// </exception>
    public ExchangeObservation Append(
        ObservationType type,
        DateTimeOffset timestamp,
        ObservationSource source = ObservationSource.Gateway,
        TimeSpan? duration = null,
        double? confidence = null,
        SafeDetails? details = null)
    {
        lock (gate)
        {
            if (ObservationTypeRules.IsSingleOccurrence(type) && ContainsCore(type))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Observation '{type}' may only be recorded once per exchange; overwriting it would discard the original evidence."));
            }

            var observation = ExchangeObservation.Create(
                ObservationId.New(),
                ExchangeId,
                observations.Count,
                type,
                timestamp,
                source,
                duration,
                confidence,
                details);

            observations.Add(observation);
            return observation;
        }
    }

    /// <summary>True when the boundary has been observed.</summary>
    public bool Contains(ObservationType type)
    {
        lock (gate)
        {
            return ContainsCore(type);
        }
    }

    /// <summary>The first observation of a boundary, or <c>null</c> when it was never observed.</summary>
    public ExchangeObservation? Find(ObservationType type)
    {
        lock (gate)
        {
            foreach (var observation in observations)
            {
                if (observation.Type == type)
                {
                    return observation;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The timestamp of a boundary, or <c>null</c> when it was never observed. Callers must not
    /// substitute a default: an unobserved boundary is unknown, not zero.
    /// </summary>
    public DateTimeOffset? TimestampOf(ObservationType type) => Find(type)?.Timestamp;

    /// <summary>
    /// The elapsed time between two boundaries, or <c>null</c> when either was never observed.
    /// </summary>
    /// <remarks>
    /// The result may be negative when the underlying clock is not monotonic. The value is returned
    /// unmodified so that clock anomalies stay visible in evidence instead of being clamped away.
    /// </remarks>
    public TimeSpan? DurationBetween(ObservationType from, ObservationType to)
    {
        DateTimeOffset? start;
        DateTimeOffset? end;

        lock (gate)
        {
            start = FindCore(from)?.Timestamp;
            end = FindCore(to)?.Timestamp;
        }

        return start is null || end is null ? null : end.Value - start.Value;
    }

    private bool ContainsCore(ObservationType type) => FindCore(type) is not null;

    private ExchangeObservation? FindCore(ObservationType type)
    {
        foreach (var observation in observations)
        {
            if (observation.Type == type)
            {
                return observation;
            }
        }

        return null;
    }
}
