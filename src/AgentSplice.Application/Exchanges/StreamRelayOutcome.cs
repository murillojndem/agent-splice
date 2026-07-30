using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Application.Exchanges;

/// <summary>How a relayed stream ended, and the evidence it produced.</summary>
/// <remarks>
/// <see cref="Error"/> and <see cref="Termination"/> answer different questions. A malformed payload
/// ends the stream with a recorded termination and no error, because the runtime answered and
/// AgentSplice did not fail; a bound violation ends it with both, because AgentSplice stopped a
/// response the client had already begun receiving.
/// </remarks>
public sealed record StreamRelayOutcome
{
    /// <summary>How the stream ended (FR-STR-011).</summary>
    public required StreamTermination Termination { get; init; }

    /// <summary>The failure to report, or <c>null</c> when AgentSplice did not fail.</summary>
    public GatewayError? Error { get; init; }

    /// <summary>The status relayed to the client.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The media type relayed to the client.</summary>
    public required string MediaType { get; init; }

    /// <summary>Bytes forwarded to the client.</summary>
    public long ClientBytes { get; init; }

    /// <summary>Events delivered to the client.</summary>
    public int ClientEvents { get; init; }

    /// <summary>Bytes of an event the stream ended in the middle of, or zero when it did not.</summary>
    public int IncompleteEventBytes { get; init; }

    /// <summary>
    /// True when the protocol's end-of-stream sentinel was seen.
    /// </summary>
    /// <remarks>
    /// Kept separately from <see cref="Termination"/> because both can be true and the enum holds
    /// one value: a stream can carry a malformed event and still end properly, and the anomaly is
    /// the more useful of the two to name.
    /// </remarks>
    public bool ProtocolTerminatorObserved { get; init; }

    /// <summary>The structural summary the observed events support, or <c>null</c> when none do.</summary>
    public StructuralResponseSummary? Summary { get; init; }

    /// <summary>Usage as the runtime reported it.</summary>
    public UsageObservation Usage { get; init; } = UsageObservation.Unknown;

    /// <summary>True when the client vanished before the response finished.</summary>
    public bool ClientGone { get; init; }

    /// <summary>True when a started response was abandoned rather than closed cleanly.</summary>
    public bool Aborted { get; init; }

    /// <summary>True when the response reached the client as an event stream.</summary>
    public bool Streamed { get; init; }
}
