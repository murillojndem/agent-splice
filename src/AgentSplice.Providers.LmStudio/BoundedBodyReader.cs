using System.Buffers;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Reads a response body into memory, refusing to exceed a configured bound.
/// </summary>
/// <remarks>
/// The non-streaming path is deliberately fully buffered so the body can be forwarded verbatim,
/// which makes an unbounded read a direct route from one defective runtime to gateway-wide memory
/// pressure. Reading stops at the bound plus one byte, so the overrun is detected without ever
/// materialising the oversized payload.
/// </remarks>
internal static class BoundedBodyReader
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>The outcome of a bounded read.</summary>
    internal readonly record struct Result(byte[]? Body, bool ExceededLimit, bool Truncated)
    {
        internal static Result TooLarge() => new(Body: null, ExceededLimit: true, Truncated: false);

        internal static Result PrematureEnd() => new(Body: null, ExceededLimit: false, Truncated: true);

        internal static Result Complete(byte[] body) => new(body, ExceededLimit: false, Truncated: false);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes, reporting an overrun rather than throwing.</summary>
    /// <param name="stream">The response body.</param>
    /// <param name="maxBytes">The inclusive upper bound on body size.</param>
    /// <param name="expectedLength">The declared content length, when the runtime declared one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static Task<Result> ReadAsync(
        Stream stream,
        long maxBytes,
        long? expectedLength,
        CancellationToken cancellationToken) =>
        ReadAsync(stream, maxBytes, expectedLength, onFirstByte: null, cancellationToken);

    /// <summary>Reads a bounded body, signalling when the first byte arrives.</summary>
    /// <param name="stream">The response body.</param>
    /// <param name="maxBytes">The inclusive upper bound on body size.</param>
    /// <param name="expectedLength">The declared content length, when the runtime declared one.</param>
    /// <param name="onFirstByte">
    /// Invoked once, when the first body byte is read. This is what makes
    /// <c>FirstUpstreamByte</c> a boundary AgentSplice observed rather than one inferred after the
    /// body had already arrived (FR-TRACE-006).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<Result> ReadAsync(
        Stream stream,
        long maxBytes,
        long? expectedLength,
        Action? onFirstByte,
        CancellationToken cancellationToken)
    {
        // A declared length over the bound is refusable before a single byte is transferred.
        if (expectedLength is { } declared && declared > maxBytes)
        {
            return Result.TooLarge();
        }

        var buffer = new MemoryStream(capacity: (int)Math.Min(expectedLength ?? ChunkSize, ChunkSize));
        var rented = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            int read;

            while ((read = await stream.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length == 0)
                {
                    onFirstByte?.Invoke();
                }

                if (buffer.Length + read > maxBytes)
                {
                    return Result.TooLarge();
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            // Cleared: this held model output, and a pooled array outlives the exchange that filled
            // it (docs/SECURITY.md).
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        // A body that stopped short of its declared length is a truncated response, not a small one:
        // parsing it would report a protocol error whose real cause was a dropped connection.
        return expectedLength is { } length && buffer.Length != length
            ? Result.PrematureEnd()
            : Result.Complete(buffer.ToArray());
    }
}
