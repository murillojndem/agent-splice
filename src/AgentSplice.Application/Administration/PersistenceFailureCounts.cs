using AgentSplice.Application.Observability;

namespace AgentSplice.Application.Administration;

/// <summary>
/// How many exchanges' evidence never reached the store since the gateway started, by reason
/// (ADR 0027).
/// </summary>
/// <remarks>
/// The metric that counts the same thing reaches no backend in a default deployment, because export
/// is off, and the two log events are read only by someone already looking. A store that silently
/// refuses every write looked, on the dashboard and the API, exactly like a quiet afternoon; these
/// counts are what <c>GET /api/v1/system</c> reports instead. Since start rather than persisted:
/// they describe this process, and a count that survived a restart would describe a store that may
/// since have been fixed.
/// </remarks>
public sealed class PersistenceFailureCounts
{
    private long queueSaturated;
    private long writeFailed;

    /// <summary>Exchanges dropped because the metadata queue was full.</summary>
    public long QueueSaturated => Interlocked.Read(ref queueSaturated);

    /// <summary>Exchanges lost because the store rejected the batch carrying them.</summary>
    public long WriteFailed => Interlocked.Read(ref writeFailed);

    /// <summary>Counts exchanges whose evidence was lost for one reason.</summary>
    public void Record(PersistenceFailureReason reason, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        switch (reason)
        {
            case PersistenceFailureReason.QueueSaturated:
                Interlocked.Add(ref queueSaturated, count);
                break;
            case PersistenceFailureReason.WriteFailed:
                Interlocked.Add(ref writeFailed, count);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown persistence failure reason.");
        }
    }
}
