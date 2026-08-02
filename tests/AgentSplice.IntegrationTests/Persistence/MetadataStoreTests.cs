using System.Text;
using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentSplice.IntegrationTests.Persistence;

/// <summary>
/// What a real proxied request leaves in a real SQLite store (FR-DATA-001, FR-DATA-002, FR-DATA-009).
/// </summary>
/// <remarks>
/// The gateway is the production host and the store is a real database file, because the property
/// under test is that evidence survives the process boundary. A test double for either half would
/// prove the mapper compiles and nothing more.
///
/// Writing happens on a background service, so each assertion waits for the row rather than reading
/// immediately. Polling with a timeout is deliberate: a test that slept a fixed interval would either
/// be slow or flaky, and one that reached into the writer to await it would be testing a seam the
/// product does not have.
/// </remarks>
public sealed class MetadataStoreTests
{
    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"qwen3.6-27b-mtp",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}
        """;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_completed_exchange_is_readable_from_the_store()
    {
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);

        var row = await SingleExchangeAsync(store);

        Assert.Equal("qwen3.6-27b-mtp", row.ClientModelId);
        Assert.Equal(GatewayFixture.RuntimeId, row.RuntimeEndpointId);
        Assert.Equal((int)ExchangeStatus.Completed, row.Status);
        Assert.Equal(200, row.UpstreamStatusCode);
        Assert.NotNull(row.CompletedAtTicks);
    }

    [Fact]
    public async Task The_stored_timeline_ends_with_the_boundaries_the_store_itself_stamped()
    {
        // Neither exists in the record the gateway hands over: MetadataQueued is stamped by the sink
        // as the record enters the queue, and PersistenceCompleted only after the write has returned.
        // Stamping either one earlier would report a queue that never had a backlog (ADR 0010).
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);
        await SingleExchangeAsync(store);

        var types = await WaitForAsync(
            store,
            async context => await context.Observations
                .OrderBy(observation => observation.Sequence)
                .Select(observation => observation.Type)
                .ToListAsync(),
            types => types.Contains((int)ObservationType.PersistenceCompleted));

        Assert.Equal((int)ObservationType.RequestAccepted, types[0]);
        Assert.Equal((int)ObservationType.MetadataQueued, types[^2]);
        Assert.Equal((int)ObservationType.PersistenceCompleted, types[^1]);

        // One boundary per position, in the order they were recorded.
        Assert.Equal(types.Count, types.Distinct().Count());
    }

    [Fact]
    public async Task The_queued_boundary_precedes_the_completion_boundary()
    {
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);
        await SingleExchangeAsync(store);

        var stamps = await WaitForAsync(
            store,
            async context => await context.Observations
                .Where(observation =>
                    observation.Type == (int)ObservationType.MetadataQueued
                    || observation.Type == (int)ObservationType.PersistenceCompleted)
                .OrderBy(observation => observation.Sequence)
                .Select(observation => observation.TimestampTicks)
                .ToListAsync(),
            found => found.Count == 2);

        Assert.True(stamps[0] <= stamps[1]);
    }

    [Fact]
    public async Task Measurements_are_stored_with_their_provenance_and_nothing_else_is_invented()
    {
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);
        await SingleExchangeAsync(store);

        var names = await WaitForAsync(
            store,
            async context => await context.Measurements.Select(m => m.Name).ToListAsync(),
            found => found.Count > 0);

        Assert.Contains(MeasurementNames.TotalDuration, names, StringComparer.Ordinal);
        Assert.Contains(MeasurementNames.PromptTokens, names, StringComparer.Ordinal);

        // A buffered exchange has no stream, so no stream-event count is stored — not a zero, which
        // would read as "it streamed and delivered nothing" (FR-OBS-004).
        Assert.DoesNotContain(MeasurementNames.ClientStreamEvents, names, StringComparer.Ordinal);
        Assert.DoesNotContain(MeasurementNames.GenerationThroughput, names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_persistence_delay_is_measured_by_the_store_that_produced_it()
    {
        // The one measurement the exchange record cannot carry: it is frozen before the write, and
        // the interval does not exist until afterwards. Declared in MeasurementNames since Stage 1A
        // and unproducible until now, which is the shape of contract this repository has had to
        // correct before.
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);
        await SingleExchangeAsync(store);

        var persistence = await WaitForAsync(
            store,
            async context => await context.Measurements
                .Where(measurement => measurement.Name == MeasurementNames.PersistenceDuration)
                .ToListAsync(),
            found => found.Count > 0);

        var measured = Assert.Single(persistence);

        Assert.Equal((int)MeasurementProvenance.Measured, measured.Provenance);
        Assert.Equal((int)MeasurementUnit.Milliseconds, measured.Unit);
        Assert.True(measured.Value >= 0d);

        // Queue-to-durable, so both ends of the interval are recorded and a reader can check the
        // number against the two boundaries rather than taking it on trust.
        Assert.NotNull(measured.StartedAtTicks);
        Assert.NotNull(measured.EndedAtTicks);
        Assert.True(measured.EndedAtTicks >= measured.StartedAtTicks);
    }

    [Fact]
    public async Task No_prompt_or_response_content_reaches_the_store()
    {
        // The single assertion this whole module exists to keep true. Metadata has to stay useful
        // without body content, and content retention is off by default (FR-DATA-005).
        const string PromptSentinel = "SENTINEL-PROMPT-abc123";
        const string ResponseSentinel = "SENTINEL-RESPONSE-xyz789";

        using var store = new TemporaryMetadataStore();

        await ProxyAsync(
            store,
            body: $$"""{"model":"qwen3.6-27b-mtp","messages":[{"role":"user","content":"{{PromptSentinel}}"}]}""",
            completion: Completion.Replace("hello", ResponseSentinel, StringComparison.Ordinal));

        await SingleExchangeAsync(store);

        using var context = store.OpenContext();

        foreach (var row in await context.Exchanges.ToListAsync())
        {
            AssertClean(row.RequestSummaryJson);
            AssertClean(row.ResponseSummaryJson);
            AssertClean(row.UsageJson);
            AssertClean(row.ClientModelId);
        }

        foreach (var observation in await context.Observations.ToListAsync())
        {
            AssertClean(observation.DetailsJson);
        }

        static void AssertClean(string? value)
        {
            if (value is null)
            {
                return;
            }

            Assert.DoesNotContain(PromptSentinel, value, StringComparison.Ordinal);
            Assert.DoesNotContain(ResponseSentinel, value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task No_caller_chosen_text_reaches_the_store_through_a_metadata_field()
    {
        // The adversarial half of the leakage suite. The previous test puts its sentinels in
        // "content", which the gateway never reads — so it passed while three fields that AgentSplice
        // does record carried whatever the caller put in them straight into SQLite with content
        // capture disabled: the role, the name of any unknown property, and the runtime's own
        // finish_reason (FR-DATA-005, docs/SECURITY.md "Safe structural summaries must not
        // reconstruct sensitive payloads accidentally").
        const string RoleSentinel = "SENTINEL-ROLE-aaa111";
        const string FieldSentinel = "SENTINEL-FIELD-bbb222";
        const string FinishSentinel = "SENTINEL-FINISH-ccc333";

        using var store = new TemporaryMetadataStore();

        var completion = Completion.Replace("\"stop\"", $"\"{FinishSentinel}\"", StringComparison.Ordinal);

        await ProxyAsync(
            store,
            body: $$"""
                {"model":"qwen3.6-27b-mtp",
                 "messages":[{"role":"{{RoleSentinel}}","content":"hi"}],
                 "{{FieldSentinel}}":1}
                """,
            completion: completion);

        await SingleExchangeAsync(store);

        using var context = store.OpenContext();
        var stored = new List<string>();

        foreach (var row in await context.Exchanges.ToListAsync())
        {
            stored.Add(row.RequestSummaryJson ?? string.Empty);
            stored.Add(row.ResponseSummaryJson ?? string.Empty);
            stored.Add(row.ClientModelId ?? string.Empty);
        }

        foreach (var observation in await context.Observations.ToListAsync())
        {
            stored.Add(observation.DetailsJson ?? string.Empty);
        }

        var everything = string.Join('\n', stored);

        Assert.DoesNotContain("SENTINEL", everything, StringComparison.OrdinalIgnoreCase);

        // Not merely absent: replaced by something a reader can interpret. The role and the finish
        // reason become buckets, and the field name becomes a hash an operator can reproduce.
        Assert.Contains(SafeVocabulary.Unrecognised, everything, StringComparison.Ordinal);
        Assert.Contains(SafeVocabulary.HashName(FieldSentinel), everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_exchange_declares_that_metadata_was_retained()
    {
        // Every row used to say Disabled, whose documented meaning is "nothing was retained", while
        // holding summaries, observations, and measurements. ExchangeRecorder.Accept opens every
        // exchange that way and cannot know better; only the store knows whether it is storing
        // (FR-TRACE-010).
        using var store = new TemporaryMetadataStore();

        await ProxyAsync(store);

        Assert.Equal((int)ContentRetentionState.MetadataOnly, (await SingleExchangeAsync(store)).ContentRetentionState);
    }

    [Fact]
    public async Task A_request_refused_before_its_envelope_was_read_also_declares_metadata_retention()
    {
        // Such a record has no CompletionExchange to carry the state at all, so a row built from one
        // would have had to invent it. The store supplies it because the store is what retained.
        using var store = new TemporaryMetadataStore();

        var row = await RefusedAsync(store, "{ not json at all", System.Net.HttpStatusCode.BadRequest);

        Assert.Equal((int)ContentRetentionState.MetadataOnly, row.ContentRetentionState);
    }

    [Fact]
    public async Task A_request_naming_an_unknown_model_keeps_the_name_and_records_no_runtime()
    {
        // The exchange is opened before resolution runs, deliberately, so a request naming an unknown
        // model still leaves evidence of what arrived. The client's own string is what it asked for
        // and is stored as such; the runtime stays absent because none was ever chosen.
        using var store = new TemporaryMetadataStore();

        var row = await RefusedAsync(
            store,
            """{"model":"a-model-nobody-configured","messages":[{"role":"user","content":"hi"}]}""",
            System.Net.HttpStatusCode.NotFound);

        Assert.Equal("a-model-nobody-configured", row.ClientModelId);
        Assert.Equal((int)ExchangeStatus.Failed, row.Status);
        Assert.Equal((int)FailureClass.ModelNotFound, row.FailureClass);
        Assert.Null(row.RuntimeEndpointId);
        Assert.Null(row.UpstreamModelId);
        Assert.False(row.Streaming);
    }

    [Fact]
    public async Task A_request_refused_before_its_envelope_was_read_is_still_stored()
    {
        // The gap ADR 0008 left for this stage. A body that does not parse never reaches
        // CompletionExchange.Accept, which requires a valid model identifier, so no exchange exists
        // to store — and refusing to record the request would leave the operator unable to tell a
        // malformed client from one that never connected.
        using var store = new TemporaryMetadataStore();

        var row = await RefusedAsync(
            store,
            "{ not json at all",
            System.Net.HttpStatusCode.BadRequest);

        Assert.Equal((int)ExchangeStatus.Failed, row.Status);
        Assert.Equal(ErrorCodes.InvalidRequest, row.ErrorCode);

        // Neither a placeholder nor an empty string. Nothing about the request was interpretable, so
        // the model it wanted and whether it wanted a stream are both unknown, and both stay so.
        Assert.Null(row.ClientModelId);
        Assert.Null(row.Streaming);
        Assert.Null(row.RequestSummaryJson);
    }

    [Fact]
    public async Task Nothing_is_written_and_no_database_appears_when_persistence_is_disabled()
    {
        // FR-DATA-001: purely ephemeral operation is a supported deployment, not a degraded one.
        using var store = new TemporaryMetadataStore();

        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "qwen3.6-27b-mtp";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
            settings["agentsplice:persistence:mode"] = "None";
            settings["agentsplice:persistence:connectionString"] = store.ConnectionString;
        });

        await ProxyOnceAsync(fixture, Body(), Completion);

        Assert.False(
            File.Exists(store.ConnectionString.Replace("Data Source=", string.Empty, StringComparison.Ordinal)),
            "A deployment configured with persistence:mode None created a database file.");
    }

    /// <summary>Sends a request the gateway refuses, and returns the row it left behind.</summary>
    private static async Task<Infrastructure.Persistence.Rows.ExchangeRow> RefusedAsync(
        TemporaryMetadataStore store,
        string body,
        System.Net.HttpStatusCode expected)
    {
        await using var fixture = await StartAsync(store);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        Assert.Equal(expected, response.StatusCode);

        return await SingleExchangeAsync(store);
    }

    private static async Task<GatewayFixture> StartAsync(TemporaryMetadataStore store) =>
        await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "qwen3.6-27b-mtp";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
            store.ApplyTo(settings);
        });

    private static async Task ProxyAsync(
        TemporaryMetadataStore store,
        string? body = null,
        string? completion = null)
    {
        await using var fixture = await StartAsync(store);

        await ProxyOnceAsync(fixture, body ?? Body(), completion ?? Completion);
    }

    private static async Task ProxyOnceAsync(GatewayFixture fixture, string body, string completion)
    {
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(completion));

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        response.EnsureSuccessStatusCode();
    }

    private static string Body() =>
        """{"model":"qwen3.6-27b-mtp","messages":[{"role":"user","content":"hi"}]}""";

    private static async Task<Infrastructure.Persistence.Rows.ExchangeRow> SingleExchangeAsync(
        TemporaryMetadataStore store)
    {
        var rows = await WaitForAsync(
            store,
            async context => await context.Exchanges.ToListAsync(),
            found => found.Count > 0);

        return Assert.Single(rows);
    }

    /// <summary>Polls the store until a projection satisfies a condition, or the timeout elapses.</summary>
    private static async Task<TResult> WaitForAsync<TResult>(
        TemporaryMetadataStore store,
        Func<Infrastructure.Persistence.AgentSpliceDbContext, Task<TResult>> read,
        Func<TResult, bool> satisfied)
    {
        var deadline = DateTime.UtcNow + WriteTimeout;
        TResult result = default!;

        while (DateTime.UtcNow < deadline)
        {
            using (var context = store.OpenContext())
            {
                result = await read(context);
            }

            if (satisfied(result))
            {
                return result;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            FormattableString.Invariant($"The store did not reach the expected state within {WriteTimeout}."));

        return result;
    }
}
