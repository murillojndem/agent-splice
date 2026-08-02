using AgentSplice.Application.Exchanges;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// One request's evidence, waiting to be written, together with the moment it started waiting.
/// </summary>
/// <remarks>
/// <paramref name="QueuedAt"/> is read by the sink at the instant the record entered the queue, not by
/// the writer when it comes back off. The two differ by however long the store was busy, and that
/// interval is the whole reason <see cref="Domain.Observations.ObservationType.MetadataQueued"/> and
/// <see cref="Domain.Observations.ObservationType.PersistenceCompleted"/> are separate boundaries:
/// stamping both at write time would report a queue that never had a backlog (ADR 0010).
/// </remarks>
internal sealed record QueuedExchangeRecord(ExchangeRecord Record, DateTimeOffset QueuedAt);
