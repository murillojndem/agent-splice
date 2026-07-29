using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Runtimes;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// Classification of upstream failures without any transport type
/// (docs/SPECIFICATION.md FR-CHAT-008, FR-HEALTH-004).
/// </summary>
public sealed class UpstreamFailureTests
{
    [Fact]
    public void A_timeout_must_record_the_phase_that_elapsed()
    {
        // "Timed out" alone is not attributable: refusing a connection, never answering, and
        // stalling mid-answer are three different runtime defects.
        Assert.Throws<ArgumentException>(() => UpstreamFailure.Create(UpstreamFailureReason.Timeout));
    }

    [Fact]
    public void Only_a_timeout_carries_a_phase()
    {
        Assert.Throws<ArgumentException>(() => UpstreamFailure.Create(
            UpstreamFailureReason.Unreachable,
            TimeoutPhase.Connect));
    }

    [Theory]
    [InlineData(TimeoutPhase.Connect)]
    [InlineData(TimeoutPhase.ResponseHeaders)]
    [InlineData(TimeoutPhase.Total)]
    public void A_timeout_records_its_phase(TimeoutPhase phase)
    {
        Assert.Equal(phase, UpstreamFailure.Timeout(phase).Phase);
    }

    [Theory]
    [InlineData(UpstreamFailureReason.AuthenticationRejected, RuntimeHealthStatus.AuthenticationFailed)]
    [InlineData(UpstreamFailureReason.InvalidResponse, RuntimeHealthStatus.IncompatibleResponse)]
    [InlineData(UpstreamFailureReason.ResponseTooLarge, RuntimeHealthStatus.IncompatibleResponse)]
    [InlineData(UpstreamFailureReason.Unreachable, RuntimeHealthStatus.Unreachable)]
    public void A_failure_maps_to_the_health_status_that_names_its_cause(
        UpstreamFailureReason reason,
        RuntimeHealthStatus expected)
    {
        Assert.Equal(expected, UpstreamFailure.Create(reason).HealthStatus);
    }

    [Fact]
    public void A_timeout_reports_the_runtime_as_unreachable()
    {
        Assert.Equal(RuntimeHealthStatus.Unreachable, UpstreamFailure.Timeout(TimeoutPhase.Connect).HealthStatus);
    }

    [Fact]
    public void Our_own_cancellation_leaves_runtime_health_undetermined()
    {
        // A caller that gave up says nothing about the runtime, so health must not be downgraded on
        // the strength of our own cancellation.
        Assert.Equal(
            RuntimeHealthStatus.Unknown,
            UpstreamFailure.Create(UpstreamFailureReason.Cancelled).HealthStatus);
    }

    [Fact]
    public void A_failure_carries_no_detail_by_default()
    {
        Assert.True(UpstreamFailure.Create(UpstreamFailureReason.Unreachable).Details.IsEmpty);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void An_impossible_status_code_is_rejected(int statusCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UpstreamFailure.Create(
            UpstreamFailureReason.AuthenticationRejected,
            statusCode: statusCode));
    }

    [Fact]
    public void An_authentication_failure_can_record_the_status_the_runtime_returned()
    {
        Assert.Equal(
            401,
            UpstreamFailure.Create(UpstreamFailureReason.AuthenticationRejected, statusCode: 401).StatusCode);
    }
}
