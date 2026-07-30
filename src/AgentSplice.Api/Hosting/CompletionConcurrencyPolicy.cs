using System.Threading.RateLimiting;
using AgentSplice.Api.Correlation;
using AgentSplice.Api.Endpoints;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Bounds how many completions the gateway serves at once (docs/SECURITY.md).
/// </summary>
/// <remarks>
/// Applied to completions alone. Model discovery and, later, health and diagnostics have to stay
/// answerable precisely when the gateway is saturated, because that is when an operator most needs
/// to ask what is happening.
///
/// Refusal rather than queueing. A queue turns an overload into unbounded latency, which for an
/// agent loop is worse than a clear no: the caller cannot tell a slow model from a full gateway and
/// has nothing to back off from. A 429 says which it is.
///
/// It rejects before the request body is read, so an overloaded gateway does not spend memory
/// materialising work it has already decided not to do.
/// </remarks>
internal static class CompletionConcurrencyPolicy
{
    /// <summary>The policy name endpoints attach to.</summary>
    internal const string Name = "agentsplice.completions";

    private const string PartitionKey = "completions";

    internal static IServiceCollection AddCompletionConcurrencyLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(Name, static context => RateLimitPartition.GetConcurrencyLimiter(
                PartitionKey,
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = context.RequestServices
                        .GetRequiredService<IOptions<AgentSpliceOptions>>()
                        .Value.Limits.MaxConcurrentCompletions,

                    // Nothing waits. The permit is either free or the request is refused.
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));

            limiter.OnRejected = RejectAsync;
        });

        return services;
    }

    /// <summary>Answers a refused request with the same envelope every other failure uses.</summary>
    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var errorWriter = http.RequestServices.GetRequiredService<IErrorEnvelopeWriter>();

        var (requestId, _) = ClientRequestId.Resolve(http.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        // A client that is being asked to slow down needs to be told for how long, or the advice is
        // not actionable and it will simply retry immediately.
        http.Response.Headers.RetryAfter = "1";

        await GatewayResponseWriter
            .WriteErrorAsync(
                http,
                GatewayErrorCatalogue.For(FailureClass.GatewayOverloaded),
                errorWriter,
                requestId,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
