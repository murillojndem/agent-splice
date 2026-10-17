using System.Text;

namespace AgentSplice.ContractTests.Documents;

/// <summary>How the gateway spells a domain enum's members on the wire: the member name in snake_case.</summary>
/// <remarks>
/// One spelling for every test that compares a published value with the domain, so a document checked
/// against the OpenAPI enums and evidence checked against the domain cannot drift apart.
/// </remarks>
internal static class WireNames
{
    /// <summary>Every member of an enum, as the wire spells it.</summary>
    internal static IReadOnlySet<string> Of<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Select(Of).ToHashSet(StringComparer.Ordinal);

    /// <summary>One PascalCase name in snake_case.</summary>
    internal static string Of(string pascalCase)
    {
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
