# Changelog

All notable changes will be documented here.

## Unreleased

### Stage 1B — Streaming correctness and timeline

- Added streaming completions (FR-STR-001 to FR-STR-012). A `stream: true` request is forwarded with
  `Accept: text/event-stream` and the runtime's answer is relayed byte for byte as it arrives.
- Relayed raw bytes rather than decoded events. Each chunk is written and flushed to the client
  before anything decodes it, so no evidence-gathering work can become a flush delay, and valid SSE
  holds by construction: split events, split UTF-8 sequences, CRLF, multi-line `data`, comments, and
  keepalives all reach the client unchanged because nothing rebuilt them. Decoding and re-emitting
  would have normalised escape forms and number formatting, exactly as re-emitting a request document
  would have.
- Separated framing from meaning. The frame reader knows where an event begins and ends; the protocol
  module knows what `[DONE]` is and which chunk first carried output. SSE is a grammar shared by more
  than one protocol, and fusing the two would make the next protocol a rewrite rather than an
  implementation.
- Made the first-token boundary honest. A first chunk announcing `role: assistant` sets
  `FirstDecodedEvent` and deliberately not `FirstSemanticEvent`, because time to first token is not
  time to first chunk — the same class of error as labelling prompt throughput as generation
  throughput.
- Fixed a live Stage 1A defect: every timeline boundary was stamped when control returned to the
  orchestrator, so `agentsplice.time_to_headers` was measuring time until the whole body had been
  read. Boundaries are now stamped from the moment they were observed. A plausible, published, wrong
  metric is worse than an absent one.
- Distinguished what the client asked for from what it was served. A `stream: true` request answered
  with a buffered JSON body never streamed, and requiring it to record how its stream ended would
  have fabricated evidence. The exchange records `upstream.streamed = false` and is summarised
  exactly as it would have been without the flag, so asking to stream never costs evidence.
- Classified every way a stream can end: normal completion, the protocol terminator, client
  cancellation, timeout with its phase, a malformed event, a lost connection, and a gateway bound.
  `LimitExceeded` is deliberately distinct from `MalformedEvent` — one is the runtime's behaviour and
  the other AgentSplice's own policy, and reporting the second as the first misattributes a gateway
  decision to the runtime.
- Made a malformed payload an observation rather than a failure. It is relayed verbatim and the
  exchange completes with no failure class, because the client's own parser is the authority on the
  runtime's protocol. A bound violation or a lost connection abandons the response instead: once the
  status is committed, a stream that stops early but closes cleanly is indistinguishable from a
  complete one.
- Wired the idle-stream timeout, configured and validated since Stage 0 and unreachable until now.
  The budget is re-armed per read rather than allocated per read, and the response-header budget is
  disarmed once headers arrive — left armed, its token fires during every long stream, and every
  mid-stream stall would have been reported as a runtime slow to answer.
- Added `limits:maxStreamEventBytes` and `limits:maxConcurrentCompletions`, closing the concurrency
  debt `docs/SECURITY.md` recorded. The streaming path retains a read buffer plus one event under
  assembly and nothing else, which makes the memory ceiling a documented product of two settings
  rather than a function of response length. Over the limit a request is refused with `429` rather
  than queued: a queue turns an overload into unbounded latency, which an agent loop cannot act on.
- Added six instruments and a `stream.termination` dimension attached only to exchanges that
  streamed, so buffered series keep their cardinality. Generation throughput is derived over the
  observed decode window and its bias is documented rather than corrected away. Prompt throughput is
  absent by design, not deferred: nothing observable marks the end of prompt processing, so the only
  available interval measures the prompt, the queue, and the network together.
- Gave `agentsplice.stream` a real span and added a contract test that every activity source the
  listener subscribes to has something writing to it. The OpenTelemetry SDK moves to Stage 1C, where
  an exporter has a consumer; three source comments that said 1B were corrected rather than left to
  drift.
- Disabled Kestrel's minimum response data rate. A local model producing one token every few seconds
  falls below the 240 bytes/s default, and Kestrel aborting the response would have been recorded as
  a client disconnect — blaming the client for a limit the gateway imposed on itself.
- Removed `upstream.connect.duration`. No pair of observable boundaries can derive it, and a declared
  name nothing can produce is the defect the previous review slice existed to remove.
- Proved "long streams pass without full buffering" behaviourally: eight megabytes stream successfully
  with the buffered ceiling set to 64 KiB, which can only happen if the streaming path never routes
  through it. `AgentSplice.PerformanceTests` was not created; ADR 0009 records why a wall-clock
  benchmark on shared runners would not have earned a place in CI.
- Added a test-releasable gate to the fake upstream. Chunk delays are real wall-clock waits, so a test
  built on them is either slow or flaky and never both; the streaming, disconnect, and idle-timeout
  suites are now deterministic and run in about a second. Client-side assertions use an independent
  SSE parser, because a test whose parser is the gateway's own proves only self-consistency.
- Added ADR 0009 recording all thirteen Stage 1B decisions, the alternatives rejected, and the two
  stream terminations that remain unreachable and why.
- Measured upstream connection time, which `docs/OBSERVABILITY.md` requires unconditionally and this
  slice had first written off as underivable. It is not: `SocketsHttpHandler.ConnectCallback` exposes
  it, so the provider's handler now opens the socket itself — reproducing the default behaviour
  exactly, since the only reason to take it over is to time it — and stashes the timing on the
  request that triggered the connection, so exactly the request that paid for one is charged for it.
  Recorded as two boundaries and derived like every other phase, and **absent for a request served by
  a pooled connection**: a zero there would claim a connection was opened instantaneously, which is a
  measurement of an event that never happened. Without this phase, a runtime slow to accept
  connections and a runtime slow to think are the same number.
- Cleared pooled buffers that held prompt or model output before returning them. A pooled array
  outlives the exchange that filled it, and content escapes through a later renter that trusts the
  array's length rather than its read count. They are rented once per exchange rather than once per
  read, so this costs a single memset against a stream that ran for seconds.
- Closed three further gaps found by reviewing the slice against the architecture documents.
  `docs/TESTING.md` claimed the duplicate terminal-event family was covered when no test produced
  one. The threat model names excessive nesting among malicious-stream behaviours and nothing
  asserted it, so a five-thousand-deep payload is now proven to be reported as malformed rather than
  overflowing the stack — a failure no catch block could contain. And the error table described a
  `502` for an exceeded stream bound that cannot occur, because the status is committed with the
  response headers before any body byte exists.

Measurements in this slice are taken against the deterministic fake upstream and are fixture
measurements, not hardware claims. Stage 1B makes no compatibility claim; that requires a conformance
report.

### Stage 1A — Review against the architecture documents

A pass over the four Stage 1A slices against `CLAUDE.md`, `docs/ARCHITECTURE.md`,
`docs/SPECIFICATION.md`, `docs/OBSERVABILITY.md`, and `docs/THREAT_MODEL.md`.

- Made the unsupported-field policy explicit, which FR-CHAT-005 requires and Stage 1A only implied.
  `agentsplice:compatibility:unsupportedFields` selects `transparent` (the default, and the reason a
  runtime extension reaches the runtime) or `strict` (reject any top-level field AgentSplice does not
  model, naming it). `adapted` stays undeclared: adapters are Stage 4, and a mode that cannot be
  applied is a policy in name only.
- Added a startup notice when a persistence mode is configured that this build does not implement.
  The shipped settings select SQLite because FR-DATA-002 makes it the local default when persistence
  is enabled, so without a notice an operator reading their own configuration would expect a database
  file and accumulating exchanges and get neither, with nothing to explain it. Running with
  `mode: None` is a supported configuration and stays silent.
- Narrowed the activity-source subscription to the two sources this stage starts spans on.
  `agentsplice.stream` and `agentsplice.persistence` belong to the Stage 1 list the specification
  declares, but nothing writes to them yet, and subscribing to a source that produces nothing is the
  same defect the existing `Later_stage_activity_sources_are_not_declared_yet` test already guards
  against for later stages.

- Removed `upstream_status_error`. It was published in the client contract with nothing able to emit
  it: an upstream non-2xx is relayed verbatim with the runtime's own body, so AgentSplice writes no
  envelope and there was nothing for the type to describe.
- Gave `agentsplice.model_discovery.duration` a producer. It was declared as a live instrument and
  never fired. A failed refresh is timed too, because how long a runtime takes to refuse is as
  diagnostic as how long it takes to answer.
- Withdrew the `x-agentsplice-trace-id` claim from `GET /v1/models`. The OpenAPI declared a header the
  endpoint never sent: model discovery is not an exchange, none of the four declared activity sources
  covers it, so it has no span and must not advertise one.
- Added a test for the class of defect all three share —
  `Every_declared_error_type_has_something_that_produces_it` — because the existing contract tests
  only checked that a name was *documented*, not that anything *emitted* it.
- Produced `Measurement` values from the request path. The measurement-with-provenance model, one of
  the product's stated differentiators, was fully built and tested but used only by tests: durations
  reached metrics as bare histogram values with no provenance, and the exchange record carried none
  at all. Latency phases now carry `Measured`, token counts keep `UpstreamReported`, and no
  throughput value is derived, because a non-streamed exchange has no boundary separating prompt
  processing from generation.
- Added stable log event identifiers. `docs/OBSERVABILITY.md` requires them and there were none; a
  message is prose that will be reworded, while an identifier is what an alert rule can match on.
- Removed a service-locator call from the observability registration. The activity listener is now a
  constructor dependency of the telemetry, so "spans exist before the first one is started" is a
  compile-time fact rather than a registration ordering convention.
- Corrected `CLAUDE.md` and `docs/ARCHITECTURE.md` to put the structural summary before model
  resolution, matching the implementation. A request naming an unknown model is the case an operator
  most needs evidence for, and resolving first leaves it with no record of what arrived.
- Recorded three further decisions in ADR 0008: the request-path reordering, the rule that declared
  vocabulary must have a producer, and the deliberate process-global mutation of the activity
  identifier format.

### Stage 1A — Observability and published contracts

- Added spans and metrics through `System.Diagnostics` alone. No OpenTelemetry SDK is referenced and
  an architecture test enforces that; these are the primitives the SDK itself consumes, so adopting
  it later adds an exporter rather than rewriting instrumentation.
- Registered AgentSplice's own `ActivityListener`, which is what makes tracing exist at all.
  `StartActivity` returns null when nothing subscribes to a source, and with no SDK nothing does — so
  every span would have been absent and `x-agentsplice-trace-id` could never have been populated,
  despite the API contract promising it. The W3C identifier format is forced because the domain's
  `TraceId` accepts only 32 lowercase hexadecimal characters.
- Declared only the instruments this stage can honestly emit. Every streaming instrument, the
  first-byte and first-event timings, and both throughput instruments are absent, because a
  non-streamed exchange offers no boundary to measure them against and a zero would read as "this
  happened, and it was none".
- Classified success and failure from the recorded upstream status class rather than from the absence
  of an error, so a relayed upstream 500 is never counted as a success.
- Kept model identifiers out of metric dimensions. They are client-supplied and unbounded, so one
  caller could otherwise multiply the cardinality of every series without limit.
- Closed a credential leak that had nothing to do with AgentSplice's own logging:
  `IHttpClientFactory` writes request headers at `Trace`, so the runtime's bearer token reached any
  enabled sink verbatim. The provider's clients now redact credential-bearing headers. Found by the
  privacy suite, which runs at `Trace` precisely so that "content is absent even at the most verbose
  setting" is the claim being made.
- Added the privacy suite: four distinct sentinels for prompt, completion, tool argument, and API
  key, asserted absent from every log channel — message, structured state, and scope — because a
  structured value reaches a sink just as surely as a formatted message.
- Published the `error.type` vocabulary and the observability names in their documents, bound by
  contract tests, so code and specification cannot drift.
- Updated the OpenAPI draft to describe what the gateway actually does: `text/event-stream` removed
  and `stream` constrained to `false` until Stage 1B, a closed `error.type` enum so a later category
  is a visible schema change, correlation headers on every error response, the statuses the gateway
  can return, and the `created` sentinel's meaning stated where a reader will find it.
- Added ADR 0008 recording all eighteen Stage 1A decisions with the alternatives rejected.

### Stage 1A — Failures, limits, and credential containment

- Consolidated error translation into a single table, total over `FailureClass`, so a new failure
  class cannot be added without deciding what it means to a client. Spread across call sites the
  same mapping drifts, and two places translating one condition into two statuses is a defect
  neither place looks wrong for.
- Published the `error.type` vocabulary. The specification supplies exactly one by example, so
  Stage 1A defines the rest: client-validation failures reuse OpenAI's `invalid_request_error` so an
  SDK that branches on `type` keeps working, and the gateway-only conditions get categories a plain
  model provider has no vocabulary for.
- Fixed a defect in the completion path: a client disconnect was recorded as a failure rather than a
  cancellation. The provider catches the cancellation itself and reports it as a classified result,
  so the exchange never reached `Cancelled` — which would have made a real disconnect
  indistinguishable from a runtime fault in both the exchange list and the metrics.
- Proved cancellation reaches the runtime. The test waits on the fake upstream's own abort signal,
  because anything less only shows that AgentSplice stopped reading, which a client experiences
  identically while the runtime keeps generating and burning the compute cancellation exists to
  reclaim.
- Verified timeout-phase attribution against a real listener, including that a runtime which
  accepted the connection and went quiet is never reported as unreachable.
- Verified credential containment end to end: the configured key is attached, a client's own
  `Authorization` never replaces it, and the key appears in no response, error, or header. An
  upstream authentication body is discarded rather than relayed, because it can hint at the key's
  shape.
- Verified the header allowlists in both directions, including that an invented client header and an
  upstream `Set-Cookie` are dropped while `Retry-After` and rate-limit headers are relayed.
- Added a last-resort exception handler so a fault escaping the pipeline still produces the stable
  envelope rather than a framework page that could disclose a stack trace.
- Diagnosed the intermittent integration failure reported in the previous slice. It was not
  contention: `WebApplicationFactory` resolves a top-level-statements entry point through static
  handoff state shared across the process, so concurrent hosts let one factory observe another's
  disposed provider, and the tests that deliberately fail startup saw `ObjectDisposedException`
  instead of the validation failure they assert. Collections now run serially in that assembly,
  which closes the window rather than narrowing it.

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
