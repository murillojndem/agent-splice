using System.Collections.Frozen;
using System.Globalization;

namespace AgentSplice.Domain.Observations;

/// <summary>
/// A bounded, sanitised key/value map attached to observations and health results.
/// </summary>
/// <remarks>
/// This is the only channel through which per-exchange detail reaches traces, administrative APIs,
/// and the dashboard. The bounds exist so that prompt content, tool arguments, or model output can
/// never be smuggled into observability output through a "details" bag
/// (docs/SPECIFICATION.md FR-TRACE-003, FR-OBS-007, docs/THREAT_MODEL.md).
///
/// Keys are a programming contract and are rejected when invalid. Values are data-dependent and are
/// truncated instead of rejected, so that a long but otherwise legitimate value degrades an
/// observation rather than failing the exchange.
/// </remarks>
public sealed class SafeDetails
{
    /// <summary>Maximum number of entries.</summary>
    public const int MaxEntries = 16;

    /// <summary>Maximum key length.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>Maximum value length before truncation.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Marker appended to a value that exceeded <see cref="MaxValueLength"/>.</summary>
    public const string TruncationMarker = "...[truncated]";

    private static readonly FrozenDictionary<string, string> NoValues =
        FrozenDictionary<string, string>.Empty;

    private SafeDetails(FrozenDictionary<string, string> values) => Values = values;

    /// <summary>An observation with no additional detail.</summary>
    public static SafeDetails Empty { get; } = new(NoValues);

    /// <summary>The sanitised entries. Enumeration order is unspecified.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>True when no detail is attached.</summary>
    public bool IsEmpty => Values.Count == 0;

    /// <summary>Creates sanitised details from key/value pairs.</summary>
    /// <exception cref="ArgumentException">A key is empty, too long, uses an unsupported character, or repeats.</exception>
    public static SafeDetails Create(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var accumulated = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var key = NormaliseKey(entry.Key);

            if (!accumulated.TryAdd(key, NormaliseValue(entry.Value)))
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Detail key '{key}' was supplied more than once."),
                    nameof(entries));
            }

            if (accumulated.Count > MaxEntries)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "An observation may carry at most {0} detail entries.",
                        MaxEntries),
                    nameof(entries));
            }
        }

        return accumulated.Count == 0
            ? Empty
            : new SafeDetails(accumulated.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Creates sanitised details from a single entry.</summary>
    public static SafeDetails Create(string key, string? value) =>
        Create([new KeyValuePair<string, string?>(key, value)]);

    /// <summary>Creates sanitised details from two entries.</summary>
    public static SafeDetails Create(string firstKey, string? firstValue, string secondKey, string? secondValue) =>
        Create(
        [
            new KeyValuePair<string, string?>(firstKey, firstValue),
            new KeyValuePair<string, string?>(secondKey, secondValue),
        ]);

    private static string NormaliseKey(string? key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var trimmed = key.Trim();

        if (trimmed.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Detail key '{0}' exceeds {1} characters.",
                    trimmed,
                    MaxKeyLength),
                nameof(key));
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Detail key '{trimmed}' may only contain letters, digits, '.', '_', and '-'."),
                    nameof(key));
            }
        }

        return trimmed;
    }

    private static string NormaliseValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var stripped = StripControlCharacters(value);

        return stripped.Length <= MaxValueLength
            ? stripped
            : string.Concat(stripped.AsSpan(0, MaxValueLength), TruncationMarker);
    }

    private static string StripControlCharacters(string value)
    {
        var needsRewrite = false;

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                needsRewrite = true;
                break;
            }
        }

        if (!needsRewrite)
        {
            return value;
        }

        // Control characters would allow log and header injection, so they are replaced rather
        // than removed: the length change stays visible in the recorded value.
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                destination[index] = char.IsControl(character) ? ' ' : character;
            }
        });
    }
}
