using System.Diagnostics;
using System.Net;
using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Providers.LmStudio;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// Which budget bounds each read of a stream, and which phase a timeout is reported as
/// (docs/SPECIFICATION.md FR-CHAT-006, FR-CHAT-007, FR-CHAT-008; ADR 0028).
/// </summary>
/// <remarks>
/// Driven through the provider's public surface, with a scripted body whose reads end only when the
/// token they were given is cancelled — or, for one step, return bytes at that moment. Which budget
/// ended a read is therefore decided by the code under test rather than by a race between the
/// script's timing and the gateway's: every budget that must not fire is far longer than the one that
/// must, and a read that waits on the wrong one either reports the wrong phase or never ends, which
/// the guard on every read turns into a failure rather than a hang.
/// </remarks>
public sealed class UpstreamStreamBudgetTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Shorter = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private const string Event = "data: {\"choices\":[]}\n\n";

    [Fact]
    public async Task Nothing_after_a_streams_headers_times_out_in_the_first_byte_phase()
    {
        // The idle budget is the shorter of the two on purpose: a first read that waited on it, as
        // every read did before ADR 0028, would report the idle phase instead.
        await using var body = await OpenAsync(
            [Step.Hold()],
            RuntimeTargetTests.Timeouts(firstByte: Short, idleStream: Shorter, total: Long));

        var failure = (await ReadAsync(body)).Failure;

        Assert.NotNull(failure);
        Assert.Equal(TimeoutPhase.FirstByte, failure.Phase);
    }

    [Fact]
    public async Task A_stall_after_the_first_byte_times_out_in_the_idle_phase()
    {
        // The first-byte budget is the longer one, and it is disarmed with the first byte: a read
        // after that byte that still waited on it would never end.
        await using var body = await OpenAsync(
            [Step.Bytes(Event), Step.Hold()],
            RuntimeTargetTests.Timeouts(firstByte: Long, idleStream: Short, total: Long));

        Assert.Equal(Encoding.UTF8.GetByteCount(Event), (await ReadAsync(body)).BytesRead);

        var failure = (await ReadAsync(body)).Failure;

        Assert.NotNull(failure);
        Assert.Equal(TimeoutPhase.IdleStream, failure.Phase);
    }

    [Fact]
    public async Task A_first_byte_that_arrives_as_its_budget_elapses_leaves_a_later_stall_in_the_idle_phase()
    {
        // The first byte arrives in the instant its budget elapses: the read returns bytes, and the
        // budget's token stays signalled, because disarming a source that has already fired does
        // nothing. The stall that follows is the idle budget's, and a classifier that consulted the
        // first-byte token before the idle one would blame the runtime for a slow start it did not
        // make.
        await using var body = await OpenAsync(
            [Step.BytesWhenCancelled(Event), Step.Hold()],
            RuntimeTargetTests.Timeouts(firstByte: Short, idleStream: Short, total: Long));

        Assert.Equal(Encoding.UTF8.GetByteCount(Event), (await ReadAsync(body)).BytesRead);

        var failure = (await ReadAsync(body)).Failure;

        Assert.NotNull(failure);
        Assert.Equal(TimeoutPhase.IdleStream, failure.Phase);
    }

    [Fact]
    public async Task A_client_that_leaves_before_the_first_byte_is_a_cancellation()
    {
        // Until the first byte a read waits on the first-byte budget alone, so that budget must be
        // linked to the client: unlinked, the runtime's request would outlive the client by up to the
        // whole budget (FR-CHAT-006).
        using var client = new CancellationTokenSource();

        await using var body = await OpenAsync(
            [Step.Hold()],
            RuntimeTargetTests.Timeouts(firstByte: Long, idleStream: Long, total: Long),
            client.Token);

        var pending = body.ReadAsync(new byte[1024], client.Token).AsTask();
        await client.CancelAsync();

        var failure = (await pending.WaitAsync(Guard)).Failure;

        Assert.NotNull(failure);
        Assert.Equal(UpstreamFailureReason.Cancelled, failure.Reason);
    }

    private static async Task<IUpstreamResponseBody> OpenAsync(
        IReadOnlyList<Step> steps,
        RuntimeTimeouts timeouts,
        CancellationToken clientToken = default)
    {
        var content = new StreamContent(new ScriptedStream(steps));

        content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

        var provider = new LmStudioModelRuntimeProvider(
            new StubHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            new NoCredentialResolver(),
            Options.Create(new AgentSpliceOptions()),
            TimeProvider.System);

        var result = await provider.StreamAsync(
            ProviderCompletionRequest.Create(
                RuntimeTargetTests.Target("http://127.0.0.1:1234/v1", timeouts: timeouts),
                Encoding.UTF8.GetBytes("""{"model":"m","messages":[],"stream":true}"""),
                "application/json",
                "text/event-stream",
                PublicRequestId.New()),
            clientToken);

        Assert.True(result.Opened, "The provider did not open the scripted stream.");

        return result.Body!;
    }

    private static async Task<UpstreamReadResult> ReadAsync(IUpstreamResponseBody body) =>
        await body.ReadAsync(new byte[1024], CancellationToken.None).AsTask().WaitAsync(Guard);

    /// <summary>One scripted read of the runtime's body.</summary>
    private sealed record Step(byte[]? Payload, bool ArrivesWhenCancelled)
    {
        /// <summary>Bytes that are already there.</summary>
        internal static Step Bytes(string text) => new(Encoding.UTF8.GetBytes(text), ArrivesWhenCancelled: false);

        /// <summary>Nothing more: the read ends only when the token it was given is cancelled.</summary>
        internal static Step Hold() => new(Payload: null, ArrivesWhenCancelled: true);

        /// <summary>Bytes that arrive in the instant the read's token is cancelled.</summary>
        internal static Step BytesWhenCancelled(string text) => new(Encoding.UTF8.GetBytes(text), ArrivesWhenCancelled: true);
    }

    /// <summary>A response body that answers each read with the next scripted step.</summary>
    private sealed class ScriptedStream(IEnumerable<Step> steps) : Stream
    {
        private readonly Queue<Step> steps = new(steps);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!steps.TryDequeue(out var step))
            {
                return 0;
            }

            if (step.Payload is null)
            {
                // A transport read throws when its token is cancelled, and this one does the same.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

                throw new UnreachableException("An infinite delay ended without its token being cancelled.");
            }

            if (step.ArrivesWhenCancelled)
            {
                await WhenCancelledAsync(cancellationToken).ConfigureAwait(false);
            }

            step.Payload.CopyTo(buffer);

            return step.Payload.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task WhenCancelledAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The moment the budget elapsed is the moment these bytes arrive.
            }
        }
    }
}
