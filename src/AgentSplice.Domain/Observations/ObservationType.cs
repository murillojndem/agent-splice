namespace AgentSplice.Domain.Observations;

/// <summary>
/// The timeline boundaries a Stage 1 completion exchange can represent
/// (docs/SPECIFICATION.md FR-TRACE-005 and section 15.1).
/// </summary>
/// <remarks>
/// Values are stable event identities. A boundary that was not observed is simply absent from the
/// timeline; there is deliberately no "unknown" member, because FR-TRACE-006 forbids fabricating
/// timing evidence.
/// </remarks>
public enum ObservationType
{
    /// <summary>The ingress request was accepted for processing.</summary>
    RequestAccepted = 1,

    /// <summary>The request body finished being read.</summary>
    RequestBodyRead = 2,

    /// <summary>Protocol shape validation completed.</summary>
    ValidationCompleted = 3,

    /// <summary>The client-visible model was resolved to a runtime and upstream model.</summary>
    ModelResolved = 4,

    /// <summary>A routing-only change was applied, such as alias substitution (FR-TRACE-007).</summary>
    RoutingApplied = 5,

    /// <summary>A privacy-safe structural summary of the request was created.</summary>
    StructuralSummaryCreated = 6,

    /// <summary>The upstream request was opened.</summary>
    UpstreamRequestOpened = 7,

    /// <summary>Upstream response headers were received.</summary>
    UpstreamHeadersReceived = 8,

    /// <summary>The first upstream body byte arrived.</summary>
    FirstUpstreamByte = 9,

    /// <summary>The first complete SSE event was decoded from the upstream stream.</summary>
    FirstDecodedEvent = 10,

    /// <summary>The first semantic output event was observed, when the protocol makes it observable.</summary>
    FirstSemanticEvent = 11,

    /// <summary>The first event was flushed to the client.</summary>
    FirstClientEventFlushed = 12,

    /// <summary>A native structured tool call was observed as protocol data (FR-CHAT-015).</summary>
    NativeToolCallObserved = 13,

    /// <summary>The upstream response completed normally.</summary>
    UpstreamCompleted = 14,

    /// <summary>The client response completed normally.</summary>
    ClientCompleted = 15,

    /// <summary>The client disconnected or cancelled (FR-CHAT-006).</summary>
    ClientCancelled = 16,

    /// <summary>A configured timeout phase fired (FR-CHAT-007, FR-CHAT-008).</summary>
    TimeoutFired = 17,

    /// <summary>Metadata was queued for persistence.</summary>
    MetadataQueued = 18,

    /// <summary>Metadata persistence completed.</summary>
    PersistenceCompleted = 19,

    /// <summary>Metadata persistence failed (FR-DATA-009).</summary>
    PersistenceFailed = 20,
}
