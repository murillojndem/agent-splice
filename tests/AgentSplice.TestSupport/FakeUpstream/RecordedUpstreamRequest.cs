using System.Collections.ObjectModel;
using System.Text;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// Everything the fake upstream saw for one request.
/// </summary>
/// <remarks>
/// Exposing the received request verbatim is what makes "forwarded exactly" a testable claim rather
/// than an assertion about AgentSplice's own view of what it sent (docs/TESTING.md, ROADMAP Stage 1A
/// exit criterion "forwarded fields are verified against the fake upstream").
/// </remarks>
public sealed class RecordedUpstreamRequest
{
    private readonly TaskCompletionSource abortSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RecordedUpstreamRequest(
        string method,
        string path,
        string? queryString,
        IReadOnlyDictionary<string, ReadOnlyCollection<string>> headers,
        ReadOnlyMemory<byte> body,
        DateTimeOffset receivedAt)
    {
        Method = method;
        Path = path;
        QueryString = queryString;
        Headers = headers;
        Body = body;
        ReceivedAt = receivedAt;
    }

    /// <summary>HTTP method.</summary>
    public string Method { get; }

    /// <summary>Request path, including the leading slash.</summary>
    public string Path { get; }

    /// <summary>Raw query string, including the leading '?', or <c>null</c> when absent.</summary>
    public string? QueryString { get; }

    /// <summary>All request headers as received.</summary>
    public IReadOnlyDictionary<string, ReadOnlyCollection<string>> Headers { get; }

    /// <summary>Raw request body bytes.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>When the request arrived.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>True once the client aborted this request before the response completed.</summary>
    public bool WasAborted { get; private set; }

    /// <summary>The request body decoded as UTF-8.</summary>
    public string BodyAsText() => Encoding.UTF8.GetString(Body.Span);

    /// <summary>The first value of a header, or <c>null</c> when the header was not sent.</summary>
    public string? Header(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// Waits until the client aborts this request.
    /// </summary>
    /// <remarks>
    /// Cancellation propagation cannot be proven from the client side alone: a client that stops
    /// reading looks identical to a client whose cancellation reached the runtime. This is the
    /// upstream-side evidence FR-CHAT-006 needs.
    /// </remarks>
    public async Task WaitForAbortAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(abortSignal.Task, Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != abortSignal.Task)
        {
            throw new TimeoutException(
                FormattableString.Invariant(
                    $"The upstream request {Method} {Path} was not aborted within {timeout}."));
        }
    }

    internal void MarkAborted()
    {
        WasAborted = true;
        abortSignal.TrySetResult();
    }
}
