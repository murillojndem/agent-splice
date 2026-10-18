using System.Reflection;
using AgentSplice.Application.Diagnostics;
using AgentSplice.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// Every stable log event is documented with its number and name (docs/OBSERVABILITY.md
/// "Structured logs").
/// </summary>
/// <remarks>
/// The identifier is what an alert rule matches, so an event the document does not list is one an
/// operator cannot know to watch for, and a number listed against the wrong name sends them to the
/// wrong one.
/// </remarks>
public sealed class EventIdContractTests
{
    [Fact]
    public void Every_event_id_is_listed_with_its_number_and_name()
    {
        var document = RepositoryPaths.ReadText("docs", "OBSERVABILITY.md");

        var events = typeof(GatewayEventIds)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => (EventId)property.GetValue(null)!)
            .ToArray();

        Assert.NotEmpty(events);

        foreach (var eventId in events)
        {
            Assert.Contains(
                FormattableString.Invariant($"| `{eventId.Id} {eventId.Name}` |"),
                document,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_two_events_share_a_number()
    {
        var numbers = typeof(GatewayEventIds)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => ((EventId)property.GetValue(null)!).Id)
            .ToArray();

        Assert.Equal(numbers.Length, numbers.Distinct().Count());
    }
}
