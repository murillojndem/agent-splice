using System.IO.Pipelines;
using System.Text;
using AgentSplice.Api.Endpoints;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Identifiers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// How the transport sink reports a client that stopped listening
/// (docs/SPECIFICATION.md FR-CHAT-006, ADR 0010).
/// </summary>
/// <remarks>
/// The interesting cases are the ones that do not throw. <c>PipeWriter.WriteAsync</c> reports a
/// completed or cancelled pipe through its <see cref="FlushResult"/>, and a sink that only watched
/// for exceptions treated those as successful deliveries — so the relay kept reading the runtime and
/// kept recording bytes as having reached a client that was gone.
///
/// Lives in the integration suite because the sink is internal to <c>AgentSplice.Api</c>. Nothing
/// here needs a server: the claim is about one method's reading of one result.
/// </remarks>
public sealed class ClientResponseSinkTests
{
    private static readonly ReadOnlyMemory<byte> Payload = Encoding.UTF8.GetBytes("data: {}\n\n");

    [Fact]
    public async Task An_ordinary_flush_is_a_write()
    {
        var sink = Sink(new ScriptedPipeWriter(new FlushResult(isCanceled: false, isCompleted: false)));

        Assert.Equal(ClientWriteResult.Written, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task A_completed_pipe_reports_the_client_gone()
    {
        // The reader is finished with this pipe, so nothing written after it can reach the client.
        // No exception is raised, which is the whole reason this case was missed.
        var sink = Sink(new ScriptedPipeWriter(new FlushResult(isCanceled: false, isCompleted: true)));

        Assert.Equal(ClientWriteResult.ClientGone, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_flush_reports_the_client_gone()
    {
        // The flush was cut short, so these bytes are not known to have been delivered. Recording
        // them as delivered is the error; treating the client as gone is recoverable.
        var sink = Sink(new ScriptedPipeWriter(new FlushResult(isCanceled: true, isCompleted: false)));

        Assert.Equal(ClientWriteResult.ClientGone, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_write_reports_the_client_gone()
    {
        var sink = Sink(new ScriptedPipeWriter(new OperationCanceledException()));

        Assert.Equal(ClientWriteResult.ClientGone, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task A_broken_transport_reports_the_client_gone()
    {
        var sink = Sink(new ScriptedPipeWriter(new IOException("the connection was reset")));

        Assert.Equal(ClientWriteResult.ClientGone, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task A_write_to_a_connection_already_aborted_reports_the_client_gone()
    {
        // Kestrel completes a write to an aborted connection without sending it, and its flush then says
        // neither cancelled nor completed. A client that read a buffered answer's status line and hung up
        // before the body's write began was recorded as having received all of it; a Linux CI run under
        // load caught it, because over a socket it is a race.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var sink = Sink(new ScriptedPipeWriter(new FlushResult(isCanceled: false, isCompleted: false)), aborted.Token);

        Assert.Equal(ClientWriteResult.ClientGone, await sink.WriteAsync(Payload, CancellationToken.None));
    }

    [Fact]
    public async Task Starting_a_response_flushes_it_and_so_learns_the_client_is_gone()
    {
        // The status is recorded as sent when the start returns, so the start flushes (ADR 0025), and
        // that flush is the first point at which a client that already left can be noticed.
        var sink = Sink(new ScriptedPipeWriter(new FlushResult(isCanceled: false, isCompleted: true)));

        var start = new ClientResponseStart(
            200,
            "application/json",
            new GatewayCorrelation(PublicRequestId.New(), ExchangeId: null, TraceId: null, Runtime: null),
            new Dictionary<string, string>(),
            DisableCaching: false);

        Assert.Equal(ClientWriteResult.ClientGone, await sink.StartAsync(start, CancellationToken.None));
    }

    private static HttpClientResponseSink Sink(PipeWriter writer, CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };

        context.Features.Set<IHttpResponseBodyFeature>(new StubResponseBodyFeature(writer));

        return new HttpClientResponseSink(context);
    }

    /// <summary>A writer whose every write produces one scripted result, or one scripted fault.</summary>
    private sealed class ScriptedPipeWriter : PipeWriter
    {
        private readonly FlushResult result;
        private readonly Exception? fault;

        internal ScriptedPipeWriter(FlushResult result) => this.result = result;

        internal ScriptedPipeWriter(Exception fault) => this.fault = fault;

        public override ValueTask<FlushResult> WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default) =>
            fault is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<FlushResult>(fault);

        public override void Advance(int bytes) => throw new NotSupportedException();

        public override void CancelPendingFlush() => throw new NotSupportedException();

        public override void Complete(Exception? exception = null)
        {
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);

        public override Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

        public override Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
    }

    private sealed class StubResponseBodyFeature(PipeWriter writer) : IHttpResponseBodyFeature
    {
        public Stream Stream => Stream.Null;

        public PipeWriter Writer { get; } = writer;

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
