using System.Text;
using System.Text.Json;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.IntegrationTests.Exchanges;

/// <summary>
/// Stage 0 exit criterion: a fake-upstream exchange is representable by the domain model
/// (docs/ROADMAP.md Stage 0).
/// </summary>
/// <remarks>
/// This test drives a real streamed HTTP exchange against the fake upstream and then records it with
/// the Stage 0 domain types. There is no gateway request path yet, so the test plays that role.
/// Doing so answers the question Stage 0 exists to answer: are the exchange, timeline, and
/// measurement types sufficient to describe a real streamed completion without inventing evidence?
///
/// Boundaries are timestamped from a <see cref="FakeTimeProvider"/> advanced at each observation, so
/// the assertions are about ordering, presence, and provenance rather than about machine speed. Real
/// latency numbers belong to the performance suite, not here.
/// </remarks>
public sealed class FakeUpstreamExchangeRepresentationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_streamed_fake_upstream_exchange_is_fully_representable()
    {
        var script = SseScript.Create()
            .Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}")
            .Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}")
            .Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}")
            .Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"choices\":[],\"usage\":{\"prompt_tokens\":41,\"completion_tokens\":7,\"total_tokens\":48}}")
            .Done()
            .Build();

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.EnqueueFor("/v1/chat/completions", script);

        var clock = new FakeTimeProvider(Origin);
        var exchangeId = ExchangeId.New();
        var timeline = new ExchangeTimeline(exchangeId);

        var exchange = CompletionExchange.Accept(
            exchangeId,
            PublicRequestId.FromExchangeId(exchangeId),
            IngressProtocol.OpenAiChatCompletions,
            ClientModelId.Create("local-coder"),
            streaming: true,
            clock.GetUtcNow(),
            ContentRetentionState.MetadataOnly);

        timeline.Append(ObservationType.RequestAccepted, clock.GetUtcNow());

        const string RequestBody =
            "{\"model\":\"local-coder\",\"stream\":true,\"messages\":[{\"role\":\"system\",\"content\":\"s\"},{\"role\":\"user\",\"content\":\"u\"}],\"reasoning_effort\":\"high\"}";

        clock.Advance(Step);
        timeline.Append(ObservationType.RequestBodyRead, clock.GetUtcNow());
        clock.Advance(Step);
        timeline.Append(ObservationType.ValidationCompleted, clock.GetUtcNow());

        var resolution = ModelResolution.FromAlias(
            ClientModelId.Create("local-coder"),
            ModelAliasId.Create("local-coder"),
            RuntimeEndpointId.Create("lmstudio-local"),
            UpstreamModelId.Create("qwen3.6-27b-mtp"));

        exchange = exchange.Resolve(resolution);

        clock.Advance(Step);
        timeline.Append(ObservationType.ModelResolved, clock.GetUtcNow());

        // FR-TRACE-007: the alias changed the model identifier, so the change is recorded rather
        // than applied silently.
        Assert.True(resolution.IsRoutingChange);
        clock.Advance(Step);
        timeline.Append(
            ObservationType.RoutingApplied,
            clock.GetUtcNow(),
            details: SafeDetails.Create(
                "alias.id",
                resolution.Alias?.Value,
                "runtime.id",
                resolution.Runtime.Value));

        var requestSummary = StructuralRequestSummary.Create(
            messageCount: 2,
            messageCountsByRole: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["system"] = 1,
                ["user"] = 1,
            },
            streamRequested: true,
            requestBodyBytes: Encoding.UTF8.GetByteCount(RequestBody),
            unknownTopLevelFieldNames: ["reasoning_effort"]);

        exchange = exchange.WithRequestSummary(requestSummary);
        clock.Advance(Step);
        timeline.Append(ObservationType.StructuralSummaryCreated, clock.GetUtcNow());

        using var client = upstream.CreateClient();
        using var timeout = new CancellationTokenSource(WaitBudget);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(upstream.OpenAiBaseAddress, "chat/completions"))
        {
            Content = new StringContent(RequestBody, Encoding.UTF8, "application/json"),
        };

        clock.Advance(Step);
        timeline.Append(ObservationType.UpstreamRequestOpened, clock.GetUtcNow());

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        clock.Advance(Step);
        timeline.Append(ObservationType.UpstreamHeadersReceived, clock.GetUtcNow());

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var (payload, eventCount) = await ReadStreamAsync(response, timeline, clock, timeout.Token);

        clock.Advance(Step);
        timeline.Append(ObservationType.UpstreamCompleted, clock.GetUtcNow());
        clock.Advance(Step);
        timeline.Append(
            ObservationType.ClientCompleted,
            clock.GetUtcNow(),
            details: SafeDetails.Create("stream.termination", nameof(StreamTermination.ProtocolTerminatorReceived)));

        var usage = ReadUsage(payload);

        exchange = exchange
            .WithResponseSummary(StructuralResponseSummary.Create(
                choiceCount: 1,
                finishReasons: ["stop"],
                responseBodyBytes: payload.Length,
                streamEventCount: eventCount,
                usageReported: !usage.IsUnknown))
            .WithUsage(usage)
            .Complete(clock.GetUtcNow(), StreamTermination.ProtocolTerminatorReceived);

        AssertExchangeIsCoherent(exchange, eventCount, payload.Length);
        AssertTimelineIsOrderedAndHonest(timeline);
        AssertMeasurementsCarryProvenance(exchange, timeline, payload.Length, eventCount);
        AssertForwardedRequestIsVerifiable(upstream, RequestBody);
    }

    private static async Task<(byte[] Payload, int EventCount)> ReadStreamAsync(
        HttpResponseMessage response,
        ExchangeTimeline timeline,
        FakeTimeProvider clock,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[64];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (!timeline.Contains(ObservationType.FirstUpstreamByte))
            {
                clock.Advance(Step);
                timeline.Append(ObservationType.FirstUpstreamByte, clock.GetUtcNow());
            }

            buffer.Write(chunk, 0, read);

            if (!timeline.Contains(ObservationType.FirstDecodedEvent)
                && ContainsCompleteEvent(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            {
                clock.Advance(Step);
                timeline.Append(ObservationType.FirstDecodedEvent, clock.GetUtcNow());
                clock.Advance(Step);
                timeline.Append(ObservationType.FirstClientEventFlushed, clock.GetUtcNow());
            }
        }

        var payload = buffer.ToArray();

        return (payload, CountEvents(Encoding.UTF8.GetString(payload)));
    }

    private static bool ContainsCompleteEvent(ReadOnlySpan<byte> received) =>
        Encoding.UTF8.GetString(received).Contains("\n\n", StringComparison.Ordinal);

    private static int CountEvents(string payload)
    {
        var count = 0;
        var index = 0;

        while ((index = payload.IndexOf("\n\n", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 2;
        }

        return count;
    }

    private static UsageObservation ReadUsage(byte[] payload)
    {
        foreach (var line in Encoding.UTF8.GetString(payload).Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..].Trim();

            if (data is "[DONE]" or "")
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);

            if (!document.RootElement.TryGetProperty("usage", out var usageElement)
                || usageElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            return UsageObservation.Create(
                promptTokens: ReadToken(usageElement, "prompt_tokens"),
                completionTokens: ReadToken(usageElement, "completion_tokens"),
                totalTokens: ReadToken(usageElement, "total_tokens"));
        }

        return UsageObservation.Unknown;
    }

    private static TokenCount? ReadToken(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var tokens)
            ? TokenCount.FromUpstream(tokens)
            : null;

    private static void AssertExchangeIsCoherent(
        CompletionExchange exchange,
        int eventCount,
        int payloadLength)
    {
        Assert.Equal(ExchangeStatus.Completed, exchange.Status);
        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, exchange.StreamTermination);
        Assert.Equal(IngressProtocol.OpenAiChatCompletions, exchange.IngressProtocol);
        Assert.Equal("lmstudio-local", exchange.RuntimeEndpointId?.Value);
        Assert.Equal("qwen3.6-27b-mtp", exchange.UpstreamModelId?.Value);
        Assert.Equal("local-coder", exchange.ClientModelId.Value);
        Assert.True(exchange.Streaming);
        Assert.True(exchange.IsTerminal);
        Assert.Null(exchange.FailureClass);

        // FR-TRACE-010: retention state is visible on the exchange, and no content was retained.
        Assert.Equal(ContentRetentionState.MetadataOnly, exchange.ContentRetentionState);

        // FR-CHAT-004 evidence: the unknown field was seen and nothing was dropped.
        Assert.Equal(["reasoning_effort"], exchange.RequestSummary?.UnknownTopLevelFieldNames);
        Assert.Empty(exchange.RequestSummary?.DroppedFieldNames ?? []);

        Assert.Equal(5, eventCount);
        Assert.Equal(eventCount, exchange.ResponseSummary?.StreamEventCount);
        Assert.Equal(payloadLength, exchange.ResponseSummary?.ResponseBodyBytes);
        Assert.Equal(["stop"], exchange.ResponseSummary?.FinishReasons);
        Assert.True(exchange.ResponseSummary?.UsageReported);

        Assert.Equal(41, exchange.Usage.PromptTokens?.Value);
        Assert.Equal(7, exchange.Usage.CompletionTokens?.Value);
        Assert.Equal(MeasurementProvenance.UpstreamReported, exchange.Usage.WeakestProvenance());
    }

    private static void AssertTimelineIsOrderedAndHonest(ExchangeTimeline timeline)
    {
        var observations = timeline.Observations;

        Assert.Equal(
            Enumerable.Range(0, observations.Count),
            observations.Select(observation => observation.Sequence));

        // Timestamps are non-decreasing because the clock only ever advanced.
        for (var index = 1; index < observations.Count; index++)
        {
            Assert.True(observations[index].Timestamp >= observations[index - 1].Timestamp);
        }

        ObservationType[] expected =
        [
            ObservationType.RequestAccepted,
            ObservationType.RequestBodyRead,
            ObservationType.ValidationCompleted,
            ObservationType.ModelResolved,
            ObservationType.RoutingApplied,
            ObservationType.StructuralSummaryCreated,
            ObservationType.UpstreamRequestOpened,
            ObservationType.UpstreamHeadersReceived,
            ObservationType.FirstUpstreamByte,
            ObservationType.FirstDecodedEvent,
            ObservationType.FirstClientEventFlushed,
            ObservationType.UpstreamCompleted,
            ObservationType.ClientCompleted,
        ];

        Assert.Equal(expected, observations.Select(observation => observation.Type));

        // FR-STR-012 and FR-TRACE-006: framing made the first decoded event observable, but nothing
        // in Stage 0 interprets payload semantics, so the semantic boundary stays absent rather than
        // being inferred from the decoded event.
        Assert.Null(timeline.TimestampOf(ObservationType.FirstSemanticEvent));
        Assert.False(timeline.Contains(ObservationType.FirstSemanticEvent));

        // Nothing timed out, nothing was cancelled, and Stage 0 persists nothing.
        Assert.False(timeline.Contains(ObservationType.TimeoutFired));
        Assert.False(timeline.Contains(ObservationType.ClientCancelled));
        Assert.False(timeline.Contains(ObservationType.PersistenceCompleted));

        // Every recorded detail is sanitised and bounded.
        foreach (var observation in observations)
        {
            Assert.True(observation.Details.Values.Count <= SafeDetails.MaxEntries);
            Assert.Equal(ObservationSource.Gateway, observation.Source);
        }

        Assert.Equal(
            "local-coder",
            timeline.Find(ObservationType.RoutingApplied)?.Details.Values["alias.id"]);
    }

    private static void AssertMeasurementsCarryProvenance(
        CompletionExchange exchange,
        ExchangeTimeline timeline,
        int payloadLength,
        int eventCount)
    {
        var exchangeId = exchange.ExchangeId;

        var headersDuration = timeline.DurationBetween(
            ObservationType.UpstreamRequestOpened,
            ObservationType.UpstreamHeadersReceived);

        var firstByteDuration = timeline.DurationBetween(
            ObservationType.UpstreamRequestOpened,
            ObservationType.FirstUpstreamByte);

        var totalDuration = timeline.DurationBetween(
            ObservationType.RequestAccepted,
            ObservationType.ClientCompleted);

        Assert.NotNull(headersDuration);
        Assert.NotNull(firstByteDuration);
        Assert.NotNull(totalDuration);

        var measurements = new List<Measurement>
        {
            Measurement.Duration(MeasurementNames.UpstreamHeadersDuration, headersDuration.Value, exchangeId),
            Measurement.Duration(MeasurementNames.TimeToFirstUpstreamByte, firstByteDuration.Value, exchangeId),
            Measurement.Duration(MeasurementNames.TotalDuration, totalDuration.Value, exchangeId),
            Measurement.Bytes(MeasurementNames.ClientResponseBytes, payloadLength, exchangeId),
            Measurement.Count(MeasurementNames.ClientStreamEvents, eventCount, exchangeId),
            Measurement.Tokens(MeasurementNames.PromptTokens, exchange.Usage.PromptTokens!.Value, exchangeId),
            Measurement.Tokens(MeasurementNames.CompletionTokens, exchange.Usage.CompletionTokens!.Value, exchangeId),
        };

        Assert.All(measurements, measurement => Assert.Equal(exchangeId, measurement.ExchangeId));
        Assert.All(measurements, measurement => Assert.True(double.IsFinite(measurement.Value)));

        // Generation throughput is derivable: the interval is measured and the token count is
        // upstream-reported.
        var generationDuration = timeline.DurationBetween(
            ObservationType.FirstUpstreamByte,
            ObservationType.UpstreamCompleted);

        var generationThroughput = ThroughputCalculator.TryCalculateGenerationThroughput(
            exchange.Usage.CompletionTokens,
            generationDuration,
            exchangeId);

        Assert.NotNull(generationThroughput);
        Assert.Equal(MeasurementNames.GenerationThroughput, generationThroughput.Name);
        Assert.Equal(MeasurementProvenance.UpstreamReported, generationThroughput.Provenance);

        // FR-OBS-004 and FR-OBS-005: prompt processing has no observable boundary in this exchange,
        // so prompt throughput stays unknown instead of borrowing the generation interval.
        Assert.Null(timeline.TimestampOf(ObservationType.FirstSemanticEvent));
        Assert.Null(ThroughputCalculator.TryCalculatePromptThroughput(
            exchange.Usage.PromptTokens,
            promptDuration: null,
            exchangeId));
    }

    private static void AssertForwardedRequestIsVerifiable(FakeUpstreamServer upstream, string requestBody)
    {
        var recorded = Assert.Single(upstream.ReceivedRequests);

        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v1/chat/completions", recorded.Path);
        Assert.Equal(requestBody, recorded.BodyAsText());
        Assert.False(recorded.WasAborted);
    }
}
