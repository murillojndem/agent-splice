using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Administration;

/// <summary>
/// The read side of the exchange surface (FR-DASH-001, FR-TRACE-009).
/// </summary>
/// <remarks>
/// Returns a <see cref="GatewayResponse"/> rather than a view, so status selection and payload shape
/// are decided here and the endpoint stays three lines that read a request, call this, and write what
/// comes back. It is the same contract the completion endpoints use, and the same architecture test
/// enforces it.
/// </remarks>
public sealed class ExchangeQueryService
{
    private readonly IExchangeQueryStore store;
    private readonly IAdministrativeEnvelopeWriter writer;
    private readonly IErrorEnvelopeWriter errorWriter;

    /// <summary>Creates the service.</summary>
    public ExchangeQueryService(
        IExchangeQueryStore store,
        IAdministrativeEnvelopeWriter writer,
        IErrorEnvelopeWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(errorWriter);

        this.store = store;
        this.writer = writer;
        this.errorWriter = errorWriter;
    }

    /// <summary>Lists exchanges, newest first.</summary>
    public async Task<GatewayResponse> ListAsync(
        PublicRequestId requestId,
        string? cursor,
        int? limit,
        string? status,
        string? runtimeId,
        CancellationToken cancellationToken)
    {
        if (!store.Retains)
        {
            return Failure(GatewayErrorCatalogue.PersistenceDisabled, requestId);
        }

        if (!ExchangeQuery.TryCreate(cursor, limit, status, runtimeId, out var query, out var invalid))
        {
            // Named, never echoed. The parameter name is AgentSplice's own vocabulary; the value is
            // the caller's text and does not belong in an error envelope.
            return Failure(
                GatewayErrorCatalogue.InvalidQuery(
                    "The query parameter is not usable on this endpoint.",
                    invalid),
                requestId);
        }

        var page = await store.ListAsync(query, cancellationToken).ConfigureAwait(false);

        return GatewayResponse.Success(200, writer.MediaType, writer.Write(page), requestId);
    }

    /// <summary>Returns one exchange in full.</summary>
    public async Task<GatewayResponse> FindAsync(
        PublicRequestId requestId,
        string exchangeId,
        CancellationToken cancellationToken)
    {
        if (!store.Retains)
        {
            return Failure(GatewayErrorCatalogue.PersistenceDisabled, requestId);
        }

        if (!TryParseExchangeId(exchangeId, out var id))
        {
            return Failure(GatewayErrorCatalogue.ExchangeNotFound, requestId);
        }

        var detail = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return detail is null
            ? Failure(GatewayErrorCatalogue.ExchangeNotFound, requestId)
            : GatewayResponse.Success(200, writer.MediaType, writer.Write(detail), requestId);
    }

    /// <summary>Returns one exchange's timeline in sequence order.</summary>
    public async Task<GatewayResponse> FindObservationsAsync(
        PublicRequestId requestId,
        string exchangeId,
        CancellationToken cancellationToken)
    {
        if (!store.Retains)
        {
            return Failure(GatewayErrorCatalogue.PersistenceDisabled, requestId);
        }

        if (!TryParseExchangeId(exchangeId, out var id))
        {
            return Failure(GatewayErrorCatalogue.ExchangeNotFound, requestId);
        }

        var observations = await store.FindObservationsAsync(id, cancellationToken).ConfigureAwait(false);

        return observations is null
            ? Failure(GatewayErrorCatalogue.ExchangeNotFound, requestId)
            : GatewayResponse.Success(200, writer.MediaType, writer.Write(observations), requestId);
    }

    /// <summary>
    /// An unparsable identifier is the same answer as an absent one.
    /// </summary>
    /// <remarks>
    /// Deliberately not a 400. The two are indistinguishable to a caller who cannot see the store,
    /// and answering "that is not a valid identifier" tells anyone probing this surface which of
    /// their guesses were well-formed.
    /// </remarks>
    private static bool TryParseExchangeId(string value, out ExchangeId exchangeId)
    {
        exchangeId = default;

        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        exchangeId = ExchangeId.From(parsed);
        return true;
    }

    private GatewayResponse Failure(GatewayError error, PublicRequestId requestId) =>
        GatewayResponse.Failure(error, errorWriter.MediaType, errorWriter.Write(error), requestId);
}
