using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentSplice.ArchitectureTests;

/// <summary>
/// Domain shape rules that back the observability guarantees.
/// </summary>
/// <remarks>
/// Timeline observations and measurements are evidence. If a property could be reassigned after the
/// fact, "immutable, sequence-ordered" (docs/SPECIFICATION.md FR-TRACE-004) would be a convention
/// rather than a guarantee, and a persisted trace could be edited by any later code path.
/// </remarks>
public sealed class DomainImmutabilityTests
{
    private static readonly Assembly Domain = typeof(AgentSplice.Domain.AssemblyMarker).Assembly;

    [Fact]
    public void No_recorded_observation_or_measurement_type_exposes_a_public_setter()
    {
        string[] evidenceNamespaces =
        [
            "AgentSplice.Domain.Observations",
            "AgentSplice.Domain.Measurements",
            "AgentSplice.Domain.Exchanges",
        ];

        foreach (var type in Domain.GetTypes().Where(type => type.IsPublic && !type.IsEnum))
        {
            if (!evidenceNamespaces.Contains(type.Namespace, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = property.GetSetMethod(nonPublic: false);

                Assert.True(
                    setter is null || setter.IsInitOnly(),
                    FormattableString.Invariant(
                        $"'{type.Name}.{property.Name}' has a public setter. Recorded evidence must not be mutable after construction."));
            }
        }
    }

    [Fact]
    public void Every_public_domain_type_is_sealed_abstract_or_static()
    {
        foreach (var type in Domain.GetTypes().Where(type => type.IsPublic && type.IsClass))
        {
            Assert.True(
                type.IsSealed || type.IsAbstract,
                FormattableString.Invariant(
                    $"'{type.Name}' should be sealed. Open domain types invite behaviour to be added by inheritance instead of by an explicit adapter."));
        }
    }

    [Fact]
    public void Domain_declares_no_static_mutable_state()
    {
        foreach (var type in Domain.GetTypes())
        {
            // Closure caches and other compiler-generated statics are an implementation detail of
            // lambdas, not authored state.
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsLiteral || field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                Assert.True(
                    field.IsInitOnly,
                    FormattableString.Invariant(
                        $"'{type.Name}.{field.Name}' is mutable static state, which CLAUDE.md forbids."));
            }
        }
    }
}

/// <summary>Reflection helper for detecting <c>init</c>-only setters.</summary>
internal static class PropertySetterExtensions
{
    /// <summary>
    /// True when a setter is <c>init</c>-only, which the compiler marks with
    /// <c>IsExternalInit</c> rather than with a distinct accessor kind.
    /// </summary>
    internal static bool IsInitOnly(this MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
