using AgentSplice.Api.Endpoints;
using AgentSplice.Api.Hosting;
using AgentSplice.Infrastructure.Composition;
using AgentSplice.Infrastructure.Configuration;
using AgentSplice.Protocols.OpenAI;
using AgentSplice.Providers.LmStudio;

var builder = WebApplication.CreateBuilder(args);

// Loopback-only unless an operator says otherwise. Applied here rather than in appsettings.json so
// that ASPNETCORE_URLS and container port settings still win; see LoopbackBindingDefault.
if (LoopbackBindingDefault.ShouldApply(builder.Configuration))
{
    builder.WebHost.UseUrls(LoopbackBindingDefault.Urls);
}

// TimeProvider is injected rather than used statically so that timing observations are
// deterministic under test (CLAUDE.md: "Use TimeProvider for time-dependent logic").
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentSpliceConfiguration(builder.Configuration);
builder.Services.AddAgentSpliceRequestPath();
builder.Services.AddOpenAiCompatibilityProtocol();
builder.Services.AddLmStudioProvider();

// Last resort only. The gateways translate their own faults into an error carrying correlation
// identifiers; this exists so that a fault escaping the pipeline still produces the stable envelope
// rather than a framework error page that could disclose a stack trace.
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

// Stage 1A serves model discovery. POST /v1/chat/completions arrives with the next slice and the
// /api/v1 administrative surface with Stage 1C (docs/ROADMAP.md). Nothing is mapped before it can
// answer honestly: a placeholder response would let a client mistake an unimplemented gateway for a
// working one, which the "an HTTP 200 result must never be recorded as proof of full compatibility"
// rule in CLAUDE.md exists to prevent.
app.MapOpenAiCompatibilityEndpoints();

await app.RunAsync().ConfigureAwait(false);
