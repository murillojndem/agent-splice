using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Primitives;

namespace AgentSplice.Api.Correlation;

/// <summary>
/// Accepts a client-supplied correlation token, or mints one (docs/API.md, FR-CHAT-009).
/// </summary>
/// <remarks>
/// A malformed <c>x-request-id</c> never fails the request. Rejecting an inference call because a
/// correlation header was oversized or carried a control character would turn a diagnostic
/// convenience into an outage, so the value is replaced and the request proceeds.
///
/// The offending value is never echoed anywhere — not into a response header, not into a log, not
/// into observation detail. Only the fact of the rejection is recorded, because the token is written
/// straight back into a response header and an unvalidated one is a header-injection vector.
/// </remarks>
public static class ClientRequestId
{
    /// <summary>How a request's correlation token came to be.</summary>
    public enum Origin
    {
        /// <summary>The client supplied a usable token.</summary>
        Client = 1,

        /// <summary>No token was supplied, so AgentSplice minted one.</summary>
        Generated = 2,

        /// <summary>A token was supplied but rejected, so AgentSplice minted one.</summary>
        Replaced = 3,
    }

    /// <summary>Resolves the correlation token for a request.</summary>
    public static (PublicRequestId RequestId, Origin Source) Resolve(StringValues supplied)
    {
        if (supplied.Count == 0)
        {
            return (PublicRequestId.New(), Origin.Generated);
        }

        // Only the first value is considered. Concatenating repeated headers would let a client
        // assemble a token longer than the documented bound out of individually valid parts.
        var candidate = supplied[0];

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return (PublicRequestId.New(), Origin.Generated);
        }

        return PublicRequestId.TryCreate(candidate, out var requestId)
            ? (requestId, Origin.Client)
            : (PublicRequestId.New(), Origin.Replaced);
    }
}
