using System.Buffers.Text;
using System.Globalization;
using System.Text;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Administration;

/// <summary>
/// A validated page request for <c>GET /api/v1/exchanges</c> (FR-TRACE-009).
/// </summary>
/// <remarks>
/// Every filter is validated rather than ignored. A status the vocabulary does not contain, or a
/// cursor that does not decode, is refused: silently dropping an unrecognised filter returns a page
/// that looks like an answer to the question the caller asked and is an answer to a different one.
/// </remarks>
public sealed record ExchangeQuery
{
    /// <summary>Largest page this surface will return, matching the OpenAPI parameter.</summary>
    public const int MaxLimit = 200;

    /// <summary>Page size when the caller names none.</summary>
    public const int DefaultLimit = 50;

    private ExchangeQuery()
    {
    }

    /// <summary>How many exchanges to return.</summary>
    public int Limit { get; private init; } = DefaultLimit;

    /// <summary>Only exchanges in this state, when the caller asked for one.</summary>
    public ExchangeStatus? Status { get; private init; }

    /// <summary>Only exchanges served by this runtime, when the caller asked for one.</summary>
    public RuntimeEndpointId? Runtime { get; private init; }

    /// <summary>Where to resume, or <c>null</c> for the first page.</summary>
    public ExchangeCursor? After { get; private init; }

    /// <summary>Validates raw query values, naming the first parameter that is unusable.</summary>
    public static bool TryCreate(
        string? cursor,
        int? limit,
        string? status,
        string? runtimeId,
        out ExchangeQuery query,
        out string? invalidParameter)
    {
        query = new ExchangeQuery();
        invalidParameter = null;

        if (limit is { } requested && (requested < 1 || requested > MaxLimit))
        {
            invalidParameter = "limit";
            return false;
        }

        ExchangeStatus? parsedStatus = null;

        if (!string.IsNullOrEmpty(status))
        {
            if (!TryParseStatus(status, out var value))
            {
                invalidParameter = "status";
                return false;
            }

            parsedStatus = value;
        }

        RuntimeEndpointId? runtime = null;

        if (!string.IsNullOrEmpty(runtimeId))
        {
            if (!RuntimeEndpointId.TryCreate(runtimeId, out var parsedRuntime))
            {
                invalidParameter = "runtimeId";
                return false;
            }

            runtime = parsedRuntime;
        }

        ExchangeCursor? after = null;

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!ExchangeCursor.TryParse(cursor, out var parsedCursor))
            {
                invalidParameter = "cursor";
                return false;
            }

            after = parsedCursor;
        }

        query = new ExchangeQuery
        {
            Limit = limit ?? DefaultLimit,
            Status = parsedStatus,
            Runtime = runtime,
            After = after,
        };

        return true;
    }

    /// <summary>Accepts the snake_case token the API publishes, and only that.</summary>
    private static bool TryParseStatus(string value, out ExchangeStatus status)
    {
        foreach (var candidate in Enum.GetValues<ExchangeStatus>())
        {
            if (string.Equals(WireName(candidate), value, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    /// <summary>The published token for a status, used by the filter and by the writer.</summary>
    public static string WireName(ExchangeStatus status)
    {
        var name = status.ToString();
        var builder = new StringBuilder(name.Length + 2);

        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsAsciiLetterUpper(name[index]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }
}

/// <summary>
/// The position a page resumes from.
/// </summary>
/// <remarks>
/// The full sort key, not an offset. Ordering is <c>(startedAt DESC, exchangeId DESC)</c>, and an
/// offset into that would skip or repeat rows whenever an exchange was written or expired between
/// two pages — which, on a gateway that is still serving traffic, is every page (FR-TRACE-009).
///
/// Opaque to the caller by construction, so the ordering can change without the cursor becoming a
/// contract. It carries no secret: base64 here is an encoding, not a protection.
/// </remarks>
public sealed record ExchangeCursor(long StartedAtTicks, Guid ExchangeId)
{
    /// <summary>Encodes the position.</summary>
    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{StartedAtTicks}:{ExchangeId:D}")));

    /// <summary>Decodes a caller-supplied cursor without trusting any part of it.</summary>
    public static bool TryParse(string value, out ExchangeCursor cursor)
    {
        cursor = default!;

        // Bounded before decoding: the caller chose this string, and a base64 blob is a cheap way to
        // ask a gateway to allocate.
        if (value.Length > 128 || !Base64.IsValid(value))
        {
            return false;
        }

        string decoded;

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0
            || !long.TryParse(decoded[..separator], CultureInfo.InvariantCulture, out var ticks)
            || !Guid.TryParseExact(decoded[(separator + 1)..], "D", out var exchangeId))
        {
            return false;
        }

        cursor = new ExchangeCursor(ticks, exchangeId);
        return true;
    }
}
