using AgentSplice.Application.Observability;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.UnitTests.Observability;

/// <summary>
/// Telemetry that records what it was asked to emit instead of emitting it.
/// </summary>
/// <remarks>
/// Lets a unit test assert that an instrument fires without attaching a real meter listener, which
/// is what makes "declared but never emitted" catchable in the suite rather than only by reading
/// the code.
/// </remarks>
internal sealed class RecordingExchangeTelemetry : IExchangeTelemetry
{
    internal List<(RuntimeEndpointId Runtime, TimeSpan Duration)> Discoveries { get; } = [];

    internal List<ExchangeTelemetrySnapshot> Exchanges { get; } = [];

    internal List<(PersistenceFailureReason Reason, int Count)> PersistenceFailures { get; } = [];

    public IExchangeTrace StartExchange() => new NoTrace();

    public IDisposable? StartProviderRequest(RuntimeEndpointId runtime, string providerKey) => null;

    public IDisposable? StartStream(RuntimeEndpointId runtime, string providerKey) => null;

    public IDisposable? StartPersistence(int batchSize) => null;

    public void RecordExchange(ExchangeTelemetrySnapshot snapshot) => Exchanges.Add(snapshot);

    public void RecordDiscovery(RuntimeEndpointId runtime, TimeSpan duration) =>
        Discoveries.Add((runtime, duration));

    public void RecordPersistenceFailure(PersistenceFailureReason reason, int count = 1) =>
        PersistenceFailures.Add((reason, count));

    private sealed class NoTrace : IExchangeTrace
    {
        public TraceId? TraceId => null;

        public void SetRuntime(RuntimeEndpointId runtime, string providerKey)
        {
        }

        public void SetOutcome(ExchangeStatus status, string? errorType)
        {
        }

        public void Dispose()
        {
        }
    }
}
