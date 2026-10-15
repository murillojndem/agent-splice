using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AgentSplice.HealthProbe;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.IntegrationTests.HealthProbe;

/// <summary>
/// The image's health probe reports liveness as Docker reads it: 0 or 1, and one line saying why
/// (ADR 0016, FR-HEALTH-001).
/// </summary>
/// <remarks>
/// Against a real loopback listener rather than a stubbed handler wherever the property is about the
/// transport — a refused connection, a redirect, a stall — because those are what the probe meets
/// inside a container. Its own budget runs on an injected clock, so the stall is expired by
/// advancing time rather than by waiting it out. And once against the gateway itself, because the
/// probe's definition of "live" is only right if the gateway's liveness answer satisfies it.
/// </remarks>
public sealed class LivenessProbeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_2xx_answer_is_healthy()
    {
        await using var server = await FakeUpstreamServer.StartAsync();
        server.SetDefault(UpstreamResponseScripts.Status((int)HttpStatusCode.NoContent));

        var target = new Uri(server.BaseAddress, "health/live");
        var (exitCode, line) = await ProbeWithTransportAsync(target.AbsoluteUri);

        Assert.Equal(LivenessProbe.Healthy, exitCode);
        Assert.StartsWith(Invariant($"ok: GET {target} answered 204 in "), line, StringComparison.Ordinal);

        var request = Assert.Single(server.ReceivedRequests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/health/live", request.Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task A_non_2xx_answer_is_unhealthy_and_names_the_status(HttpStatusCode status)
    {
        await using var server = await FakeUpstreamServer.StartAsync();
        server.SetDefault(UpstreamResponseScripts.Status((int)status));

        var target = new Uri(server.BaseAddress, "health/live");
        var (exitCode, line) = await ProbeWithTransportAsync(target.AbsoluteUri);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.StartsWith(Invariant($"unhealthy: GET {target} answered {(int)status} in "), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_is_reported_rather_than_followed()
    {
        // A redirect would make the answer come from somewhere other than the endpoint named in the
        // image. The probe reports the 302 it was given and asks nothing else.
        await using var server = await FakeUpstreamServer.StartAsync();
        server.SetDefault(UpstreamResponseScripts.Status((int)HttpStatusCode.Found) with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["location"] = new Uri(server.BaseAddress, "elsewhere").AbsoluteUri,
            }.AsReadOnly(),
        });

        var (exitCode, line) = await ProbeWithTransportAsync(new Uri(server.BaseAddress, "health/live").AbsoluteUri);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Contains(" answered 302 in ", line, StringComparison.Ordinal);
        Assert.Single(server.ReceivedRequests);
    }

    [Fact]
    public async Task No_answer_within_the_probe_timeout_is_unhealthy()
    {
        // The listener accepts the request and sends nothing for far longer than the bound. The probe
        // must give up on its own budget — measured by the clock it was given — and say so, rather
        // than leave Docker to kill it with nothing reported.
        await using var server = await FakeUpstreamServer.StartAsync();
        server.SetDefault(UpstreamResponseScripts.StallBeforeHeaders(Bound * 6));

        var clock = new FakeTimeProvider();
        var target = new Uri(server.BaseAddress, "health/live").AbsoluteUri;

        using var handler = LivenessProbe.CreateHandler();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        // Waiting is registered before the probe starts: the server hands a request only to a waiter
        // already present when it arrives.
        var arrived = server.WaitForRequestAsync(Bound);
        var probe = LivenessProbe.RunAsync([target], output, clock, handler);

        await arrived;
        Assert.False(probe.IsCompleted, "The probe answered before its budget expired.");

        clock.Advance(LivenessProbe.RequestTimeout);

        var exitCode = await probe.WaitAsync(Bound);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Equal(
            Invariant($"unhealthy: GET {target} had no answer within {(long)LivenessProbe.RequestTimeout.TotalMilliseconds} ms"),
            SingleLine(output));
    }

    [Fact]
    public async Task A_refused_connection_is_unhealthy()
    {
        var target = Invariant($"http://127.0.0.1:{UnusedLoopbackPort()}/health/live");

        var (exitCode, line) = await ProbeWithTransportAsync(target);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Equal(Invariant($"unhealthy: GET {target} failed: ConnectionRefused"), line);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5280/health/live")]
    [InlineData("http://localhost:5280/health/live")]
    [InlineData("http://10.0.0.1:5280/health/live")]
    [InlineData("http://example.com/health/live")]
    [InlineData("http://user:secret@127.0.0.1:5280/health/live")]
    [InlineData("ftp://127.0.0.1/health/live")]
    [InlineData("/health/live")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_target_off_loopback_http_is_refused_without_a_request(string target)
    {
        using var handler = new RecordingHandler();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await LivenessProbe.RunAsync([target], output, TimeProvider.System, handler);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Equal(0, handler.Requests);

        // The refusal never echoes what it refused, so a credential in a mistyped URL does not end
        // up in docker inspect.
        var line = SingleLine(output);
        Assert.StartsWith("unhealthy: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Anything_but_exactly_one_argument_is_refused_without_a_request(int count)
    {
        using var handler = new RecordingHandler();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var args = Enumerable.Repeat("http://127.0.0.1:5280/health/live", count).ToArray();
        var exitCode = await LivenessProbe.RunAsync(args, output, TimeProvider.System, handler);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Equal(0, handler.Requests);
        Assert.StartsWith("unhealthy: ", SingleLine(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5280/health/live")]
    [InlineData("http://127.0.0.2/health/live")]
    [InlineData("http://[::1]:5280/health/live")]
    public void A_loopback_http_target_is_accepted(string target)
    {
        Assert.True(LivenessProbe.TryParseTarget(target, out var parsed));
        Assert.Equal(new Uri(target), parsed);
    }

    [Fact]
    public async Task An_unexpected_fault_is_still_an_unhealthy_answer()
    {
        // An unhandled exception would exit with a code Docker does not define. Whatever goes wrong,
        // the probe answers 1 and names the failure class.
        using var handler = new RecordingHandler(new InvalidOperationException("boom"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await LivenessProbe.RunAsync(
            ["http://127.0.0.1:5280/health/live"],
            output,
            TimeProvider.System,
            handler);

        Assert.Equal(LivenessProbe.Unhealthy, exitCode);
        Assert.Equal(
            "unhealthy: GET http://127.0.0.1:5280/health/live failed: InvalidOperationException",
            SingleLine(output));
    }

    [Fact]
    public async Task The_probe_accepts_the_gateways_own_liveness_answer()
    {
        // The pairing that matters. The gateway answers liveness with 204 and answers HEAD with 405,
        // so a probe that demanded exactly 200, or asked with HEAD, would report a healthy gateway as
        // unhealthy — and every check here against a scripted listener would still pass.
        using var factory = new AgentSpliceApplicationFactory();
        using var handler = factory.Server.CreateHandler();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await LivenessProbe.RunAsync(
            ["http://127.0.0.1/health/live"],
            output,
            TimeProvider.System,
            handler);

        Assert.Equal(LivenessProbe.Healthy, exitCode);
        Assert.StartsWith("ok: GET http://127.0.0.1/health/live answered 204 in ", SingleLine(output), StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Line)> ProbeWithTransportAsync(string target)
    {
        using var handler = LivenessProbe.CreateHandler();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await LivenessProbe.RunAsync([target], output, TimeProvider.System, handler).WaitAsync(Bound);

        return (exitCode, SingleLine(output));
    }

    /// <summary>Exactly one line was written, terminated; returns it.</summary>
    private static string SingleLine(StringWriter output)
    {
        var text = output.ToString();

        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);

        var line = text[..^Environment.NewLine.Length];
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);

        return line;
    }

    /// <summary>A loopback port nothing listens on: bound by the operating system, then released.</summary>
    private static int UnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>Counts requests, and either throws a given fault or answers 204.</summary>
    private sealed class RecordingHandler(Exception? fault = null) : HttpMessageHandler
    {
        private int requests;

        internal int Requests => Volatile.Read(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);

            return fault is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
                : Task.FromException<HttpResponseMessage>(fault);
        }
    }
}
