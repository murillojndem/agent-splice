using AgentSplice.Domain.Observations;
using Xunit;

namespace AgentSplice.UnitTests.Observations;

/// <summary>
/// <see cref="SafeDetails"/> is the only per-exchange detail channel that reaches traces and
/// administrative APIs, so its bounds are a privacy control, not a convenience
/// (docs/SPECIFICATION.md FR-TRACE-003, FR-OBS-007, docs/THREAT_MODEL.md).
/// </summary>
public sealed class SafeDetailsTests
{
    [Fact]
    public void Empty_carries_no_entries()
    {
        Assert.True(SafeDetails.Empty.IsEmpty);
        Assert.Empty(SafeDetails.Empty.Values);
    }

    [Fact]
    public void Create_keeps_supplied_entries()
    {
        var details = SafeDetails.Create("phase", "response_headers", "runtime.id", "lmstudio-local");

        Assert.Equal(2, details.Values.Count);
        Assert.Equal("response_headers", details.Values["phase"]);
        Assert.Equal("lmstudio-local", details.Values["runtime.id"]);
    }

    [Fact]
    public void Create_truncates_long_values_rather_than_rejecting_them()
    {
        var value = new string('x', SafeDetails.MaxValueLength + 50);

        var details = SafeDetails.Create("field", value);
        var stored = details.Values["field"];

        Assert.EndsWith(SafeDetails.TruncationMarker, stored, StringComparison.Ordinal);
        Assert.Equal(SafeDetails.MaxValueLength + SafeDetails.TruncationMarker.Length, stored.Length);
    }

    [Fact]
    public void Create_replaces_control_characters_so_a_value_cannot_inject_log_lines()
    {
        var details = SafeDetails.Create("field", "first\nsecond\tthird\r\n");

        Assert.Equal("first second third  ", details.Values["field"]);
    }

    [Fact]
    public void Create_maps_a_null_value_to_an_empty_string()
    {
        var details = SafeDetails.Create("field", null);

        Assert.Equal(string.Empty, details.Values["field"]);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has:colon")]
    public void Create_rejects_keys_outside_the_permitted_charset(string key)
    {
        Assert.Throws<ArgumentException>(() => SafeDetails.Create(key, "value"));
    }

    [Fact]
    public void Create_rejects_a_blank_key()
    {
        Assert.Throws<ArgumentException>(() => SafeDetails.Create("   ", "value"));
    }

    [Fact]
    public void Create_rejects_an_oversized_key()
    {
        var key = new string('k', SafeDetails.MaxKeyLength + 1);

        Assert.Throws<ArgumentException>(() => SafeDetails.Create(key, "value"));
    }

    [Fact]
    public void Create_rejects_a_repeated_key()
    {
        var entries = new[]
        {
            new KeyValuePair<string, string?>("phase", "first"),
            new KeyValuePair<string, string?>("phase", "second"),
        };

        Assert.Throws<ArgumentException>(() => SafeDetails.Create(entries));
    }

    [Fact]
    public void Create_rejects_more_entries_than_the_bound_allows()
    {
        var entries = Enumerable
            .Range(0, SafeDetails.MaxEntries + 1)
            .Select(index => new KeyValuePair<string, string?>(
                string.Concat("key", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "value"))
            .ToArray();

        Assert.Throws<ArgumentException>(() => SafeDetails.Create(entries));
    }

    [Fact]
    public void Create_accepts_exactly_the_maximum_number_of_entries()
    {
        var entries = Enumerable
            .Range(0, SafeDetails.MaxEntries)
            .Select(index => new KeyValuePair<string, string?>(
                string.Concat("key", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "value"))
            .ToArray();

        Assert.Equal(SafeDetails.MaxEntries, SafeDetails.Create(entries).Values.Count);
    }
}
