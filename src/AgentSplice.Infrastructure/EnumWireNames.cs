using System.Text;

namespace AgentSplice.Infrastructure;

/// <summary>
/// Renders a domain enum member as the snake_case token the published contracts use.
/// </summary>
/// <remarks>
/// <c>openapi/agentsplice-openapi.yaml</c> declares closed snake_case enumerations for content
/// retention state, measurement provenance, runtime health, model resolution source, and capability
/// provenance, and a contract test asserts each one matches the domain member names exactly. Deriving
/// the wire token from the member name here is what makes that test meaningful: a hand-maintained
/// lookup would drift the moment a member is added, and the drift would surface as a client that
/// cannot deserialise a value rather than as a failing build.
/// </remarks>
internal static class EnumWireNames
{
    /// <summary>Converts <c>UpstreamReported</c> to <c>upstream_reported</c>.</summary>
    internal static string ToSnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        ToSnakeCase(value.ToString());

    /// <summary>Converts a PascalCase name to its snake_case wire form.</summary>
    internal static string ToSnakeCase(string pascalCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pascalCase);

        var builder = new StringBuilder(pascalCase.Length + 4);

        for (var index = 0; index < pascalCase.Length; index++)
        {
            var character = pascalCase[index];

            if (index > 0 && char.IsAsciiLetterUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
