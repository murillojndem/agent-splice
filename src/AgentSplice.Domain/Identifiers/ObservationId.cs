using System.Globalization;

namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Identity of a single immutable timeline observation (docs/SPECIFICATION.md section 13.4).
/// </summary>
public readonly record struct ObservationId
{
    private ObservationId(Guid value) => Value = value;

    /// <summary>The underlying value. Never <see cref="Guid.Empty"/> for a constructed identifier.</summary>
    public Guid Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a fresh observation identity.</summary>
    public static ObservationId New() => new(Guid.NewGuid());

    /// <summary>Rehydrates an observation identity from persistence.</summary>
    public static ObservationId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An observation identifier must not be empty.", nameof(value));
        }

        return new ObservationId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
