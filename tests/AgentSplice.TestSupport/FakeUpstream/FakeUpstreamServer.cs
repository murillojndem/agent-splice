using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// A real HTTP server that answers with scripted responses and records exactly what it received.
/// </summary>
/// <remarks>
/// This is a real Kestrel listener rather than an in-memory test handler. Streaming preservation,
/// cancellation propagation, and timeout phases are properties of the transport: an in-memory
/// handler cannot demonstrate that a client disconnect reaches the runtime, or that events are
/// flushed rather than buffered (docs/TESTING.md, docs/SPECIFICATION.md FR-CHAT-006, FR-STR-003).
///
/// Resolution order for a request is: a script queued for that exact path, then a script on the
/// shared queue, then the default. The out-of-the-box default is 404, so a test that forgot to
/// script a call fails loudly instead of receiving an accidental 200.
/// </remarks>
public sealed class FakeUpstreamServer : IAsyncDisposable
{
    private const string UnscriptedResponseBody =
        "{\"error\":{\"message\":\"No fake upstream response was scripted for this request.\",\"type\":\"fake_upstream_unscripted\"}}";

    private readonly WebApplication application;
    private readonly ConcurrentQueue<UpstreamResponseScript> sharedQueue = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<UpstreamResponseScript>> pathQueues =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<RecordedUpstreamRequest> received = [];
    private readonly ConcurrentQueue<TaskCompletionSource<RecordedUpstreamRequest>> waiters = new();
    private readonly object receivedGate = new();
    private readonly TimeProvider timeProvider;

    private UpstreamResponseScript defaultScript = UpstreamResponseScripts.Json(
        UnscriptedResponseBody,
        (int)HttpStatusCode.NotFound);

    private FakeUpstreamServer(WebApplication application, TimeProvider timeProvider)
    {
        this.application = application;
        this.timeProvider = timeProvider;
    }

    /// <summary>The loopback base address the server is listening on, with a trailing slash.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The OpenAI-compatible base address, <c>{BaseAddress}v1/</c>.</summary>
    public Uri OpenAiBaseAddress => new(BaseAddress, "v1/");

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedUpstreamRequest> ReceivedRequests
    {
        get
        {
            lock (receivedGate)
            {
                return received.ToArray();
            }
        }
    }

    /// <summary>Starts a server on an ephemeral loopback port.</summary>
    public static async Task<FakeUpstreamServer> StartAsync(
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Kestrel must not impose its own limits on the fixture: a test that scripts a long stall or
        // a large payload is testing AgentSplice's timeout and buffering behaviour, not Kestrel's.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MinResponseDataRate = null;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
        });

        var application = builder.Build();
        var server = new FakeUpstreamServer(application, timeProvider ?? TimeProvider.System);

        application.Run(server.HandleAsync);

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The fake upstream server did not report a bound address.");

        server.BaseAddress = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        return server;
    }

    /// <summary>Creates a client pointed at this server.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

    /// <summary>Queues a response for the next request to any path.</summary>
    public FakeUpstreamServer Enqueue(UpstreamResponseScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        sharedQueue.Enqueue(script);
        return this;
    }

    /// <summary>Queues a response for the next request to a specific path.</summary>
    public FakeUpstreamServer EnqueueFor(string path, UpstreamResponseScript script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(script);

        pathQueues.GetOrAdd(Normalise(path), static _ => new ConcurrentQueue<UpstreamResponseScript>())
            .Enqueue(script);

        return this;
    }

    /// <summary>Sets the response used when no queued script applies.</summary>
    public FakeUpstreamServer SetDefault(UpstreamResponseScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        defaultScript = script;
        return this;
    }

    /// <summary>Waits for the next request the server has not yet handed to a waiter.</summary>
    public async Task<RecordedUpstreamRequest> WaitForRequestAsync(TimeSpan timeout)
    {
        var waiter = new TaskCompletionSource<RecordedUpstreamRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        waiters.Enqueue(waiter);

        var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != waiter.Task)
        {
            throw new TimeoutException(
                FormattableString.Invariant($"No upstream request arrived within {timeout}."));
        }

        return await waiter.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await application.StopAsync().ConfigureAwait(false);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    private static string Normalise(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    private async Task HandleAsync(HttpContext context)
    {
        var recorded = await RecordAsync(context).ConfigureAwait(false);
        var script = Resolve(context.Request.Path.Value ?? "/");

        // A single registration covers the whole response: an abort during a delay, during a write,
        // or after the last write all have to be observable as the same fact.
        await using var abortRegistration = context.RequestAborted
            .Register(recorded.MarkAborted)
            .ConfigureAwait(false);

        try
        {
            await WriteAsync(context, script).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            recorded.MarkAborted();
        }
        catch (IOException)
        {
            // The client went away mid-write. Recorded as an abort rather than surfaced, because the
            // fixture's job is to report what happened, not to fail the test itself.
            recorded.MarkAborted();
        }
    }

    private async Task<RecordedUpstreamRequest> RecordAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);

        var headers = new Dictionary<string, ReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in context.Request.Headers)
        {
            var values = new List<string>(header.Value.Count);

            foreach (var value in header.Value)
            {
                values.Add(value ?? string.Empty);
            }

            headers[header.Key] = values.AsReadOnly();
        }

        var recorded = new RecordedUpstreamRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            headers,
            buffer.ToArray(),
            timeProvider.GetUtcNow());

        lock (receivedGate)
        {
            received.Add(recorded);
        }

        while (waiters.TryDequeue(out var waiter))
        {
            if (waiter.TrySetResult(recorded))
            {
                break;
            }
        }

        return recorded;
    }

    private UpstreamResponseScript Resolve(string path)
    {
        if (pathQueues.TryGetValue(Normalise(path), out var queue) && queue.TryDequeue(out var pathScript))
        {
            return pathScript;
        }

        return sharedQueue.TryDequeue(out var sharedScript) ? sharedScript : defaultScript;
    }

    private static async Task WriteAsync(HttpContext context, UpstreamResponseScript script)
    {
        var cancellationToken = context.RequestAborted;

        if (script.HeaderDelay > TimeSpan.Zero)
        {
            await Task.Delay(script.HeaderDelay, cancellationToken).ConfigureAwait(false);
        }

        context.Response.StatusCode = script.StatusCode;

        if (script.ContentType is not null)
        {
            context.Response.ContentType = script.ContentType;
        }

        foreach (var (name, value) in script.Headers)
        {
            context.Response.Headers[name] = value;
        }

        // Without this, Kestrel may hold small writes back and every "was the event flushed
        // promptly" assertion would be measuring the fixture instead of the gateway.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        // StartAsync commits the headers but does not guarantee they reach the socket. A script that
        // sends headers and then stalls exists precisely to let a client's response-header phase
        // succeed and its idle-stream phase fire, so the headers must be on the wire now.
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (script.Body is { } body)
        {
            await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in script.Chunks)
        {
            if (chunk.Delay > TimeSpan.Zero)
            {
                await Task.Delay(chunk.Delay, cancellationToken).ConfigureAwait(false);
            }

            await context.Response.Body.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (script.TrailingDelay > TimeSpan.Zero)
        {
            await Task.Delay(script.TrailingDelay, cancellationToken).ConfigureAwait(false);
        }

        if (script.ClosePrematurely)
        {
            context.Abort();
        }
    }
}
