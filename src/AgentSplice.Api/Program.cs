using AgentSplice.Api.Endpoints;
using AgentSplice.Api.Hosting;
using AgentSplice.Infrastructure.Composition;
using AgentSplice.Infrastructure.Configuration;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.Observability;
using AgentSplice.Protocols.OpenAI;
using AgentSplice.Providers.LmStudio;

// The working directory is the content root, as ASP.NET Core chooses it, except in the self-contained
// packages, whose executable finds its settings and dashboard beside itself wherever it is started
// from (ADR 0017, ADR 0021). Every other build gets exactly CreateBuilder(args).
var builder = WebApplication.CreateBuilder(ContentRootDefault.For(args));

// Loopback-only unless an operator says otherwise. Applied here rather than in appsettings.json so
// that ASPNETCORE_URLS and container port settings still win; see LoopbackBindingDefault.
if (LoopbackBindingDefault.ShouldApply(builder.Configuration))
{
    builder.WebHost.UseUrls(LoopbackBindingDefault.Urls);
}


// A local model can legitimately produce one token every few seconds, which is below Kestrel's
// default minimum response rate of 240 bytes/s. Left at the default, Kestrel aborts the response
// mid-stream, and AgentSplice would record that as a client disconnect — blaming the client for a
// limit the gateway imposed on itself. The runtime's own idle-stream budget is the bound that
// belongs here, and it is configurable per runtime.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MinResponseDataRate = null);

// TimeProvider is injected rather than used statically so that timing observations are
// deterministic under test (CLAUDE.md: "Use TimeProvider for time-dependent logic").
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentSpliceConfiguration(builder.Configuration);
builder.Services.AddAgentSpliceRequestPath();

// The metadata store. Whether it is used at all is decided from validated options when a service is
// resolved, not here: configuration is still being layered while this line runs.
builder.Services.AddAgentSplicePersistence();
builder.Services.AddOpenAiCompatibilityProtocol();
builder.Services.AddLmStudioProvider();
builder.Services.AddAgentSpliceObservability();
builder.Services.AddCompletionConcurrencyLimit();

// Which dashboard build, if any, is served at /dashboard/. Decided when resolved, like everything
// else that reads options (ADR 0015).
builder.Services.AddDashboard();

// Resolved per request by the endpoint filter on the /api/v1 group.
builder.Services.AddSingleton<AdministrationAuthorization>();

// Only requests addressed to the loopback names, unless AllowedHosts names others: a page that
// rebinds its own domain to 127.0.0.1 is otherwise same-origin with the gateway (ADR 0027).
builder.Services.AddLoopbackHostFiltering();

// Last resort only. The gateways translate their own faults into an error carrying correlation
// identifiers; this exists so that a fault escaping the pipeline still produces the stable envelope
// rather than a framework error page that could disclose a stack trace.
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// After Build, and deliberately. builder.Configuration is still being assembled while the composition
// root runs — a host layers its sources as it is built, and the test host adds its overrides through
// ConfigureAppConfiguration — so a guard reading it there can miss the very binding it exists to
// catch. app.Configuration is the finished article, and this still runs before the server accepts
// anything (FR-HEALTH-006, NFR 14.2).
AdministrationBindingGuard.Verify(app.Configuration);

// The token the guard requires protects /api/v1 alone; what such a binding exposes is said once,
// where an operator reads startup.
NetworkExposureNotice.Announce(
    app.Configuration,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NetworkExposureNotice)));

app.UseExceptionHandler();
app.UseRateLimiter();

// The dashboard on the gateway's own origin, confined to /dashboard so that no other route answers
// differently because of it; plus a redirect from / while it is served. Nothing at all when it is
// disabled or no build exists — the gateway is complete without it (FR-DASH-002, ADR 0015).
app.MapDashboard();

// Model discovery and completions, streamed or buffered.
app.MapOpenAiCompatibilityEndpoints();

// The administrative surface that reads what the store retained. Nothing here answers with a
// placeholder: a deployment that retains nothing says so rather than returning an empty page, because
// "no exchanges are stored" and "no exchanges happened" are different facts.
app.MapAdministrativeEndpoints();

await app.RunAsync().ConfigureAwait(false);
