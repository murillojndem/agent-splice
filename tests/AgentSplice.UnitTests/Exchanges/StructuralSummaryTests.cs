using System.Globalization;
using AgentSplice.Domain.Exchanges;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// Structural summaries record shapes, not content (docs/SPECIFICATION.md FR-TRACE-003,
/// FR-TRACE-008). The bounds here stop a generated or adversarial request from turning a summary
/// into an unbounded store of caller-chosen strings.
/// </summary>
public sealed class StructuralSummaryTests
{
    [Fact]
    public void A_request_summary_records_counts_and_flags()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 3,
            messageCountsByRole: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["system"] = 1,
                ["user"] = 1,
                ["assistant"] = 1,
            },
            toolDeclarationCount: 4,
            toolChoicePresent: true,
            streamRequested: true,
            streamOptionsPresent: true,
            requestBodyBytes: 8192);

        Assert.Equal(3, summary.MessageCount);
        Assert.Equal(1, summary.MessageCountsByRole["user"]);
        Assert.Equal(4, summary.ToolDeclarationCount);
        Assert.True(summary.ToolChoicePresent);
        Assert.True(summary.StreamRequested);
        Assert.True(summary.StreamOptionsPresent);
        Assert.Equal(8192L, summary.RequestBodyBytes);
    }

    [Fact]
    public void A_transparent_exchange_drops_no_fields()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["reasoning_effort", "seed"]);

        Assert.Equal(
            [SafeVocabulary.HashName("reasoning_effort"), SafeVocabulary.HashName("seed")],
            summary.UnknownTopLevelFieldNames);
        Assert.Empty(summary.DroppedFieldNames);
    }

    [Fact]
    public void Unknown_field_names_are_deduplicated()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["seed", "seed", "seed"]);

        Assert.Equal([SafeVocabulary.HashName("seed")], summary.UnknownTopLevelFieldNames);
    }

    [Fact]
    public void Unknown_field_names_are_bounded_in_count()
    {
        var names = Enumerable
            .Range(0, StructuralRequestSummary.MaxUnknownFieldNames + 10)
            .Select(index => "field" + index.ToString(CultureInfo.InvariantCulture));

        var summary = StructuralRequestSummary.Create(messageCount: 1, unknownTopLevelFieldNames: names);

        Assert.Equal(StructuralRequestSummary.MaxUnknownFieldNames, summary.UnknownTopLevelFieldNames.Count);
    }

    [Fact]
    public void An_unknown_field_name_is_stored_as_a_hash_and_never_as_itself()
    {
        // The name is chosen by the client, so it can carry anything the client wants to put into a
        // store that has content capture switched off. Truncating it bounded how much was kept and
        // left every character of the remainder client-chosen.
        const string Secret = "SENTINEL-PROMPT-abc123";

        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: [Secret]);

        var stored = Assert.Single(summary.UnknownTopLevelFieldNames);

        Assert.DoesNotContain("SENTINEL", stored, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(SafeVocabulary.HashPrefix, stored, StringComparison.Ordinal);

        // Stable, so an operator asking "was this field forwarded?" hashes the name and compares.
        Assert.Equal(SafeVocabulary.HashName(Secret), stored);
    }

    [Fact]
    public void A_field_name_containing_control_characters_is_hashed_rather_than_rejected()
    {
        // It used to throw. The name comes from the client, so a validating helper that rejects by
        // throwing turns a hostile property name into a failed request: input validation shaped like
        // a denial of service.
        var summary = StructuralRequestSummary.Create(
            messageCount: 1,
            unknownTopLevelFieldNames: ["field\nname"]);

        Assert.StartsWith(
            SafeVocabulary.HashPrefix,
            Assert.Single(summary.UnknownTopLevelFieldNames),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_outside_the_vocabulary_is_bucketed_and_still_counted()
    {
        var summary = StructuralRequestSummary.Create(
            messageCount: 3,
            messageCountsByRole:
            [
                new KeyValuePair<string, int>("user", 1),
                new KeyValuePair<string, int>("SENTINEL-PROMPT-abc123", 1),
                new KeyValuePair<string, int>("also-not-a-role", 1),
            ]);

        Assert.Equal(1, summary.MessageCountsByRole["user"]);

        // Both unrecognised roles fold into one bucket, and the counts still reconcile with
        // MessageCount so nothing looks lost.
        Assert.Equal(2, summary.MessageCountsByRole[SafeVocabulary.Unrecognised]);
        Assert.Equal(summary.MessageCount, summary.MessageCountsByRole.Values.Sum());

        foreach (var key in summary.MessageCountsByRole.Keys)
        {
            Assert.DoesNotContain("SENTINEL", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Negative_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: 1, toolDeclarationCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StructuralRequestSummary.Create(messageCount: 1, requestBodyBytes: -1));
    }

    [Fact]
    public void A_response_summary_records_stream_shape_and_native_tool_calls()
    {
        var summary = StructuralResponseSummary.Create(
            choiceCount: 1,
            finishReasons: ["stop"],
            nativeToolCallCount: 2,
            responseBodyBytes: 4096,
            streamEventCount: 37,
            usageReported: true);

        Assert.Equal(1, summary.ChoiceCount);
        Assert.Equal(["stop"], summary.FinishReasons);
        Assert.Equal(2, summary.NativeToolCallCount);
        Assert.Equal(4096L, summary.ResponseBodyBytes);
        Assert.Equal(37, summary.StreamEventCount);
        Assert.True(summary.UsageReported);
    }

    [Fact]
    public void A_non_streamed_response_summary_reports_no_events_and_no_usage_by_default()
    {
        var summary = StructuralResponseSummary.Create(choiceCount: 1);

        Assert.Equal(0, summary.StreamEventCount);
        Assert.False(summary.UsageReported);
        Assert.Empty(summary.FinishReasons);
    }

    [Fact]
    public void Finish_reasons_are_deduplicated_and_drawn_from_the_vocabulary()
    {
        // The runtime chooses this string. A runtime returning generated text in it would otherwise
        // have that text stored with content capture disabled.
        var summary = StructuralResponseSummary.Create(
            finishReasons: ["stop", "stop", "length", "SENTINEL-RESPONSE-xyz789", "another-unknown"]);

        Assert.Equal(["stop", "length", SafeVocabulary.Unrecognised], summary.FinishReasons);

        // No count bound is needed once the vocabulary is closed, and there is none: the list cannot
        // exceed the vocabulary plus one bucket however many distinct strings arrive.
        Assert.Equal(summary.FinishReasons.Distinct(StringComparer.Ordinal).Count(), summary.FinishReasons.Count);
    }

    [Fact]
    public void Blank_finish_reasons_are_ignored()
    {
        var summary = StructuralResponseSummary.Create(finishReasons: ["", "   ", "stop"]);

        Assert.Equal(["stop"], summary.FinishReasons);
    }
}
