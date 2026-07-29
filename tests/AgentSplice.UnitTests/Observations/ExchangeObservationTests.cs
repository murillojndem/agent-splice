using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using Xunit;

namespace AgentSplice.UnitTests.Observations;

/// <summary>
/// Observation construction guards. An out-of-range confidence or a negative duration would be
/// recorded as evidence, so they are rejected at the boundary.
/// </summary>
public sealed class ExchangeObservationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_defaults_details_to_empty_rather_than_null()
    {
        var observation = Create();

        Assert.NotNull(observation.Details);
        Assert.True(observation.Details.IsEmpty);
    }

    [Fact]
    public void Create_leaves_duration_and_confidence_absent_when_not_supplied()
    {
        var observation = Create();

        Assert.Null(observation.Duration);
        Assert.Null(observation.Confidence);
    }

    [Fact]
    public void Create_rejects_a_negative_sequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(sequence: -1));
    }

    [Fact]
    public void Create_rejects_a_negative_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Create(duration: TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData(-0.01d)]
    [InlineData(1.01d)]
    [InlineData(double.NaN)]
    public void Create_rejects_a_confidence_outside_the_unit_interval(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(confidence: confidence));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(1d)]
    public void Create_accepts_a_confidence_within_the_unit_interval(double confidence)
    {
        Assert.Equal(confidence, Create(confidence: confidence).Confidence);
    }

    [Fact]
    public void Create_rejects_an_empty_observation_identity()
    {
        Assert.Throws<ArgumentException>(() => ExchangeObservation.Create(
            default,
            ExchangeId.New(),
            0,
            ObservationType.RequestAccepted,
            Origin,
            ObservationSource.Gateway));
    }

    [Fact]
    public void Create_rejects_an_empty_exchange_identity()
    {
        Assert.Throws<ArgumentException>(() => ExchangeObservation.Create(
            ObservationId.New(),
            default,
            0,
            ObservationType.RequestAccepted,
            Origin,
            ObservationSource.Gateway));
    }

    [Fact]
    public void Create_rejects_an_undefined_observation_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeObservation.Create(
            ObservationId.New(),
            ExchangeId.New(),
            0,
            (ObservationType)9999,
            Origin,
            ObservationSource.Gateway));
    }

    [Fact]
    public void Create_rejects_an_undefined_observation_source()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeObservation.Create(
            ObservationId.New(),
            ExchangeId.New(),
            0,
            ObservationType.RequestAccepted,
            Origin,
            (ObservationSource)9999));
    }

    private static ExchangeObservation Create(
        int sequence = 0,
        TimeSpan? duration = null,
        double? confidence = null) =>
        ExchangeObservation.Create(
            ObservationId.New(),
            ExchangeId.New(),
            sequence,
            ObservationType.RequestAccepted,
            Origin,
            ObservationSource.Gateway,
            duration,
            confidence);
}
