using System.Net;
using System.Text;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.FakeUpstream;

/// <summary>
/// Verifies the fake upstream itself.
/// </summary>
/// <remarks>
/// Every Stage 1 streaming, cancellation, and timeout test will trust this fixture. If the fixture
/// silently buffered, reordered, or completed cleanly where it was told to reset, those tests would
/// pass while the gateway was broken, so the fixture is tested first (docs/TESTING.md).
/// </remarks>
public sealed class FakeUpstreamServerTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_unscripted_request_is_refused_rather_than_answered_with_a_misleading_success()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        using var client = upstream.CreateClient();

        using var response = await client.GetAsync(new Uri("v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_request_is_recorded_exactly_as_received()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json("{\"ok\":true}"));

        using var client = upstream.CreateClient();
        using var content = new StringContent(
            "{\"model\":\"qwen3.6-27b-mtp\",\"messages\":[],\"reasoning_effort\":\"high\"}",
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/chat/completions?trace=1", UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.Add("x-request-id", "req-42");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recorded = Assert.Single(upstream.ReceivedRequests);

        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v1/chat/completions", recorded.Path);
        Assert.Equal("?trace=1", recorded.QueryString);
        Assert.Equal("req-42", recorded.Header("x-request-id"));
        Assert.Contains("reasoning_effort", recorded.BodyAsText(), StringComparison.Ordinal);
        Assert.False(recorded.WasAborted);
    }

    [Fact]
    public async Task A_path_specific_script_takes_precedence_over_the_shared_queue()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.Enqueue(UpstreamResponseScripts.Json("{\"source\":\"shared\"}"));
        upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json("{\"source\":\"path\"}"));

        using var client = upstream.CreateClient();

        Assert.Contains("path", await client.GetStringAsync(new Uri("v1/models", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains("shared", await client.GetStringAsync(new Uri("v1/other", UriKind.Relative)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_are_served_in_the_order_they_were_queued()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.Enqueue(UpstreamResponseScripts.Json("{\"n\":1}"));
        upstream.Enqueue(UpstreamResponseScripts.Json("{\"n\":2}"));

        using var client = upstream.CreateClient();

        Assert.Contains("1", await client.GetStringAsync(new Uri("v1/a", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains("2", await client.GetStringAsync(new Uri("v1/b", UriKind.Relative)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_only_script_returns_the_scripted_status()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(UpstreamResponseScripts.Status((int)HttpStatusCode.Unauthorized));

        using var client = upstream.CreateClient();
        using var response = await client.GetAsync(new Uri("v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_event_stream_arrives_byte_for_byte_identical_to_the_script()
    {
        var script = SseScript.Create()
            .Data("{\"delta\":\"Hel\"}")
            .Data("{\"delta\":\"lo\"}")
            .Done();

        var expected = Encoding.UTF8.GetString(script.ToBytes());

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script.Build());

        using var client = upstream.CreateClient();

        Assert.Equal(expected, await client.GetStringAsync(new Uri("v1/chat/completions", UriKind.Relative)));
    }

    [Fact]
    public async Task An_event_stream_split_one_byte_at_a_time_still_reassembles_intact()
    {
        // FR-STR-004: events must survive arbitrary network boundaries, including a split inside a
        // multi-byte UTF-8 sequence. The accented characters are the point of the fixture.
        var script = SseScript.Create()
            .Data("{\"delta\":\"café-naïve-\\u00e9\"}")
            .Done();

        var expected = Encoding.UTF8.GetString(script.ToBytes());

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script.SplitByteByByte().Build());

        using var client = upstream.CreateClient();

        Assert.Equal(expected, await client.GetStringAsync(new Uri("v1/chat/completions", UriKind.Relative)));
    }

    [Fact]
    public async Task Multiline_data_comments_and_crlf_endings_are_transmitted_verbatim()
    {
        var script = SseScript.Create()
            .UseCrLf()
            .Comment("keepalive")
            .MultilineData(["first", "second", "third"])
            .Retry(TimeSpan.FromSeconds(3))
            .NamedEvent("usage", "{\"prompt_tokens\":10}")
            .Done();

        var expected = Encoding.UTF8.GetString(script.ToBytes());

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script.Build());

        using var client = upstream.CreateClient();
        var received = await client.GetStringAsync(new Uri("v1/chat/completions", UriKind.Relative));

        Assert.Equal(expected, received);
        Assert.Contains("\r\n\r\n", received, StringComparison.Ordinal);
        Assert.Contains(": keepalive", received, StringComparison.Ordinal);
        Assert.Contains("data: first\r\ndata: second\r\ndata: third\r\n\r\n", received, StringComparison.Ordinal);
        Assert.Contains("retry: 3000", received, StringComparison.Ordinal);
        Assert.Contains("event: usage", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_headers_can_be_delayed_so_a_header_timeout_can_be_provoked()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(30)));

        using var client = upstream.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(new Uri("v1/chat/completions", UriKind.Relative), timeout.Token));
    }

    [Fact]
    public async Task Headers_can_arrive_before_a_stall_so_an_idle_stream_timeout_can_be_provoked()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(30)));

        using var client = upstream.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync(
            new Uri("v1/chat/completions", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[16];

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(buffer, readTimeout.Token).AsTask());
    }

    [Fact]
    public async Task A_premature_close_surfaces_to_the_client_as_a_broken_stream()
    {
        var script = SseScript.Create()
            .Data("{\"delta\":\"partial\"}")
            .ClosePrematurely();

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script.Build());

        using var client = upstream.CreateClient();

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri("v1/chat/completions", UriKind.Relative)));
    }

    [Fact]
    public async Task Malformed_json_is_delivered_unrepaired()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(UpstreamResponseScripts.MalformedJson());

        using var client = upstream.CreateClient();
        var body = await client.GetStringAsync(new Uri("v1/chat/completions", UriKind.Relative));

        Assert.EndsWith("\"index\": 0, ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_cancellation_is_observed_by_the_upstream()
    {
        // FR-CHAT-006 cannot be proven from the client side: a client that stops reading looks the
        // same as one whose cancellation reached the runtime. This asserts the upstream side.
        var script = SseScript.Create()
            .Data("{\"delta\":\"one\"}")
            .Data("{\"delta\":\"two\"}", TimeSpan.FromSeconds(30))
            .Done()
            .Build();

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script);

        using var client = upstream.CreateClient();
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetAsync(
            new Uri("v1/chat/completions", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);

        var recorded = await upstream.WaitForRequestAsync(WaitBudget);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var response = await pending;
            await response.Content.ReadAsStringAsync(cancellation.Token);
        });

        await recorded.WaitForAbortAsync(WaitBudget);

        Assert.True(recorded.WasAborted);
    }

    [Fact]
    public async Task Events_are_flushed_as_they_are_written_rather_than_buffered_until_completion()
    {
        // NFR-PERF-002 and FR-STR-003 are only measurable if the fixture flushes. A buffering fixture
        // would make every "first client event" measurement meaningless.
        var script = SseScript.Create()
            .Data("{\"delta\":\"first\"}")
            .Data("{\"delta\":\"second\"}", TimeSpan.FromSeconds(20))
            .Build();

        await using var upstream = await FakeUpstreamServer.StartAsync();
        upstream.SetDefault(script);

        using var client = upstream.CreateClient();
        using var timeout = new CancellationTokenSource(WaitBudget);

        using var response = await client.GetAsync(
            new Uri("v1/chat/completions", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[256];

        var read = await stream.ReadAsync(buffer, timeout.Token);

        Assert.Contains(
            "first",
            Encoding.UTF8.GetString(buffer, 0, read),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForRequestAsync_fails_loudly_when_no_request_arrives()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();

        await Assert.ThrowsAsync<TimeoutException>(
            () => upstream.WaitForRequestAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task The_openai_base_address_is_derived_from_the_bound_port()
    {
        await using var upstream = await FakeUpstreamServer.StartAsync();

        Assert.Equal("127.0.0.1", upstream.BaseAddress.Host);
        Assert.EndsWith("/v1/", upstream.OpenAiBaseAddress.AbsoluteUri, StringComparison.Ordinal);
        Assert.NotEqual(0, upstream.BaseAddress.Port);
    }
}
