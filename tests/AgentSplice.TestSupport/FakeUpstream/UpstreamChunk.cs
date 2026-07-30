namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// One write-and-flush the fake upstream performs, with the delay that precedes it.
/// </summary>
/// <param name="Bytes">Raw bytes written to the response body.</param>
/// <param name="Delay">Delay applied before the write.</param>
/// <param name="Gate">Held before the write until a test releases it, or <c>null</c> for no gate.</param>
/// <remarks>
/// Chunks are raw bytes rather than events on purpose. Tests must be able to split an SSE event
/// across arbitrary network and UTF-8 boundaries (docs/SPECIFICATION.md FR-STR-004), which is
/// impossible if the fixture can only emit whole events.
/// </remarks>
public readonly record struct UpstreamChunk(
    ReadOnlyMemory<byte> Bytes,
    TimeSpan Delay,
    UpstreamGate? Gate = null);
