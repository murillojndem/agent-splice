using System.Globalization;

namespace AgentSplice.Domain.Measurements;

/// <summary>
/// Validation for measurement names, which become metric and API dimensions
/// (docs/SPECIFICATION.md FR-OBS-006).
/// </summary>
internal static class MeasurementNameGuard
{
    internal const int MaxLength = 96;

    internal static string Require(string? name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        var trimmed = name.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "A measurement name must be {0} characters or fewer.",
                    MaxLength),
                parameterName);
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_'))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Measurement name '{trimmed}' may only contain letters, digits, '.', and '_'."),
                    parameterName);
            }
        }

        return trimmed;
    }
}
