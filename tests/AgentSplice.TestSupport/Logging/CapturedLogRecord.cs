using Microsoft.Extensions.Logging;

namespace AgentSplice.TestSupport.Logging;

/// <summary>
/// One captured log record, with every channel a value could have reached.
/// </summary>
/// <param name="Level">The level it was written at.</param>
/// <param name="Category">The logger category.</param>
/// <param name="EventId">The event identifier.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception's full text, when one was attached.</param>
/// <param name="StateValues">Structured state, rendered as <c>key=value</c>.</param>
/// <param name="Scopes">Active scopes, rendered the same way.</param>
public sealed record CapturedLogRecord(
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? Exception,
    IReadOnlyList<string> StateValues,
    IReadOnlyList<string> Scopes)
{
    /// <summary>Every string this record contributed to a log sink.</summary>
    public IEnumerable<string> AllText()
    {
        yield return Message;

        if (Exception is { } exception)
        {
            yield return exception;
        }

        foreach (var value in StateValues)
        {
            yield return value;
        }

        foreach (var scope in Scopes)
        {
            yield return scope;
        }
    }
}
