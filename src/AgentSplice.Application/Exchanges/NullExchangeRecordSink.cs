namespace AgentSplice.Application.Exchanges;

/// <summary>
/// Discards exchange evidence. The Stage 1A default.
/// </summary>
/// <remarks>
/// Stage 1A stores nothing (FR-DATA-001: purely ephemeral operation must be possible). Because
/// nothing is queued, the <c>MetadataQueued</c>, <c>PersistenceCompleted</c>, and
/// <c>PersistenceFailed</c> boundaries correctly stay absent from every timeline rather than being
/// recorded against a store that does not exist.
/// </remarks>
public sealed class NullExchangeRecordSink : IExchangeRecordSink
{
    /// <inheritdoc />
    public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
