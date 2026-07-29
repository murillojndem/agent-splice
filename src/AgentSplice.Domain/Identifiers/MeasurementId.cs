using System.Globalization;

namespace AgentSplice.Domain.Identifiers;

/// <summary>
/// Identity of a single measurement (docs/SPECIFICATION.md section 13.5).
/// </summary>
public readonly record struct MeasurementId
{
    private MeasurementId(Guid value) => Value = value;

    /// <summary>The underlying value. Never <see cref="Guid.Empty"/> for a constructed identifier.</summary>
    public Guid Value { get; }

    /// <summary>True when this instance is <c>default</c> and therefore carries no identity.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a fresh measurement identity.</summary>
    public static MeasurementId New() => new(Guid.NewGuid());

    /// <summary>Rehydrates a measurement identity from persistence.</summary>
    public static MeasurementId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A measurement identifier must not be empty.", nameof(value));
        }

        return new MeasurementId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
