using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace AgentSplice.ArchitectureTests;

/// <summary>
/// The enforceable form of "no domain or orchestration logic in endpoint lambdas" (CLAUDE.md).
/// </summary>
/// <remarks>
/// Reviewing this by eye works until the first time parsing one field inside a handler is the
/// shortest path to a fix. After that the rule only holds if the build enforces it, because logic
/// that lives in an endpoint is the one kind that cannot be unit tested.
/// </remarks>
public sealed class EndpointBoundaryTests
{
    private static readonly Assembly Api = typeof(Program).Assembly;

    [Fact]
    public void The_api_does_not_parse_or_serialise_protocol_json()
    {
        // Protocol shape belongs to a protocol module. An endpoint that reaches for a JSON writer is
        // deciding what the wire looks like.
        AssertApiDoesNotDependOn("System.Text.Json");
    }

    [Fact]
    public void The_api_does_not_record_exchange_evidence()
    {
        AssertApiDoesNotDependOn(
            "AgentSplice.Domain.Observations.ExchangeTimeline",
            "AgentSplice.Domain.Observations.ExchangeObservation",
            "AgentSplice.Domain.Exchanges.CompletionExchange");
    }

    [Fact]
    public void The_api_does_not_reach_past_the_application_into_a_provider()
    {
        AssertApiDoesNotDependOn("AgentSplice.Providers.LmStudio.LmStudioModelRuntimeProvider");
    }

    [Fact]
    public void The_api_does_not_open_its_own_upstream_connections()
    {
        AssertApiDoesNotDependOn("System.Net.Http.HttpClient", "System.Net.Sockets");
    }

    private static void AssertApiDoesNotDependOn(params string[] forbidden)
    {
        var result = Types.InAssembly(Api)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Violating types: " + string.Join(
                ", ",
                (result.FailingTypeNames ?? []).DefaultIfEmpty("(none reported)")));
    }
}
