using System.Net;
using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Providers.LmStudio;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// When the buffered path says the first upstream byte arrived
/// (docs/SPECIFICATION.md FR-TRACE-006, ADR 0010).
/// </summary>
/// <remarks>
/// The whole body is read before the provider returns, so a clock read after that read names the
/// moment the <em>last</em> byte arrived and files it as the first. The two are indistinguishable
/// for a short answer and the entire length of the response apart for a long one, which is exactly
/// when the boundary is worth having.
///
/// Proved with a body stream that advances a controllable clock between its reads. A real transport
/// cannot be asked to stall mid-body on demand, and a test that waited for a real delay would be
/// asserting on a race between the fixture and the reader.
/// </remarks>
public sealed class UpstreamFirstByteTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(20);

    private const string Completion =
        """{"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]}""";

    [Fact]
    public async Task The_first_byte_is_stamped_when_the_first_chunk_arrived_not_when_the_body_ended()
    {
        var clock = Clock();
        var body = Encoding.UTF8.GetBytes(Completion);

        var result = await CompleteAsync(clock, Chunks(body, Stall));

        Assert.True(result.Answered);

        var firstByte = Assert.NotNull(result.FirstByteAt);
        var completed = Assert.NotNull(result.CompletedAt);

        Assert.True(
            firstByte < Origin + Stall,
            FormattableString.Invariant($"The first byte was dated {firstByte}, after the {Stall} the body spent arriving."));

        Assert.True(
            completed >= Origin + Stall,
            FormattableString.Invariant($"Completion was dated {completed}, before the body had finished arriving."));

        Assert.NotEqual(firstByte, completed);
    }

    [Fact]
    public async Task A_later_chunk_never_overwrites_the_first_byte_boundary()
    {
        // Four chunks, each a second apart. A boundary reassigned per read would drift forwards and
        // end up naming the last one.
        var clock = Clock();
        var body = Encoding.UTF8.GetBytes(Completion);
        var second = TimeSpan.FromSeconds(1);

        var result = await CompleteAsync(clock, Chunks(body, second, parts: 4));

        Assert.True(Assert.NotNull(result.FirstByteAt) < Origin + (2 * second));
    }

    [Fact]
    public async Task A_body_that_ends_early_keeps_the_first_byte_it_did_produce()
    {
        // The failure branches carry the same boundary as the success branch: knowing the runtime
        // had started replying is exactly what separates "it never answered" from "it stopped".
        var clock = Clock();
        var body = Encoding.UTF8.GetBytes(Completion);

        var result = await CompleteAsync(
            clock,
            Chunks(body, Stall),
            declaredLength: body.Length + 4096);

        Assert.NotNull(result.Failure);
        Assert.True(Assert.NotNull(result.FirstByteAt) < Origin + Stall);
    }

    [Fact]
    public async Task A_response_that_produced_no_body_records_no_first_byte()
    {
        // Absent rather than stamped with the moment the empty read returned. No byte arrived, and a
        // boundary claiming one would be evidence of an event that never happened.
        var result = await CompleteAsync(Clock(), []);

        Assert.True(result.Answered);
        Assert.Null(result.FirstByteAt);
    }

    private static FakeTimeProvider Clock() =>
        new(Origin) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };

    /// <summary>Splits a body into equal chunks, letting the clock run between them.</summary>
    private static List<BodyChunk> Chunks(byte[] body, TimeSpan between, int parts = 2)
    {
        var size = (body.Length + parts - 1) / parts;
        var chunks = new List<BodyChunk>(parts);

        for (var offset = 0; offset < body.Length; offset += size)
        {
            var length = Math.Min(size, body.Length - offset);

            // The gap precedes every chunk but the first, so the first byte is available at the
            // origin and the last only after the whole stall has elapsed.
            chunks.Add(new BodyChunk(body.AsMemory(offset, length), offset == 0 ? TimeSpan.Zero : between));
        }

        return chunks;
    }

    private static async Task<ProviderCompletionResult> CompleteAsync(
        FakeTimeProvider clock,
        IReadOnlyList<BodyChunk> chunks,
        long? declaredLength = null)
    {
        var content = new StreamContent(new ScriptedBodyStream(clock, chunks));

        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (declaredLength is { } length)
        {
            content.Headers.ContentLength = length;
        }

        var provider = new LmStudioModelRuntimeProvider(
            new StubHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            new NoCredentialResolver(),
            Options.Create(new AgentSpliceOptions()),
            clock);

        var target = RuntimeTargetTests.Target("http://127.0.0.1:1234/v1");

        return await provider.CompleteAsync(
            ProviderCompletionRequest.Create(
                target,
                Encoding.UTF8.GetBytes("""{"model":"m","messages":[]}"""),
                "application/json",
                "application/json",
                PublicRequestId.New()),
            CancellationToken.None);
    }

    /// <summary>One scripted body read, and the time that passes before it becomes available.</summary>
    private readonly record struct BodyChunk(ReadOnlyMemory<byte> Bytes, TimeSpan Elapsed);

    /// <summary>
    /// A response body that hands out its chunks one read at a time, letting the clock run in
    /// between.
    /// </summary>
    private sealed class ScriptedBodyStream : Stream
    {
        private readonly FakeTimeProvider clock;
        private readonly Queue<BodyChunk> chunks;

        internal ScriptedBodyStream(FakeTimeProvider clock, IEnumerable<BodyChunk> chunks)
        {
            this.clock = clock;
            this.chunks = new Queue<BodyChunk>(chunks);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!chunks.TryDequeue(out var chunk))
            {
                return ValueTask.FromResult(0);
            }

            // Time passes before these bytes exist, which is what a runtime still generating looks
            // like from the reader's side.
            clock.Advance(chunk.Elapsed);
            chunk.Bytes.CopyTo(buffer);

            return ValueTask.FromResult(chunk.Bytes.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that answers every request with one scripted response.</summary>
    private sealed class StubHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response));

        private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(response);
        }
    }

    private sealed class NoCredentialResolver : IRuntimeApiKeyResolver
    {
        public RuntimeCredential Resolve(RuntimeTarget target) => RuntimeCredential.None;
    }
}
