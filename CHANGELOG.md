# Changelog

All notable changes will be documented here.

## Unreleased

### Stage 1A — Non-streaming completions

- Added `POST /v1/chat/completions` for non-streaming requests (FR-CHAT-001, FR-CHAT-003,
  FR-CHAT-004, FR-CHAT-009, FR-CHAT-010, FR-CHAT-014, FR-CHAT-015). A standard client can now
  complete a request through AgentSplice.
- Forwarding is byte-preserving. The runtime receives the client's original bytes unless routing
  renames the model, in which case only the bytes of the top-level `model` value are replaced.
  The body is spliced using offsets recorded during the same pass that builds the structural
  summary, rather than reparsed and re-emitted: a JSON writer normalises escape forms and number
  formatting, so `A` would become `A` and `1.0` would become `1`, and an exact-forwarding test
  built on that would only prove that our own parser round-trips.
- Made `ExchangeStatus.Completed` mean the transport cycle finished, not that the operation
  succeeded. A relayed 429 or 500 completes with no failure class, because AgentSplice did not fail;
  the runtime's own status is recorded separately and is what success and error are classified from.
- Added `UpstreamResponseMetadata` to the domain. Status is transport metadata, not body structure,
  and the two come apart exactly when it matters: a 204 has no body, a `429 text/plain` has one that
  is not protocol data, and a truncated 500 has one that cannot be parsed. Attaching status to the
  structural summary would have lost it in all three cases.
- Bounded the cardinality of recorded role names and made every truncation visible. The summary
  already bounded each name's length but not how many distinct names it held, so a request with a
  unique role per message would have grown it without limit — defeating the bound's own rationale.
  Folded roles still sum to the message count, and silent truncation no longer reads as completeness.
- Rejected `stream: true` with a stable message that names no roadmap stage, since the message is a
  public contract that outlives the stage. Buffering an event stream into a JSON body would be an
  invisible semantic transformation, and a `200` would make an unimplemented capability look
  implemented.
- Rejected a repeated `model`, `messages`, or `stream`. "Last wins" can differ between AgentSplice's
  validation, the splice arithmetic, and the runtime's parser, so the three could disagree about
  what was actually sent.
- Added header allowlists in both directions. The client's `Authorization` is never forwarded, no
  hop-by-hop header is copied, and a relayed 429 keeps its `Retry-After` — without which the status
  conveys nothing actionable.
- Built the structural summary *before* resolving the model, so a request naming an unknown model
  still leaves safe evidence of what arrived.
- Added `IExchangeRecordSink` with a discarding default. It is the only way Stage 1A's timeline is
  observable before persistence exists, so the "routing changes are represented as events" exit
  criterion would otherwise be untestable, and it is the seam Stage 1C implements.
- Fixed a Stage 0 validator defect: an identity alias, mapping a model name to itself on a chosen
  runtime, was rejected as a resolution cycle. The resolver does not chain alias to alias, so it
  terminates immediately — and an identity alias is the only way an operator can pin a model to one
  runtime when two offer it, so rejecting it removed the sole deterministic override of the
  FR-MOD-004 tie-break. Multi-alias cycles are still rejected.
- Capped integration-suite parallelism. Nearly every test binds a real listener and boots a host, and
  unbounded parallelism made the suite compete with itself, which surfaced as a startup test failing
  for a reason other than the one it asserts.

### Stage 1A — Model discovery and routing

- Added `GET /v1/models`, composing configured aliases with models discovered from enabled runtimes
  (FR-MOD-001, FR-MOD-002). The first HTTP surface the gateway serves.
- Added deterministic model resolution (FR-MOD-005): an enabled alias, then a discovered model, then
  an optional configured pass-through runtime. Aliases resolve without any network call, so an
  alias-only deployment never pays for discovery on the request path.
- Distinguished "the catalogue was consulted and the model is absent" from "no runtime could be
  asked". The first is a 404, the second a 502. Reporting a model as missing when the truth is that
  AgentSplice could not ask is precisely the misleading evidence this product exists to remove.
- Separated *a routing decision was made* from *the forwarded body must change*. An alias that
  selects a runtime without renaming the model, a duplicate identifier resolved by tie-break, and a
  pass-through are all routing decisions FR-TRACE-007 requires to be visible, and none of them
  changes a byte. `ModelResolution.IsRoutingChange` answers only the second question.
- Added per-runtime discovery caching with the configured window, the stale-serve policy, and
  refresh coalescing (FR-MOD-003). A failed refresh is remembered for the same window: without that,
  every request naming an unknown model would wait out the connect timeout again while a runtime is
  down. A failed refresh never destroys the catalogue it failed to replace.
- Made model identifiers opaque. They previously had to match a punctuation allowlist, which would
  have rejected values a runtime would have accepted and made AgentSplice the source of a failure
  that does not exist downstream. Validation now bounds length and rejects only control characters
  and text that cannot be encoded as UTF-8.
- Kept creation times honest. `created` is a Unix timestamp, so zero is a claim about 1970 rather
  than a way of saying "unknown". The catalogue holds `null`, an alias inherits the evidence of the
  model it targets, and the compatibility sentinel the OpenAI schema forces exists only inside the
  response writer.
- Added `agentsplice:defaultRuntimeId`, validated to name a configured *enabled* runtime, which
  makes `ModelResolutionSource.PassThrough` reachable and keeps a discovery-disabled runtime
  routable. Unset by default.
- Added `agentsplice:limits`, bounding request, completion, and catalogue bodies. Mandatory because
  the non-streaming path is deliberately fully buffered, which without a ceiling turns one defective
  runtime into gateway-wide memory pressure.
- Added the LM Studio provider with one named HTTP client per runtime. `ConnectTimeout` is a
  property of the handler while `timeouts:connect` is configured per runtime, so a shared handler
  could honour only one runtime's budget; per-runtime clients also isolate connection pools.
  `HttpClient.Timeout` is infinite, because the 100-second default throws a cancellation
  indistinguishable from a client disconnect and would make timeout-phase attribution impossible.
- Contained upstream credentials. They are resolved inside the provider at the moment the request is
  built rather than carried through orchestration, and `RuntimeCredential.ToString()` returns a
  placeholder so an accidental `{Credential}` in a log template cannot leak a key. Redirects and
  system proxies are disabled so a bearer token cannot be sent to a host the operator did not name.
- Enforced two new boundaries by test: `AgentSplice.Application` may not reference
  `System.Net.Http`, which forces transport classification into the provider; and the API project
  may not parse protocol JSON, record exchange evidence, or open its own connections, which is the
  checkable form of "no orchestration in endpoint lambdas".

Measurements in this slice are taken against the deterministic fake upstream and are fixture
measurements, not hardware claims. Stage 1A makes no compatibility claim; that requires a
conformance report.

### Stage 0 — Repository foundation

- Bootstrapped the .NET 8 solution with the seven Stage 1 production projects and enforced the
  dependency rules from `docs/ARCHITECTURE.md` with architecture tests.
- Pinned the SDK feature band, centralised package versions, promoted warnings to errors, and moved
  formatting enforcement to a separate CI step (ADR 0007).
- Added the Stage 0 domain model: exchanges, append-only timelines, observations with bounded
  sanitised detail, measurements with mandatory provenance, and throughput derivation that returns
  unknown rather than zero when the evidence is insufficient.
- Added typed configuration with startup validation, including duplicate and cyclic alias detection
  (FR-MOD-006), timeout-phase coherence, credential-free runtime URLs, and a guard that refuses to
  start when the Stage 4 adapter flag is enabled.
- Added stable Stage 1 error codes and the normative OpenTelemetry names, both verified against the
  documents that declare them by contract tests.
- Added the deterministic fake upstream runtime: a real loopback Kestrel listener with byte-exact SSE
  scripting, arbitrary chunk boundaries, delayed headers, trailing stalls, premature close, request
  recording, and upstream-side cancellation observation.
- Added Windows and Ubuntu CI, a real multi-stage Dockerfile running as a non-root user, a
  `.dockerignore` that keeps host build output out of the image context, and loopback-bound Compose
  defaults.
- Made the loopback binding default a fallback rather than an `appsettings.json` value. Declared in the
  settings file it layered over `ASPNETCORE_URLS`, so the container bound loopback inside itself and
  the published port mapping was unreachable.
- Corrected the deployment environment variables to the double-underscore form that actually binds to
  the `agentsplice` configuration section, and added a contract test that fails when a variable binds
  to nothing.
- Declared the three Stage 1 administrative endpoints that `docs/API.md` documented but the OpenAPI
  draft omitted.
- Verified the Stage 0 exit criterion: a streamed fake-upstream exchange is fully representable by the
  domain model, with unobserved boundaries staying absent rather than becoming zero.

### Revised product thesis

- Repositioned AgentSplice from a protocol-normalizing gateway to a local-first interoperability, observability, replay, conformance, and evaluation platform.
- Defined trace, replay, conformance, evaluation, regression, and OpenTelemetry export as the durable core.
- Moved tool-call recovery, prompt/schema compaction, protocol translation, model-specific rules, and runtime workarounds to optional later-stage adapters.
- Reordered the roadmap so transparent trace proxying comes first, followed by replay/conformance, agent evaluation, adapters, and ecosystem/backend work.
- Added product-positioning, conformance, replay, and adapter-lifecycle documents.
- Updated architecture, API, security, testing, observability, benchmarking, portfolio, and implementation-agent instructions.
- Expanded the OpenAPI draft with exchange and administrative trace endpoints.

### Original specification pack

- Created specification-first repository pack.
- Defined AgentSplice name, initial architecture, roadmap, API draft, security model, and implementation instructions.
