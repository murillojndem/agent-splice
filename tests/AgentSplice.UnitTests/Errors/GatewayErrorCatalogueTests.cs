using AgentSplice.Application.Errors;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using Xunit;

namespace AgentSplice.UnitTests.Errors;

/// <summary>
/// The mapping from internal failure classes to what a client sees (docs/API.md).
/// </summary>
public sealed class GatewayErrorCatalogueTests
{
    [Fact]
    public void Every_failure_class_has_a_client_facing_error()
    {
        // Totality is the point: a new failure class must not be addable without deciding what it
        // means to a client.
        foreach (var failureClass in Enum.GetValues<FailureClass>())
        {
            Assert.NotNull(GatewayErrorCatalogue.For(failureClass));
        }
    }

    [Fact]
    public void The_catalogue_declares_no_failure_class_the_domain_does_not_have()
    {
        Assert.Equal(
            Enum.GetValues<FailureClass>().ToHashSet(),
            GatewayErrorCatalogue.DeclaredFailureClasses.ToHashSet());
    }

    [Fact]
    public void Every_error_reports_the_failure_class_it_translates()
    {
        foreach (var failureClass in Enum.GetValues<FailureClass>())
        {
            Assert.Equal(failureClass, GatewayErrorCatalogue.For(failureClass).FailureClass);
        }
    }

    [Fact]
    public void Every_error_uses_a_published_code_and_type()
    {
        foreach (var failureClass in Enum.GetValues<FailureClass>())
        {
            var error = GatewayErrorCatalogue.For(failureClass);

            Assert.True(ErrorCodes.Core.Contains(error.Code), error.Code + " is not a published code.");
            Assert.True(ErrorTypes.All.Contains(error.Type), error.Type + " is not a published type.");
        }
    }

    [Fact]
    public void Distinct_failure_classes_map_to_distinct_codes()
    {
        // Two classes sharing a code would make them indistinguishable in a conformance report.
        var codes = Enum.GetValues<FailureClass>()
            .Select(failureClass => GatewayErrorCatalogue.For(failureClass).Code)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(FailureClass.InvalidRequest)]
    [InlineData(FailureClass.ModelNotFound)]
    public void A_client_mistake_is_reported_as_a_client_error(FailureClass failureClass)
    {
        Assert.InRange(GatewayErrorCatalogue.For(failureClass).StatusCode, 400, 499);
    }

    [Theory]
    [InlineData(FailureClass.RuntimeUnavailable)]
    [InlineData(FailureClass.RuntimeAuthenticationFailed)]
    [InlineData(FailureClass.UpstreamTimeout)]
    [InlineData(FailureClass.InvalidUpstreamResponse)]
    [InlineData(FailureClass.InvalidUpstreamStream)]
    [InlineData(FailureClass.InternalError)]
    public void An_upstream_or_gateway_fault_never_blames_the_client(FailureClass failureClass)
    {
        // A 4xx tells the client to change its request. None of these can be fixed that way.
        Assert.InRange(GatewayErrorCatalogue.For(failureClass).StatusCode, 500, 599);
    }

    [Fact]
    public void An_upstream_credential_failure_is_never_reported_as_401_or_403()
    {
        // The credential is the gateway's, not the client's. Echoing the runtime's status would tell
        // a client to fix a key it does not own.
        var error = GatewayErrorCatalogue.For(FailureClass.RuntimeAuthenticationFailed);

        Assert.Equal(502, error.StatusCode);
        Assert.NotEqual(401, error.StatusCode);
        Assert.NotEqual(403, error.StatusCode);
    }

    [Fact]
    public void A_missing_provider_module_is_reported_as_a_configuration_fault()
    {
        // The runtime was never contacted, so reporting it as unreachable would send an operator
        // looking at the wrong thing.
        var error = GatewayErrorCatalogue.For(FailureClass.RuntimeNotFound);

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(ErrorTypes.Configuration, error.Type);
    }

    [Fact]
    public void A_cancellation_is_recorded_with_a_status_distinguishable_from_a_server_fault()
    {
        var error = GatewayErrorCatalogue.For(FailureClass.RequestCancelled);

        Assert.Equal(GatewayErrorCatalogue.ClientClosedRequestStatus, error.StatusCode);
        Assert.Equal(ErrorTypes.Cancellation, error.Type);
    }

    [Fact]
    public void No_message_carries_a_host_a_credential_or_upstream_content()
    {
        // Messages are compile-time constants precisely so this can be asserted at all.
        string[] forbidden = ["http://", "https://", "127.0.0.1", "localhost", "Bearer", "{", "}"];

        foreach (var failureClass in Enum.GetValues<FailureClass>())
        {
            var message = GatewayErrorCatalogue.For(failureClass).Message;

            foreach (var fragment in forbidden)
            {
                Assert.DoesNotContain(fragment, message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_message_reads_as_a_complete_sentence()
    {
        foreach (var failureClass in Enum.GetValues<FailureClass>())
        {
            var message = GatewayErrorCatalogue.For(failureClass).Message;

            Assert.EndsWith(".", message, StringComparison.Ordinal);
            Assert.True(char.IsUpper(message[0]));
        }
    }

    [Theory]
    [InlineData(UpstreamFailureReason.Unreachable, FailureClass.RuntimeUnavailable)]
    [InlineData(UpstreamFailureReason.AuthenticationRejected, FailureClass.RuntimeAuthenticationFailed)]
    [InlineData(UpstreamFailureReason.InvalidResponse, FailureClass.InvalidUpstreamResponse)]
    [InlineData(UpstreamFailureReason.ResponseTooLarge, FailureClass.InvalidUpstreamResponse)]
    [InlineData(UpstreamFailureReason.Cancelled, FailureClass.RequestCancelled)]
    public void An_upstream_failure_translates_to_its_failure_class(
        UpstreamFailureReason reason,
        FailureClass expected)
    {
        Assert.Equal(expected, GatewayErrorCatalogue.Translate(UpstreamFailure.Create(reason)).FailureClass);
    }

    [Fact]
    public void Every_timeout_phase_translates_to_a_gateway_timeout()
    {
        foreach (var phase in Enum.GetValues<TimeoutPhase>())
        {
            var error = GatewayErrorCatalogue.Translate(UpstreamFailure.Timeout(phase));

            Assert.Equal(FailureClass.UpstreamTimeout, error.FailureClass);
            Assert.Equal(504, error.StatusCode);
        }
    }

    [Fact]
    public void Every_upstream_failure_reason_can_be_translated()
    {
        foreach (var reason in Enum.GetValues<UpstreamFailureReason>())
        {
            var failure = reason == UpstreamFailureReason.Timeout
                ? UpstreamFailure.Timeout(TimeoutPhase.Total)
                : UpstreamFailure.Create(reason);

            Assert.NotNull(GatewayErrorCatalogue.Translate(failure));
        }
    }

    [Fact]
    public void An_oversized_body_is_reported_as_payload_too_large()
    {
        Assert.Equal(413, GatewayErrorCatalogue.BodyTooLarge.StatusCode);
    }

    [Fact]
    public void Discovery_that_could_not_be_attempted_is_not_reported_as_a_missing_model()
    {
        // "The model does not exist" and "AgentSplice could not ask" are different facts.
        Assert.Equal(502, GatewayErrorCatalogue.DiscoveryUnavailable.StatusCode);
        Assert.NotEqual(
            GatewayErrorCatalogue.For(FailureClass.ModelNotFound).Code,
            GatewayErrorCatalogue.DiscoveryUnavailable.Code);
    }

    [Fact]
    public void A_validation_failure_can_name_the_offending_field()
    {
        var error = GatewayErrorCatalogue.InvalidRequest("The 'stream' field must be a boolean.", "stream");

        Assert.Equal("stream", error.Param);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void An_undeclared_failure_class_is_a_defect_rather_than_a_silent_default()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GatewayErrorCatalogue.For((FailureClass)999));
    }
}
