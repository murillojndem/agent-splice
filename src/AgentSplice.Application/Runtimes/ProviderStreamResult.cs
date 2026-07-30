using AgentSplice.Domain.Exchanges;

namespace AgentSplice.Application.Runtimes;

/// <summary>
/// An opened upstream response, or why it could not be opened.
/// </summary>
/// <remarks>
/// A non-2xx answer is a success here, exactly as it is on the buffered path: the body is returned
/// and relayed verbatim with the runtime's own status and content type, so there is one relay rather
/// than a special case per status. Only a transport failure produces a <see cref="Failure"/>, and
/// only an authentication rejection has its body withheld, because that body can hint at the shape
/// of a key the client never supplied.
///
/// The caller owns <see cref="Body"/> and must dispose it. Until then the connection, the response,
/// and the runtime's timeout budgets are all still open.
/// </remarks>
public sealed record ProviderStreamResult
{
    private ProviderStreamResult()
    {
    }

    /// <summary>What the response headers said, or <c>null</c> when none arrived.</summary>
    public UpstreamResponseMetadata? Response { get; private init; }

    /// <summary>The open body, or <c>null</c> when the runtime never answered.</summary>
    public IUpstreamResponseBody? Body { get; private init; }

    /// <summary>Why the runtime could not be asked, or <c>null</c> when it answered.</summary>
    public UpstreamFailure? Failure { get; private init; }

    /// <summary>Headers from the response allowlist, to be relayed to the client.</summary>
    public IReadOnlyDictionary<string, string> RelayedHeaders { get; private init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What establishing a connection cost, or <c>null</c> when a pooled one was reused.</summary>
    public UpstreamConnectObservation? Connection { get; private init; }

    /// <summary>Attaches connection timing to an opened response.</summary>
    public ProviderStreamResult WithConnection(UpstreamConnectObservation? connection) =>
        connection is null ? this : this with { Connection = connection };

    /// <summary>True when the runtime answered, whatever status it chose.</summary>
    public bool Opened => Body is not null && Response is not null && Failure is null;

    /// <summary>Records an opened response, whatever its status.</summary>
    public static ProviderStreamResult FromResponse(
        UpstreamResponseMetadata response,
        IUpstreamResponseBody body,
        IReadOnlyDictionary<string, string>? relayedHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(body);

        return new ProviderStreamResult
        {
            Response = response,
            Body = body,
            RelayedHeaders = relayedHeaders
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Records that the response could not be opened.</summary>
    public static ProviderStreamResult Failed(
        UpstreamFailure failure,
        UpstreamResponseMetadata? response = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new ProviderStreamResult { Failure = failure, Response = response };
    }
}
