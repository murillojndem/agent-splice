using AgentSplice.Api.Hosting;
using AgentSplice.Infrastructure.Configuration;

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

var app = builder.Build();

// Stage 0 is the repository foundation: the host boots and validates its configuration, and
// deliberately exposes no HTTP surface. GET /v1/models and POST /v1/chat/completions arrive with
// Stage 1A, and the /api/v1 administrative surface with Stage 1C (docs/ROADMAP.md).
// Serving a placeholder response would let a client mistake an unimplemented gateway for a
// working one, which the "an HTTP 200 result must never be recorded as proof of full
// compatibility" rule in CLAUDE.md exists to prevent.

await app.RunAsync().ConfigureAwait(false);
