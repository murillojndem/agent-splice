using AgentSplice.Application.Runtimes;
using Xunit;

namespace AgentSplice.UnitTests.Runtimes;

/// <summary>
/// Credential containment (docs/SPECIFICATION.md FR-DATA-010, docs/SECURITY.md).
/// </summary>
public sealed class RuntimeCredentialTests
{
    private const string Secret = "sk-do-not-log-this-value";

    [Fact]
    public void To_string_never_reveals_the_key()
    {
        // The property that matters: an accidental {Credential} in a log template must emit a
        // placeholder, not the key.
        var credential = RuntimeCredential.FromValue(Secret);

        Assert.DoesNotContain(Secret, credential.ToString(), StringComparison.Ordinal);
        Assert.Equal(RuntimeCredential.RedactedRendering, credential.ToString());
    }

    [Fact]
    public void An_interpolated_credential_renders_as_redacted()
    {
        var credential = RuntimeCredential.FromValue(Secret);

        Assert.DoesNotContain(Secret, $"bearer {credential}", StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_credential_renders_distinctly_from_a_redacted_one()
    {
        // "No key is configured" and "a key exists but is hidden" are different operational facts.
        Assert.Equal(RuntimeCredential.AbsentRendering, RuntimeCredential.None.ToString());
        Assert.NotEqual(RuntimeCredential.RedactedRendering, RuntimeCredential.None.ToString());
    }

    [Fact]
    public void Reveal_returns_the_value_at_the_single_deliberate_disclosure_point()
    {
        Assert.Equal(Secret, RuntimeCredential.FromValue(Secret).Reveal());
    }

    [Fact]
    public void Revealing_an_absent_credential_fails_rather_than_returning_an_empty_bearer_token()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeCredential.None.Reveal());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_environment_value_yields_no_credential(string? value)
    {
        // An empty bearer token would be sent upstream and rejected, reporting an authentication
        // failure whose real cause is an unset variable.
        var credential = RuntimeCredential.FromValue(value);

        Assert.False(credential.HasValue);
        Assert.Same(RuntimeCredential.None, credential);
    }

    [Fact]
    public void A_resolved_credential_reports_that_it_has_a_value()
    {
        Assert.True(RuntimeCredential.FromValue(Secret).HasValue);
    }
}
