using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace AgentSplice.Application.Models;

/// <summary>
/// Serves <c>GET /v1/models</c> (docs/SPECIFICATION.md FR-MOD-001).
/// </summary>
/// <remarks>
/// The status rules are the substance here, because an empty catalogue has two very different
/// causes and reporting the wrong one sends a user looking in the wrong place.
///
/// Nothing configured, or every runtime deliberately switched off, is an operator fact: the honest
/// answer is an empty list with 200, and blaming the upstream for it would be misleading evidence.
/// Every consulted runtime failing with nothing left to serve is a genuine upstream outage, and that
/// is a 502.
/// </remarks>
public sealed class ModelListGateway
{
    private static readonly GatewayError DiscoveryUnavailable = GatewayError.Create(
        ErrorCodes.RuntimeUnavailable,
        ErrorTypes.UpstreamUnavailable,
        502,
        "No configured runtime could be reached to list models.",
        failureClass: FailureClass.RuntimeUnavailable);

    private static readonly GatewayError Internal = GatewayError.Create(
        ErrorCodes.InternalError,
        ErrorTypes.Internal,
        500,
        "The gateway failed to build the model list.",
        failureClass: FailureClass.InternalError);

    private readonly ModelCatalogueService catalogue;
    private readonly IModelListWriter writer;
    private readonly IErrorEnvelopeWriter errorWriter;
    private readonly ILogger<ModelListGateway> logger;

    /// <summary>Creates the gateway.</summary>
    public ModelListGateway(
        ModelCatalogueService catalogue,
        IModelListWriter writer,
        IErrorEnvelopeWriter errorWriter,
        ILogger<ModelListGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(logger);

        this.catalogue = catalogue;
        this.writer = writer;
        this.errorWriter = errorWriter;
        this.logger = logger;
    }

    /// <summary>Builds the model list response.</summary>
    public async Task<GatewayResponse> ListAsync(PublicRequestId requestId, CancellationToken cancellationToken)
    {
        try
        {
            var composed = await catalogue.ComposeAsync(cancellationToken).ConfigureAwait(false);

            foreach (var outcome in composed.Outcomes)
            {
                if (outcome.Failure is not null || !outcome.YieldedCatalogue)
                {
                    logger.LogWarning(
                        "Model discovery for runtime {RuntimeId} reported {RuntimeHealth}. Served from stale cache: {ServedStale}.",
                        outcome.Runtime.Value,
                        outcome.Status,
                        outcome.ServedFromStaleCache);
                }
            }

            if (composed.Entries.Count == 0 && composed.EveryDiscoveryAttemptFailed)
            {
                return Failure(DiscoveryUnavailable, requestId);
            }

            return GatewayResponse.Success(
                200,
                writer.MediaType,
                writer.Write(composed.Entries),
                requestId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is never surfaced; only the fact and the correlation token are.
            logger.LogError(exception, "Building the model list failed for request {RequestId}.", requestId.Value);
            return Failure(Internal, requestId);
        }
    }

    private GatewayResponse Failure(GatewayError error, PublicRequestId requestId) =>
        GatewayResponse.Failure(error, errorWriter.MediaType, errorWriter.Write(error), requestId);
}
