using System.Globalization;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace AgentSplice.Application.Configuration;

/// <summary>
/// Startup validation for <see cref="AgentSpliceOptions"/>
/// (docs/SPECIFICATION.md FR-MOD-006, NFR 14.2 "Configuration is validated before readiness").
/// </summary>
/// <remarks>
/// Every rule here exists because the alternative is a silent misconfiguration that only surfaces as
/// a confusing runtime failure: an alias pointing at a runtime that no longer exists, two runtimes
/// sharing an ID so routing becomes order-dependent, content capture enabled with nowhere to store
/// it, or an adapter flag that an operator believes is doing something.
/// </remarks>
public sealed class AgentSpliceOptionsValidator : IValidateOptions<AgentSpliceOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentSpliceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePublicBaseUrl(options, failures);
        ValidatePersistence(options, failures);
        ValidateDiagnostics(options, failures);
        ValidateCapture(options, failures);
        ValidateAdapters(options, failures);
        ValidateLimits(options, failures);

        var enabledRuntimeIds = ValidateRuntimes(options, failures);
        ValidateAliases(options, enabledRuntimeIds.All, failures);
        ValidateDefaultRuntime(options, enabledRuntimeIds, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePublicBaseUrl(AgentSpliceOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return;
        }

        if (!TryParseHttpUri(options.PublicBaseUrl, out _))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"agentsplice:publicBaseUrl '{options.PublicBaseUrl}' must be an absolute http or https URL."));
        }
    }

    private static void ValidatePersistence(AgentSpliceOptions options, List<string> failures)
    {
        var persistence = options.Persistence;

        if (!Enum.IsDefined(persistence.Mode))
        {
            failures.Add("agentsplice:persistence:mode is not a recognised persistence mode.");
        }

        if (persistence.Mode != PersistenceMode.None && string.IsNullOrWhiteSpace(persistence.ConnectionString))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"agentsplice:persistence:connectionString is required when the mode is '{persistence.Mode}'."));
        }

        if (persistence.MetadataQueueCapacity <= 0)
        {
            failures.Add(
                "agentsplice:persistence:metadataQueueCapacity must be greater than zero; an unbounded queue would let persistence back-pressure grow without limit.");
        }
    }

    private static void ValidateDiagnostics(AgentSpliceOptions options, List<string> failures)
    {
        var diagnostics = options.Diagnostics;

        if (!Enum.IsDefined(diagnostics.StoreHeaders))
        {
            failures.Add("agentsplice:diagnostics:storeHeaders is not a recognised header capture mode.");
        }

        if (diagnostics.StoreHeaders != HeaderCaptureMode.Allowlist)
        {
            return;
        }

        for (var index = 0; index < diagnostics.HeaderAllowlist.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(diagnostics.HeaderAllowlist[index]))
            {
                failures.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "agentsplice:diagnostics:headerAllowlist[{0}] must not be blank.",
                        index));
            }
        }
    }

    private static void ValidateCapture(AgentSpliceOptions options, List<string> failures)
    {
        var capture = options.Capture;

        if (capture.Retention.Metadata <= TimeSpan.Zero)
        {
            failures.Add("agentsplice:capture:retention:metadata must be greater than zero.");
        }

        if (capture.Retention.Content <= TimeSpan.Zero)
        {
            failures.Add("agentsplice:capture:retention:content must be greater than zero.");
        }

        if (!capture.ContentEnabled)
        {
            return;
        }

        // Content capture is the one setting that moves prompts and model output out of process
        // memory. If it is on, it has to be on deliberately and completely.
        if (options.Persistence.Mode == PersistenceMode.None)
        {
            failures.Add(
                "agentsplice:capture:contentEnabled is true but agentsplice:persistence:mode is 'None'; content capture requires a configured store.");
        }

        if (!capture.MetadataEnabled)
        {
            failures.Add(
                "agentsplice:capture:contentEnabled is true but agentsplice:capture:metadataEnabled is false; content without its exchange metadata is not attributable.");
        }
    }

    private static void ValidateAdapters(AgentSpliceOptions options, List<string> failures)
    {
        if (options.Adapters.Enabled)
        {
            failures.Add(
                "agentsplice:adapters:enabled must be false. Compatibility adapters are a Stage 4 capability (docs/ROADMAP.md); enabling the flag now would report transformations that do not run.");
        }
    }

    private static RuntimeIdentifierSet ValidateRuntimes(AgentSpliceOptions options, List<string> failures)
    {
        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
        var enabledRuntimeIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < options.Runtimes.Count; index++)
        {
            var runtime = options.Runtimes[index];
            var prefix = string.Format(CultureInfo.InvariantCulture, "agentsplice:runtimes[{0}]", index);

            if (!RuntimeEndpointId.TryCreate(runtime.Id, out var runtimeId))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:id '{runtime.Id}' is not a valid runtime identifier ({IdentifierDescriptions.Slug})."));
            }
            else if (!runtimeIds.Add(runtimeId.Value))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:id '{runtimeId.Value}' is declared more than once; runtime identifiers must be unique so that routing is deterministic."));
            }
            else if (runtime.Enabled)
            {
                enabledRuntimeIds.Add(runtimeId.Value);
            }

            if (string.IsNullOrWhiteSpace(runtime.Provider))
            {
                failures.Add(FormattableString.Invariant($"{prefix}:provider is required."));
            }

            ValidateRuntimeBaseUrl(runtime, prefix, failures);
            ValidateRuntimeApiKeyReference(runtime, prefix, failures);
            ValidateDiscovery(runtime.Discovery, prefix, failures);
            ValidateTimeouts(runtime.Timeouts, prefix, failures);
        }

        return new RuntimeIdentifierSet(runtimeIds, enabledRuntimeIds);
    }

    /// <summary>
    /// Validates the optional pass-through target.
    /// </summary>
    /// <remarks>
    /// The target must be enabled, not merely configured. A default runtime that is switched off
    /// would make every unrecognised model fail at request time with a routing error that names a
    /// runtime the operator believes is in use, which is harder to diagnose than a startup failure
    /// naming the setting.
    /// </remarks>
    private static void ValidateDefaultRuntime(
        AgentSpliceOptions options,
        RuntimeIdentifierSet runtimeIds,
        List<string> failures)
    {
        if (options.DefaultRuntimeId is null)
        {
            return;
        }

        if (!RuntimeEndpointId.TryCreate(options.DefaultRuntimeId, out var defaultRuntimeId))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"agentsplice:defaultRuntimeId '{options.DefaultRuntimeId}' is not a valid runtime identifier ({IdentifierDescriptions.Slug})."));
            return;
        }

        if (!runtimeIds.All.Contains(defaultRuntimeId.Value))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"agentsplice:defaultRuntimeId '{defaultRuntimeId.Value}' does not match any configured runtime."));
            return;
        }

        if (!runtimeIds.Enabled.Contains(defaultRuntimeId.Value))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"agentsplice:defaultRuntimeId '{defaultRuntimeId.Value}' names a runtime that is not enabled; pass-through routing would never reach it."));
        }
    }

    private static void ValidateLimits(AgentSpliceOptions options, List<string> failures)
    {
        var limits = options.Limits;

        var bounds = new (string Name, long Value)[]
        {
            ("maxRequestBodyBytes", limits.MaxRequestBodyBytes),
            ("maxUpstreamCompletionBodyBytes", limits.MaxUpstreamCompletionBodyBytes),
            ("maxCatalogueBodyBytes", limits.MaxCatalogueBodyBytes),
        };

        foreach (var (name, value) in bounds)
        {
            if (value <= 0)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"agentsplice:limits:{name} must be greater than zero; an unbounded body would let a single request exhaust process memory."));
            }
        }
    }

    /// <summary>Configured runtime identifiers, split by whether the runtime participates in routing.</summary>
    private readonly record struct RuntimeIdentifierSet(
        HashSet<string> All,
        HashSet<string> Enabled);

    private static void ValidateRuntimeBaseUrl(
        RuntimeEndpointOptions runtime,
        string prefix,
        List<string> failures)
    {
        if (!TryParseHttpUri(runtime.BaseUrl, out var baseUri))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"{prefix}:baseUrl '{runtime.BaseUrl}' must be an absolute http or https URL."));
            return;
        }

        // Credentials in a URL would be copied into every trace and replay target. Rejecting the
        // shape outright is simpler than redacting it everywhere afterwards (FR-DATA-010).
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"{prefix}:baseUrl must not embed credentials; reference an environment variable through apiKeyEnvironmentVariable instead."));
        }
    }

    private static void ValidateRuntimeApiKeyReference(
        RuntimeEndpointOptions runtime,
        string prefix,
        List<string> failures)
    {
        if (runtime.ApiKeyEnvironmentVariable is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.ApiKeyEnvironmentVariable))
        {
            failures.Add(
                FormattableString.Invariant(
                    $"{prefix}:apiKeyEnvironmentVariable must name an environment variable or be omitted."));
            return;
        }

        foreach (var character in runtime.ApiKeyEnvironmentVariable)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:apiKeyEnvironmentVariable '{runtime.ApiKeyEnvironmentVariable}' is not a valid environment variable name. This setting holds a variable name, never the key itself."));
                return;
            }
        }
    }

    private static void ValidateDiscovery(DiscoveryOptions discovery, string prefix, List<string> failures)
    {
        if (discovery.CacheDuration < TimeSpan.Zero)
        {
            failures.Add(
                FormattableString.Invariant($"{prefix}:discovery:cacheDuration must not be negative."));
        }
    }

    private static void ValidateTimeouts(TimeoutOptions timeouts, string prefix, List<string> failures)
    {
        var phases = new (string Name, TimeSpan Value)[]
        {
            ("connect", timeouts.Connect),
            ("responseHeaders", timeouts.ResponseHeaders),
            ("idleStream", timeouts.IdleStream),
            ("total", timeouts.Total),
        };

        foreach (var (phaseName, value) in phases)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add(
                    FormattableString.Invariant($"{prefix}:timeouts:{phaseName} must be greater than zero."));
            }
        }

        // A total budget below a phase budget makes the phase unreachable, so the phase that
        // actually fired could never be reported accurately (FR-CHAT-008).
        if (timeouts.Total > TimeSpan.Zero)
        {
            if (timeouts.Connect > timeouts.Total)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:timeouts:connect must not exceed timeouts:total."));
            }

            if (timeouts.ResponseHeaders > timeouts.Total)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:timeouts:responseHeaders must not exceed timeouts:total."));
            }

            if (timeouts.IdleStream > timeouts.Total)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:timeouts:idleStream must not exceed timeouts:total."));
            }
        }
    }

    private static void ValidateAliases(
        AgentSpliceOptions options,
        HashSet<string> runtimeIds,
        List<string> failures)
    {
        var aliasIds = new HashSet<string>(StringComparer.Ordinal);
        var aliasTargets = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < options.Aliases.Count; index++)
        {
            var alias = options.Aliases[index];
            var prefix = string.Format(CultureInfo.InvariantCulture, "agentsplice:aliases[{0}]", index);

            var aliasIdValid = ModelAliasId.TryCreate(alias.Id, out var aliasId);

            if (!aliasIdValid)
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:id '{alias.Id}' is not a valid alias identifier ({IdentifierDescriptions.Model})."));
            }
            else if (!aliasIds.Add(aliasId.Value))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:id '{aliasId.Value}' is declared more than once; FR-MOD-006 requires duplicate aliases to fail validation."));
            }

            if (!RuntimeEndpointId.TryCreate(alias.RuntimeId, out var aliasRuntimeId))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:runtimeId '{alias.RuntimeId}' is not a valid runtime identifier."));
            }
            else if (!runtimeIds.Contains(aliasRuntimeId.Value))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:runtimeId '{aliasRuntimeId.Value}' does not match any configured runtime."));
            }

            if (!UpstreamModelId.TryCreate(alias.UpstreamModelId, out var upstreamModelId))
            {
                failures.Add(
                    FormattableString.Invariant(
                        $"{prefix}:upstreamModelId '{alias.UpstreamModelId}' is not a valid model identifier ({IdentifierDescriptions.Model})."));
            }
            else if (aliasIdValid)
            {
                aliasTargets[aliasId.Value] = upstreamModelId.Value;
            }
        }

        DetectAliasCycles(aliasTargets, failures);
    }

    /// <summary>
    /// Detects alias chains that never terminate at a real upstream model.
    /// </summary>
    /// <remarks>
    /// An alias whose upstream model identifier is itself an alias forms a chain. If that chain
    /// loops, resolution would either recurse forever or resolve differently depending on iteration
    /// order. FR-MOD-006 requires it to fail validation instead.
    /// </remarks>
    private static void DetectAliasCycles(Dictionary<string, string> aliasTargets, List<string> failures)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in aliasTargets.Keys)
        {
            if (reported.Contains(start))
            {
                continue;
            }

            var visited = new List<string> { start };
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var current = start;

            while (aliasTargets.TryGetValue(current, out var next))
            {
                if (!seen.Add(next))
                {
                    visited.Add(next);

                    failures.Add(
                        FormattableString.Invariant(
                            $"agentsplice:aliases contains a resolution cycle: {string.Join(" -> ", visited)}. FR-MOD-006 requires alias cycles to fail validation."));

                    foreach (var member in visited)
                    {
                        reported.Add(member);
                    }

                    break;
                }

                visited.Add(next);
                current = next;
            }
        }
    }

    private static bool TryParseHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = default!;
        return false;
    }

    private static class IdentifierDescriptions
    {
        internal const string Slug = "letters, digits, '-', '_', and '.'";

        /// <summary>
        /// Mirrors <c>IdentifierText.OpaqueRule</c>. Model identifiers are opaque third-party values,
        /// so the constraint is length and printability rather than a punctuation allowlist.
        /// </summary>
        internal const string Model = "non-blank text of at most 256 characters with no control characters";
    }
}
