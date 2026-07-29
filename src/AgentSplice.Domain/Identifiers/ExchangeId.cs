using System.Globalization;

namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Identity of a single completion exchange (docs/SPECIFICATION.md FR-TRACE-001).
/// </summary>
public readonly record struct ExchangeId
{
    private ExchangeId(Guid value) => Value = value;

    /// <summary>The underlying value. Never <see cref="Guid.Empty"/> for a constructed identifier.</summary>
    public Guid Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a fresh exchange identity.</summary>
    public static ExchangeId New() => new(Guid.NewGuid());

    /// <summary>Rehydrates an exchange identity, typically from persistence or an administrative API.</summary>
    public static ExchangeId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An exchange identifier must not be empty.", nameof(value));
        }

        return new ExchangeId(value);
    }

    /// <summary>Parses the canonical dashed form.</summary>
    public static ExchangeId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return From(Guid.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
