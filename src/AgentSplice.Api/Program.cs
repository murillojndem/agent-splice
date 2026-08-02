using AgentSplice.Api.Endpoints;
using AgentSplice.Api.Hosting;
using AgentSplice.Infrastructure.Composition;
using AgentSplice.Infrastructure.Configuration;
using AgentSplice.Infrastructure.Persistence;
using AgentSplice.Observability;
using AgentSplice.Protocols.OpenAI;
using AgentSplice.Providers.LmStudio;

var builder = WebApplication.CreateBuilder(args);

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

// Last resort only. The gateways translate their own faults into an error carrying correlation
// identifiers; this exists so that a fault escaping the pipeline still produces the stable envelope
// rather than a framework error page that could disclose a stack trace.
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();

// Model discovery and completions, streamed or buffered.
app.MapOpenAiCompatibilityEndpoints();

// The administrative surface that reads what the store retained. Nothing here answers with a
// placeholder: a deployment that retains nothing says so rather than returning an empty page, because
// "no exchanges are stored" and "no exchanges happened" are different facts.
app.MapAdministrativeEndpoints();

await app.RunAsync().ConfigureAwait(false);
