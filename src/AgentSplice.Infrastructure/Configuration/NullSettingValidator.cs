using System.Reflection;
using AgentSplice.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Configuration;

/// <summary>
/// Refuses a switch, a number, a duration, or a mode given as null (NFR 14.2, ADR 0024).
/// </summary>
/// <remarks>
/// Since .NET 10 the configuration binder writes a null it finds into the property, where it used to
/// skip it, and JSON's <c>null</c> reaches it as a null, where it used to arrive as an empty string.
/// For a value type that is the type's zero value: <c>"mode": null</c> binds
/// <see cref="PersistenceMode.None"/> and <c>"metadataEnabled": null</c> binds <c>false</c>, so either
/// one quietly retains nothing, and the result is a valid configuration that
/// <see cref="AgentSpliceOptionsValidator"/> has no reason to refuse. On .NET 8 the same file failed
/// startup, because no switch or mode converts from an empty string. This keeps that failure, and
/// names the setting.
///
/// Read from the configuration rather than from the bound options, because once bound a null is
/// indistinguishable from a <c>false</c> or a <c>0</c> someone meant. Strings are left alone: a null
/// one is the setting's absence, which is what every optional string already means by it, and a
/// required one is refused as empty by the options validator. A section or a list given as null keeps
/// its defaults, as it always has.
/// </remarks>
internal sealed class NullSettingValidator : IValidateOptions<AgentSpliceOptions>
{
    private readonly IConfiguration section;

    /// <summary>Creates the validator over the configuration section the options are bound from.</summary>
    internal NullSettingValidator(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        this.section = section;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentSpliceOptions options)
    {
        var failures = NullValues(section, typeof(AgentSpliceOptions))
            .Select(path => FormattableString.Invariant(
                $"{path} is null. Remove the setting to use its default, or give it a value; a null would be read as false, zero, or the first of a mode's values, not as the default."))
            .ToList();

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// The path of every setting under <paramref name="section"/> that is null where
    /// <paramref name="type"/> binds a value type. A key no property matches is not this class's to judge.
    /// </summary>
    private static IEnumerable<string> NullValues(IConfiguration section, Type type) =>
        section.GetChildren().SelectMany(setting =>
            type.GetProperty(setting.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is { } property
                ? NullValuesIn(setting, property.PropertyType)
                : []);

    private static IEnumerable<string> NullValuesIn(IConfigurationSection setting, Type type)
    {
        if (type.IsValueType)
        {
            // A value type that can hold null takes one as a value.
            return Nullable.GetUnderlyingType(type) is null && setting.Value is null && !setting.GetChildren().Any()
                ? [setting.Path]
                : [];
        }

        if (type == typeof(string))
        {
            return [];
        }

        // A list's entries are its children, keyed by index.
        return ElementType(type) is { } element
            ? setting.GetChildren().SelectMany(entry => NullValuesIn(entry, element))
            : NullValues(setting, type);
    }

    private static Type? ElementType(Type type) =>
        type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
}
