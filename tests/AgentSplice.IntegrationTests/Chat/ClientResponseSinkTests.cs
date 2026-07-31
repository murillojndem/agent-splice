using System.IO.Pipelines;
using System.Text;
using AgentSplice.Api.Endpoints;
using AgentSplice.Application.Exchanges;
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

    private static HttpClientResponseSink Sink(PipeWriter writer)
    {
        var context = new DefaultHttpContext();

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
