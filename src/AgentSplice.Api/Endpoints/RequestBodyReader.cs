using System.Buffers;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Reads a request body into memory, refusing to exceed the configured bound.
/// </summary>
/// <remarks>
/// Reads to the bound plus one byte, so an overrun is detected without materialising the oversized
/// payload. A declared <c>Content-Length</c> over the bound is refused before a byte is transferred.
/// </remarks>
internal static class RequestBodyReader
{
    private const int ChunkSize = 16 * 1024;

    internal readonly record struct Result(byte[] Body, bool ExceededLimit)
    {
        internal static Result TooLarge() => new([], ExceededLimit: true);
    }

    internal static async Task<Result> ReadAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } declared && declared > maxBytes)
        {
            return Result.TooLarge();
        }

        using var buffer = new MemoryStream(
            capacity: (int)Math.Min(request.ContentLength ?? ChunkSize, ChunkSize));

        var rented = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            int read;

            while ((read = await request.Body.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return Result.TooLarge();
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            // Cleared: this held the client's prompt, and a pooled array outlives the request that
            // filled it (docs/SECURITY.md).
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        return new Result(buffer.ToArray(), ExceededLimit: false);
    }
}
