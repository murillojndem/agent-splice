using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Observations;

/// <summary>
/// Timeline ordering and, more importantly, the absence rules: an unobserved boundary must stay
/// unknown rather than becoming a zero (docs/SPECIFICATION.md FR-TRACE-004 to FR-TRACE-006).
/// </summary>
public sealed class ExchangeTimelineTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Append_assigns_consecutive_sequence_numbers_from_zero()
    {
        var clock = new FakeTimeProvider(Origin);
        var timeline = new ExchangeTimeline(ExchangeId.New());

        timeline.Append(ObservationType.RequestAccepted, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(3));
        timeline.Append(ObservationType.ValidationCompleted, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(2));
        timeline.Append(ObservationType.ModelResolved, clock.GetUtcNow());

        Assert.Equal([0, 1, 2], timeline.Observations.Select(observation => observation.Sequence));
        Assert.Equal(
            [ObservationType.RequestAccepted, ObservationType.ValidationCompleted, ObservationType.ModelResolved],
            timeline.Observations.Select(observation => observation.Type));
    }

    [Fact]
    public void Append_records_the_supplied_timestamp_without_reinterpreting_it()
    {
        var clock = new FakeTimeProvider(Origin);
        var timeline = new ExchangeTimeline(ExchangeId.New());

        var observation = timeline.Append(ObservationType.FirstUpstreamByte, clock.GetUtcNow());

        Assert.Equal(Origin, observation.Timestamp);
    }

    [Fact]
    public void Append_stamps_every_observation_with_the_owning_exchange()
    {
        var exchangeId = ExchangeId.New();
        var timeline = new ExchangeTimeline(exchangeId);

        var observation = timeline.Append(ObservationType.RequestAccepted, Origin);

        Assert.Equal(exchangeId, observation.ExchangeId);
    }

    [Fact]
    public void Append_refuses_to_record_a_first_byte_boundary_twice()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());
        timeline.Append(ObservationType.FirstUpstreamByte, Origin);

        var exception = Assert.Throws<InvalidOperationException>(
            () => timeline.Append(ObservationType.FirstUpstreamByte, Origin.AddMilliseconds(5)));

        Assert.Contains("only be recorded once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_allows_repeatable_boundaries_to_occur_more_than_once()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());

        timeline.Append(ObservationType.NativeToolCallObserved, Origin);
        timeline.Append(ObservationType.NativeToolCallObserved, Origin.AddMilliseconds(5));

        Assert.Equal(2, timeline.Count);
    }

    [Fact]
    public void TimestampOf_returns_null_for_a_boundary_that_was_never_observed()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());
        timeline.Append(ObservationType.RequestAccepted, Origin);

        Assert.Null(timeline.TimestampOf(ObservationType.FirstSemanticEvent));
        Assert.False(timeline.Contains(ObservationType.FirstSemanticEvent));
    }

    [Fact]
    public void DurationBetween_returns_null_when_either_boundary_is_missing()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());
        timeline.Append(ObservationType.UpstreamRequestOpened, Origin);

        Assert.Null(timeline.DurationBetween(
            ObservationType.UpstreamRequestOpened,
            ObservationType.FirstUpstreamByte));

        Assert.Null(timeline.DurationBetween(
            ObservationType.RequestAccepted,
            ObservationType.UpstreamRequestOpened));
    }

    [Fact]
    public void DurationBetween_measures_the_interval_when_both_boundaries_exist()
    {
        var clock = new FakeTimeProvider(Origin);
        var timeline = new ExchangeTimeline(ExchangeId.New());

        timeline.Append(ObservationType.UpstreamRequestOpened, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(42));
        timeline.Append(ObservationType.FirstUpstreamByte, clock.GetUtcNow());

        Assert.Equal(
            TimeSpan.FromMilliseconds(42),
            timeline.DurationBetween(ObservationType.UpstreamRequestOpened, ObservationType.FirstUpstreamByte));
    }

    [Fact]
    public void Observations_is_a_snapshot_that_later_appends_do_not_mutate()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());
        timeline.Append(ObservationType.RequestAccepted, Origin);

        var snapshot = timeline.Observations;
        timeline.Append(ObservationType.ValidationCompleted, Origin.AddMilliseconds(1));

        Assert.Single(snapshot);
        Assert.Equal(2, timeline.Count);
    }

    [Fact]
    public void Constructor_rejects_an_empty_exchange_identity()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeTimeline(default));
    }

    [Fact]
    public void Find_returns_the_first_occurrence_of_a_repeatable_boundary()
    {
        var timeline = new ExchangeTimeline(ExchangeId.New());
        timeline.Append(ObservationType.NativeToolCallObserved, Origin);
        timeline.Append(ObservationType.NativeToolCallObserved, Origin.AddSeconds(1));

        Assert.Equal(Origin, timeline.Find(ObservationType.NativeToolCallObserved)?.Timestamp);
    }

    [Theory]
    [InlineData(ObservationType.FirstUpstreamByte, true)]
    [InlineData(ObservationType.FirstDecodedEvent, true)]
    [InlineData(ObservationType.FirstSemanticEvent, true)]
    [InlineData(ObservationType.FirstClientEventFlushed, true)]
    [InlineData(ObservationType.ClientCompleted, true)]
    [InlineData(ObservationType.TimeoutFired, false)]
    [InlineData(ObservationType.NativeToolCallObserved, false)]
    [InlineData(ObservationType.PersistenceFailed, false)]
    public void Single_occurrence_rules_protect_the_boundaries_that_carry_first_event_evidence(
        ObservationType type,
        bool expectedSingleOccurrence)
    {
        Assert.Equal(expectedSingleOccurrence, ObservationTypeRules.IsSingleOccurrence(type));
    }
}
