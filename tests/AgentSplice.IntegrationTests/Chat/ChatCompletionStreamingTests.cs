using System.Net;
using System.Text;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Streamed completions end to end, against a real listener
/// (docs/SPECIFICATION.md FR-STR-001 to FR-STR-012).
/// </summary>
/// <remarks>
/// Every assertion here is made from the client's side with an independent SSE parser. Reusing the
/// gateway's own parser would prove only that AgentSplice agrees with itself and would keep passing
/// through any framing bug the two share.
///
/// Waiting is gate-driven rather than timed. A test that sleeps is a test that is either slow or
/// flaky, and "the client saw event one before the runtime was allowed to send event two" is only a
/// real claim if the fixture guarantees the second half.
/// </remarks>
public sealed class ChatCompletionStreamingTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private const string RoleChunk = """{"choices":[{"index":0,"delta":{"role":"assistant"}}]}""";
    private const string ContentChunk = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";
    private const string FinishChunk = """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";
    private const string UsageChunk =
        """{"choices":[],"usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}""";

    [Fact]
    public async Task A_streamed_completion_reaches_the_client_byte_for_byte()
    {
        // The strongest statement of transparency available: not "an equivalent stream" but the
        // runtime's own bytes. A relay that decoded and re-emitted would normalise escapes and
        // number formats and still pass a semantic comparison.
        var script = SseScript.Create()
            .Data(RoleChunk)
            .Data(ContentChunk)
            .Data(FinishChunk)
            .Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(script.ToBytes(), body);
    }

    [Fact]
    public async Task An_event_reaches_the_client_before_the_runtime_is_allowed_to_send_the_next()
    {
        // FR-STR-003. The weak form of this test reads the whole stream and counts events, which
        // passes just as well on a gateway that buffered everything and flushed once at the end —
        // the exact failure that makes a streaming proxy useless for an interactive client.
        var gate = new UpstreamGate();

        var script = SseScript.Create()
            .Data(ContentChunk)
            .Gate(gate)
            .Data(FinishChunk)
            .Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendAsync(fixture);
        using var reader = new SseClientReader(await response.Content.ReadAsStreamAsync());

        var first = await reader.ReadEventAsync();

        Assert.NotNull(first);
        Assert.Equal(ContentChunk, first.Data);

        // The runtime has not been allowed to send anything else yet, so the client holding this
        // event proves it was forwarded rather than accumulated.
        await gate.WaitForReachedAsync(WaitBudget);
        gate.Release();

        var rest = await reader.ReadToEndAsync();

        Assert.Equal(3, rest.Count);
        Assert.Equal("[DONE]", rest[^1].Data);
    }

    [Fact]
    public async Task An_event_stream_split_one_byte_at_a_time_arrives_intact()
    {
        var script = SseScript.Create()
            .Data(ContentChunk)
            .Done()
            .SplitByteByByte();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);

        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_multi_byte_character_split_across_reads_arrives_intact()
    {
        // FR-STR-004. A relay that decoded text would either throw here or substitute a replacement
        // character, and the client would receive something the runtime never sent.
        var script = SseScript.Create()
            .Data("""{"choices":[{"delta":{"content":"café 世界 🙂"}}]}""")
            .Done()
            .SplitByteByByte();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);

        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Bytes_that_are_not_valid_utf8_are_still_forwarded_unchanged()
    {
        // A runtime emitting a malformed byte is misbehaving, but mangling its output would replace
        // one diagnosis with a harder one: the user would be debugging AgentSplice instead.
        var script = SseScript.Create()
            .RawBytes(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)':', (byte)' ', 0xC3, (byte)'\n', (byte)'\n' })
            .Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);

        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Crlf_multiline_data_and_comments_all_survive_the_relay()
    {
        // FR-STR-005. Each of these is a shape a naive line-based relay silently corrupts.
        var script = SseScript.Create()
            .UseCrLf()
            .Comment("keepalive")
            .MultilineData(["""{"choices":[{"delta":{"content":"one""", """two"}}]}"""])
            .Retry(TimeSpan.FromSeconds(3))
            .Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);

        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_streamed_response_is_marked_uncacheable()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Done().Build());

        using var response = await PostAsync(fixture);

        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_streamed_response_carries_the_correlation_headers()
    {
        // These are committed before the first byte of the body, which is the only chance a streamed
        // response ever gets to carry them.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Done().Build());

        using var response = await PostAsync(fixture);

        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.True(response.Headers.Contains("x-agentsplice-exchange-id"));
        Assert.True(response.Headers.Contains("x-agentsplice-runtime"));
    }

    [Fact]
    public async Task The_done_sentinel_is_recorded_as_the_protocol_terminator()
    {
        var record = await ProxyAsync(SseScript.Create().Data(ContentChunk).Done().Build());

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, record.Exchange.StreamTermination);
        Assert.True(record.Exchange.StreamedResponse);
        Assert.Null(record.Exchange.FailureClass);
    }

    [Fact]
    public async Task A_repeated_terminator_is_relayed_and_recorded_once()
    {
        // docs/TESTING.md's "duplicate terminal event" family. The boundaries the relay records are
        // single-occurrence in the domain, so a second terminator that re-recorded one would throw
        // from the timeline mid-stream — after bytes had already been flushed, where the failure
        // could no longer be expressed as a status.
        var script = SseScript.Create().Data(ContentChunk).Done().Done();

        var record = await ProxyAsync(script.Build());

        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, record.Exchange!.StreamTermination);
        Assert.Null(record.Exchange.FailureClass);

        // Both terminators reached the client: the runtime sent them, so the client sees them.
        Assert.Equal(3, record.Exchange.ResponseSummary?.StreamEventCount);

        Assert.Single(record.Observations, o => o.Type == ObservationType.FirstDecodedEvent);
        Assert.Single(record.Observations, o => o.Type == ObservationType.FirstClientEventFlushed);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_terminator_completes_normally()
    {
        // Not every OpenAI-compatible runtime sends the sentinel. Ending without one is a complete
        // answer, and the two endings are recorded distinctly rather than being collapsed.
        var record = await ProxyAsync(SseScript.Create().Data(ContentChunk).Build());

        Assert.Equal(StreamTermination.NormalCompletion, record.Exchange!.StreamTermination);
        Assert.Null(record.Exchange.FailureClass);
    }

    [Fact]
    public async Task A_streamed_exchange_records_the_streaming_boundaries_in_order()
    {
        var record = await ProxyAsync(
            SseScript.Create().Data(RoleChunk).Data(ContentChunk).Data(UsageChunk).Done().Build());

        Assert.Equal(
            [
                ObservationType.RequestAccepted,
                ObservationType.RequestBodyRead,
                ObservationType.ValidationCompleted,
                ObservationType.StructuralSummaryCreated,
                ObservationType.ModelResolved,
                ObservationType.RoutingApplied,
                ObservationType.UpstreamRequestOpened,
                ObservationType.UpstreamConnectionStarted,
                ObservationType.UpstreamConnectionEstablished,
                ObservationType.UpstreamHeadersReceived,
                ObservationType.FirstUpstreamByte,
                ObservationType.FirstDecodedEvent,
                ObservationType.FirstClientEventFlushed,
                ObservationType.FirstSemanticEvent,
                ObservationType.UpstreamCompleted,
                ObservationType.ClientCompleted,
            ],
            record.Observations.Select(observation => observation.Type));
    }

    [Fact]
    public async Task A_role_only_first_chunk_leaves_the_semantic_boundary_for_the_chunk_that_carries_output()
    {
        // The load-bearing distinction. Time to first token is not time to first chunk, and an
        // OpenAI-compatible runtime's first chunk is almost always a role announcement carrying no
        // output at all (FR-STR-012).
        var gate = new UpstreamGate();

        var record = await ProxyAsync(
            SseScript.Create()
                .Data(RoleChunk)
                .Gate(gate)
                .Data(ContentChunk)
                .Done()
                .Build(),
            releaseAfterFirstEvent: gate);

        var decoded = Timestamp(record, ObservationType.FirstDecodedEvent);
        var semantic = Timestamp(record, ObservationType.FirstSemanticEvent);

        Assert.True(
            decoded < semantic,
            FormattableString.Invariant($"The decoded boundary at {decoded} did not precede the semantic one at {semantic}."));
    }

    [Fact]
    public async Task A_stream_of_keepalives_alone_records_no_semantic_boundary()
    {
        // A comment holds a connection open; it is not output, and a boundary claiming otherwise
        // would report a time to first token for a response that produced none.
        var record = await ProxyAsync(
            SseScript.Create().Comment("keepalive").Comment("keepalive").Build());

        Assert.DoesNotContain(
            ObservationType.FirstSemanticEvent,
            record.Observations.Select(observation => observation.Type));

        Assert.Contains(
            ObservationType.FirstDecodedEvent,
            record.Observations.Select(observation => observation.Type));
    }

    [Fact]
    public async Task Keepalives_are_relayed_but_not_counted_as_delivered_events()
    {
        var record = await ProxyAsync(
            SseScript.Create().Comment("keepalive").Data(ContentChunk).Done().Build());

        // Two data events; the comment is framing the client raises no event for.
        Assert.Equal(2, record.Exchange!.ResponseSummary?.StreamEventCount);
    }

    [Fact]
    public async Task A_terminal_usage_chunk_is_recorded_with_upstream_provenance()
    {
        // FR-STR-010. The usage chunk carries no choices, so a reader that keyed on choices would
        // discard exactly the chunk that carries the token counts.
        var record = await ProxyAsync(
            SseScript.Create().Data(ContentChunk).Data(UsageChunk).Done().Build());

        Assert.Equal(41, record.Exchange!.Usage.PromptTokens?.Value);
        Assert.Equal(7, record.Exchange.Usage.CompletionTokens?.Value);
        Assert.Equal(MeasurementProvenance.UpstreamReported, record.Exchange.Usage.WeakestProvenance());
        Assert.True(record.Exchange.ResponseSummary?.UsageReported);
    }

    [Fact]
    public async Task Generation_throughput_is_derived_and_prompt_throughput_is_not()
    {
        // FR-OBS-005. The generation window is observable; no boundary separates prompt processing
        // from anything else, so deriving prompt throughput would mean borrowing this same interval
        // and calling it something different.
        var record = await ProxyAsync(
            SseScript.Create().Data(ContentChunk).Data(UsageChunk).Done().Build());

        Assert.DoesNotContain(record.Measurements, m => m.Name == MeasurementNames.PromptThroughput);
        Assert.Contains(record.Measurements, m => m.Name == MeasurementNames.ClientStreamEvents);
        Assert.Contains(record.Measurements, m => m.Name == MeasurementNames.TimeToFirstClientEvent);
    }

    [Fact]
    public async Task The_finish_reason_is_recorded_verbatim()
    {
        var record = await ProxyAsync(
            SseScript.Create().Data(ContentChunk).Data(FinishChunk).Done().Build());

        Assert.Equal(["stop"], record.Exchange!.ResponseSummary?.FinishReasons);
    }

    [Fact]
    public async Task Unknown_request_fields_still_reach_the_runtime_verbatim_when_streaming()
    {
        const string Body =
            """{"model":"local-coder","stream":true,"reasoning_effort":"high","messages":[{"role":"user","content":"hi"}]}""";

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Done().Build());

        using var response = await PostAsync(fixture, Body);

        await response.Content.ReadAsByteArrayAsync();

        // Only the bytes of the model value move, and only because the alias renames the model. Every
        // other byte, including a field AgentSplice does not model, is the client's own.
        Assert.Equal(
            Body.Replace("local-coder", "qwen3.6-27b-mtp", StringComparison.Ordinal),
            Assert.Single(fixture.Upstream.ReceivedRequests).BodyAsText());
    }

    [Fact]
    public async Task A_long_stream_passes_with_the_buffered_bound_set_far_below_it()
    {
        // The roadmap's exit criterion, as behaviour rather than an allocation measurement: with the
        // buffered ceiling at 64 KiB, eight megabytes can only arrive if the streaming path never
        // routes through it.
        var filler = new string('x', 1000);
        var script = SseScript.Create();

        for (var i = 0; i < 8 * 1024; i++)
        {
            script.Data("{\"choices\":[{\"delta\":{\"content\":\"" + filler + "\"}}]}");
        }

        script.Done();

        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            settings => settings["agentsplice:limits:maxUpstreamCompletionBodyBytes"] = "65536",
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await PostAsync(fixture);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Length > 8 * 1024 * 1024);
        Assert.Equal(script.ToBytes().Length, body.Length);

        // Waited for rather than assumed. The client's last byte arrives before the gateway has
        // finished recording, so a test that stopped here would tear the host down mid-request — and
        // it would also be asserting only that the bytes arrived, not that eight megabytes were
        // observed correctly on the way past.
        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, record.Exchange!.StreamTermination);
        Assert.Equal(8 * 1024 + 1, record.Exchange.ResponseSummary?.StreamEventCount);
    }

    private static DateTimeOffset Timestamp(ExchangeRecord record, ObservationType type) =>
        record.Observations.Single(observation => observation.Type == type).Timestamp;

    private static async Task<ExchangeRecord> ProxyAsync(
        UpstreamResponseScript script,
        UpstreamGate? releaseAfterFirstEvent = null)
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            configure: null,
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script);

        using var response = await SendAsync(fixture);

        if (releaseAfterFirstEvent is { } gate)
        {
            // Held open until the client has actually taken the first event, so the boundary the
            // test is about is separated in time from the one that follows it.
            using var reader = new SseClientReader(await response.Content.ReadAsStreamAsync());

            await reader.ReadEventAsync();
            await gate.WaitForReachedAsync(WaitBudget);
            gate.Release();
            await reader.ReadToEndAsync();
        }
        else
        {
            await response.Content.ReadAsByteArrayAsync();
        }

        return await sink.WaitForRecordAsync(WaitBudget);
    }

    private static Task<GatewayFixture> StartAsync(
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
                settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
                configure?.Invoke(settings);
            },
            configureServices);

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture, string? body = null) =>
        SendAsync(fixture, body, HttpCompletionOption.ResponseContentRead);

    private static async Task<HttpResponseMessage> SendAsync(
        GatewayFixture fixture,
        string? body = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        using var content = new StringContent(
            body ?? """{"model":"local-coder","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = content,
        };

        return await fixture.Client.SendAsync(request, completion);
    }
}
