using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// Convenience factories for the non-streaming <see cref="UpstreamResponseScript"/> shapes tests
/// need most often.
/// </summary>
public static class UpstreamResponseScripts
{
    /// <summary>A JSON response.</summary>
    public static UpstreamResponseScript Json(
        string json,
        int statusCode = (int)HttpStatusCode.OK,
        TimeSpan? headerDelay = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        return new UpstreamResponseScript
        {
            StatusCode = statusCode,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(json),
            HeaderDelay = headerDelay ?? TimeSpan.Zero,
        };
    }

    /// <summary>A response with an explicit content type and raw text body.</summary>
    public static UpstreamResponseScript Text(
        string body,
        string contentType = "text/plain",
        int statusCode = (int)HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);

        return new UpstreamResponseScript
        {
            StatusCode = statusCode,
            ContentType = contentType,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    /// <summary>A status-only response with no body.</summary>
    public static UpstreamResponseScript Status(int statusCode) => new()
    {
        StatusCode = statusCode,
        ContentType = null,
        Body = null,
        Chunks = ReadOnlyCollection<UpstreamChunk>.Empty,
    };

    /// <summary>
    /// Accepts the request and never sends headers, so the client's response-header timeout fires.
    /// </summary>
    public static UpstreamResponseScript StallBeforeHeaders(TimeSpan duration) => new()
    {
        StatusCode = (int)HttpStatusCode.OK,
        ContentType = "application/json",
        HeaderDelay = duration,
        Body = Encoding.UTF8.GetBytes("{}"),
    };

    /// <summary>
    /// Sends headers, then stalls without a body, so the client's idle-stream timeout fires.
    /// </summary>
    public static UpstreamResponseScript StallAfterHeaders(TimeSpan duration) => new()
    {
        StatusCode = (int)HttpStatusCode.OK,
        ContentType = "text/event-stream",
        TrailingDelay = duration,
    };

    /// <summary>
    /// Returns a body that is not valid JSON, for the malformed-upstream-response fixture family.
    /// </summary>
    public static UpstreamResponseScript MalformedJson(int statusCode = (int)HttpStatusCode.OK) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json",
        Body = Encoding.UTF8.GetBytes("{\"id\": \"chatcmpl-1\", \"choices\": [ {\"index\": 0, "),
    };

    /// <summary>
    /// An event stream that carries a status other than 200, for the "a runtime can refuse a
    /// streaming request in its own words" fixtures.
    /// </summary>
    public static UpstreamResponseScript EventStreamStatus(int statusCode, string body = "") => new()
    {
        StatusCode = statusCode,
        ContentType = "text/event-stream",
        Body = Encoding.UTF8.GetBytes(body),
    };

    /// <summary>Writes part of a body and then resets the connection, producing a premature EOF.</summary>
    public static UpstreamResponseScript TruncatedJson() => new()
    {
        StatusCode = (int)HttpStatusCode.OK,
        ContentType = "application/json",
        Body = Encoding.UTF8.GetBytes("{\"id\": \"chatcmpl-1\""),
        ClosePrematurely = true,
    };

    /// <summary>
    /// Promises more bytes than it delivers, then ends the response.
    /// </summary>
    /// <remarks>
    /// The other way a body ends early, and the reason it exists separately from
    /// <see cref="TruncatedJson"/>: a reset connection and a declared length the sender never
    /// honours reach the client as different exceptions, and a gateway that classified them
    /// differently would report the same runtime fault two ways depending on timing.
    /// </remarks>
    public static UpstreamResponseScript UnderDeliveredContentLength() => new()
    {
        StatusCode = (int)HttpStatusCode.OK,
        ContentType = "application/json",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = "4096",
        }.AsReadOnly(),
        Body = Encoding.UTF8.GetBytes("{\"id\": \"chatcmpl-1\""),
    };
}
