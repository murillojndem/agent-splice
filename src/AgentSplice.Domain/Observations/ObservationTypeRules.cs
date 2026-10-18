namespace AgentSplice.Domain.Observations;

/// <summary>
/// Invariants about how often a boundary may appear in one exchange timeline.
/// </summary>
/// <remarks>
/// "First" boundaries and lifecycle terminals are single-occurrence by definition. Recording a
/// second one would silently overwrite the real first-byte or completion evidence, which is exactly
/// the class of mistake FR-TRACE-006 and FR-STR-012 are written to prevent.
/// </remarks>
public static class ObservationTypeRules
{
    private static readonly HashSet<ObservationType> SingleOccurrenceTypes =
    [
        ObservationType.RequestAccepted,
        ObservationType.RequestBodyRead,
        ObservationType.ValidationCompleted,
        ObservationType.ModelResolved,
        ObservationType.StructuralSummaryCreated,
        ObservationType.UpstreamRequestOpened,

        // One exchange opens at most one connection. A second would mean the request was retried,
        // which Stage 1 never does.
        ObservationType.UpstreamConnectionStarted,
        ObservationType.UpstreamConnectionEstablished,
        ObservationType.UpstreamHeadersReceived,
        ObservationType.FirstUpstreamByte,
        ObservationType.FirstDecodedEvent,
        ObservationType.FirstSemanticEvent,
        ObservationType.FirstClientEventFlushed,
        ObservationType.UpstreamCompleted,
        ObservationType.ClientResponseStarted,
        ObservationType.ClientCompleted,
        ObservationType.ClientCancelled,
        ObservationType.ClientResponseAbandoned,

        // One upstream request, so at most one way it failed, and its first reported error is the
        // one worth dating.
        ObservationType.UpstreamFailed,
        ObservationType.UpstreamErrorReported,
    ];

    /// <summary>True when the boundary may appear at most once per exchange.</summary>
    public static bool IsSingleOccurrence(ObservationType type) => SingleOccurrenceTypes.Contains(type);
}
