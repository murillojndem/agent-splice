using AgentSplice.Application.Exchanges;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// Captures the evidence the gateway hands to its sink.
/// </summary>
/// <remarks>
/// Nothing is persisted and there is no administrative API yet, so this seam is the only way a test
/// can see a timeline at all. It lives here rather than in <c>AgentSplice.TestSupport</c> because
/// that project deliberately references no production assembly, and this one is defined entirely in
/// terms of a production contract.
///
/// Waiting is necessary rather than incidental: the record is handed over after the client's
/// response has completed, so a test that read its response and immediately asserted would race the
/// gateway.
/// </remarks>
internal sealed class CapturingExchangeSink : IExchangeRecordSink
{
    private readonly TaskCompletionSource<ExchangeRecord> first =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<ExchangeRecord> records = [];

    /// <summary>Every record handed over so far.</summary>
    internal IReadOnlyList<ExchangeRecord> Records
    {
        get
        {
            lock (records)
            {
                return records.ToArray();
            }
        }
    }

    public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken)
    {
        lock (records)
        {
            records.Add(record);
        }

        first.TrySetResult(record);

        return ValueTask.CompletedTask;
    }

    /// <summary>Waits for the first record, failing loudly rather than hanging.</summary>
    internal async Task<ExchangeRecord> WaitForRecordAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(first.Task, Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != first.Task)
        {
            throw new TimeoutException("No exchange record was handed to the sink in time.");
        }

        return await first.Task.ConfigureAwait(false);
    }
}
