# ADR 0008 — Stage 1A transparent request path

- Status: Accepted
- Date: 2026-07-29
- Related: ADR 0002 (System.Text.Json), ADR 0004 (content storage opt-in), ADR 0006 (durable core), ADR 0007 (Stage 0 toolchain)

## Context

Stage 1A adds the first HTTP surface: `GET /v1/models` and non-streaming `POST /v1/chat/completions`.
"Transparent by default" (P-002) has to become a property the build can check rather than an
intention, and several choices are cheap now and expensive once streaming, persistence, and a
dashboard depend on them.

## Decisions

### 1. Byte-splice model substitution, not document re-emission

The upstream receives the client's original bytes unless routing renames the model. When it does,
only the byte span of the top-level `model` value is replaced, located by `Utf8JsonReader` during the
same pass that builds the structural summary.

Rejected: re-emitting the parsed document with `Utf8JsonWriter` and `JsonElement.WriteTo`. It is
semantically equivalent and not byte-identical, because a writer normalises escape forms and number
formatting: `"A"` becomes `"A"` and `1.0` becomes `1`. The claim would then be "nothing else
changed as far as our own parser is concerned", and an exact-forwarding test exists precisely because
that is not the claim worth making.

The replacement value is JSON-encoded rather than copied, because a model identifier is an opaque
third-party value and may contain a quote or a backslash (decision 9).

### 2. No response rewriting, and an explicit header allowlist

The response `model` keeps the runtime's value. Rewriting it back to the client's alias is not
required for routing, security, or protocol correctness, so P-002 forbids it.

Headers are allowlisted in both directions rather than denylisted. A denylist is wrong by default:
any header a client or runtime invents later crosses the boundary until someone notices, and the cost
of noticing late is a leaked credential or a smuggled cookie. `Retry-After` and rate-limit headers
are relayed, because a 429 without them has discarded the one thing the status exists to convey.

### 3. `x-request-id` is forwarded upstream

The cheapest link between an AgentSplice exchange and a runtime log line, and it carries no content.
The client's `Authorization` is never forwarded (docs/SECURITY.md).

### 4. Ports in Application, transport in the provider

`AgentSplice.Application` has no dependency on `System.Net.Http`, enforced by an architecture test.
The provider classifies transport failures into an Application-owned `UpstreamFailure`, so error
translation is deterministic, vendor-free, and unit-testable without a socket. Without the test the
boundary holds only until the first time catching `HttpRequestException` in Application is convenient.

### 5. The exchange record boundary

`CompletionExchange.Accept` requires a valid `ClientModelId`, so a request failing before its model is
known produces an `ExchangeId`, a `PublicRequestId`, a trace, a timeline, and a stable envelope — but
no `CompletionExchange`.

Rejected: accepting with a placeholder model (fabricated evidence), and making `ClientModelId`
optional (ripples into `Resolve` and into every shape Stage 1C will persist).

**Consequence, owned by Stage 1C:** `/api/v1/exchanges` will not list malformed requests until
`Accept` is relaxed or a distinct rejected-request artifact exists.

### 6. `stream: true` is refused with 400

Buffering an event stream into a JSON body would be an invisible semantic transformation, and a 200
would make an unimplemented capability look implemented. The message names no roadmap stage, because
it is a public contract that outlives the stage and a client cannot act on "Stage 1B".

### 7. Timeout ownership and phase attribution

`HttpClient.Timeout` is `Timeout.InfiniteTimeSpan`. The 100-second default throws a
`TaskCanceledException` indistinguishable from a client disconnect, which would make FR-CHAT-008
unimplementable and would report every disconnect as a timeout.

Phases are enforced by linked `CancellationTokenSource`s rooted at the client's token, plus
`SocketsHttpHandler.ConnectTimeout`. Attribution checks the client token first, then the **total**
budget before the **response-header** budget: the header source is linked to the total source, so
both are signalled together when the total elapses, and checking headers first would misreport every
total expiry as a header timeout.

Because validation guarantees `responseHeaders <= total`, the total budget can only fire distinctly
once headers have arrived and the body read is what stalls.

### 8. One named HTTP client per runtime

`SocketsHttpHandler.ConnectTimeout` is a handler property while `timeouts:connect` is configured per
runtime, so a shared handler could honour only one runtime's budget and every other configured value
would silently not apply. Per-runtime clients also isolate connection pools.

Clients are configured lazily by name rather than enumerated at registration, because reading the
runtime list during registration binds configuration before a host has finished assembling its
sources — the same ordering hazard ADR 0007 records for the loopback default.

`AllowAutoRedirect` and `UseProxy` are both off: a redirect would silently change which runtime
answered and could send the bearer token to a host the operator never named.

### 9. External model identifiers are opaque

`ClientModelId`, `UpstreamModelId`, and `ModelAliasId` bound length and reject control characters and
text that cannot be encoded as UTF-8. They no longer enforce a punctuation allowlist.

Model identifiers are chosen by runtimes, registries, and model authors. Rejecting a value the
runtime would have accepted makes AgentSplice the source of a failure that does not exist downstream,
which is the opposite of transparent forwarding. `RuntimeEndpointId` keeps the strict slug rule
because it is operator-chosen and is a bounded metric dimension.

A repeated top-level `model`, `messages`, or `stream` is refused: "last wins" can differ between
AgentSplice's validation, the splice arithmetic, and the runtime's own parser, so the three could
disagree about what was actually sent.

### 10. Every client- and runtime-supplied name is bounded, and truncation is visible

`StructuralRequestSummary` bounded each recorded name's length but not how many distinct role names
it held, so a request with a unique role per message would have grown it without limit — defeating
the bound's own rationale. Roles beyond the cap fold into one bucket, so the per-role counts still sum
to the message count, and `RoleNamesTruncated`, `UnknownFieldNamesTruncated`, and
`FinishReasonsTruncated` make a bounded list distinguishable from a complete one.

### 11. Correlation header guarantees

`x-agentsplice-request-id` is returned on every response at every status.
`x-agentsplice-exchange-id` is returned for completions only — model discovery is not an exchange,
and naming a record that does not exist would be a fabrication. `x-agentsplice-runtime` appears only
after routing has chosen one. `x-agentsplice-trace-id` appears whenever an activity exists
(decision 13).

A malformed `x-request-id` never fails a request: the value is replaced and only the fact of the
rejection is recorded. Echoing an unvalidated token into a response header is a header-injection
vector, and failing an inference call over a diagnostic convenience is worse than losing the
convenience.

`x-agentsplice-diagnostics` is deliberately **not** implemented in this stage. Accepting and ignoring
it advertises a capability that does not exist, and as an unauthenticated client-supplied verbosity
switch it would be a leak vector.

### 12. Upstream non-2xx is relayed verbatim

`401` and `403` are never echoed: the credential is the gateway's, so a `401` would tell a client to
fix a key it does not own, and the body can hint at the key's shape. It is discarded.

Every other non-2xx is relayed unchanged — status, body, and allowlisted headers — **whether or not
the body is JSON**. Parsing gathers evidence and never gates forwarding. A runtime answering
`429 text/plain` or `500 text/html` is still answering, and substituting a gateway error would discard
the most actionable diagnostic a user has.

Therefore `ExchangeStatus.Completed` means *the transport cycle finished*, not *the operation
succeeded*. A relayed 500 completes with no `FailureClass`, because AgentSplice did not fail. Success
and error are classified from `UpstreamResponseMetadata.StatusClass`, never from the absence of a
failure class.

Rejected: mapping upstream 5xx to 502 so the gateway never impersonates its own failure. It loses the
runtime's status semantics, and the always-present `x-agentsplice-*` headers plus the recorded
upstream status already distinguish the two sides. Also rejected: adding a `FailureClass.UpstreamStatus`
member, which would force a twelfth `ErrorCodes` entry for a condition that is not an AgentSplice
failure.

### 13. HTTP status is transport metadata, not body structure

`UpstreamResponseMetadata` carries the status, media type, upstream request id, and the moment
headers arrived. It is separate from `StructuralResponseSummary`, which describes an interpretable
body, because the two come apart exactly when it matters: a `204` has no body, a `429 text/plain` has
one that is not protocol data, and a truncated `500` has one that cannot be parsed. In all three the
status was observed and must be recordable.

### 14. Stage 1A instruments with `System.Diagnostics` alone

No OpenTelemetry SDK is referenced, enforced by an architecture test. Because nothing else subscribes
to the `agentsplice.*` sources, AgentSplice registers its own `ActivityListener` and forces the W3C
identifier format — without it `StartActivity` returns null, every span is absent, and
`x-agentsplice-trace-id` could never be populated even though the API contract promises it.

Stage 1B replaces the listener with the SDK and must not run both: two listeners on one source sample
every activity twice.

Only the instruments this stage can honestly emit are declared. Every streaming instrument, the
first-byte and first-event timings, and both throughput instruments are absent, because a
non-streamed exchange offers no boundary to measure them against and a zero would read as "this
happened, and it was none".

`IHttpClientFactory` logs request headers at `Trace`, so the provider's clients configure header
redaction. Without it the runtime's bearer token is written verbatim to any enabled sink — a
disclosure unrelated to AgentSplice's own logging that would survive every other precaution.

### 15. `created` for a model with no creation evidence

Internally `null`. The OpenAI `Model` schema marks `created` required and integral and mainstream
SDKs deserialize it into a non-nullable integer, so the `/v1/models` envelope substitutes `0` as a
compatibility sentinel — and nowhere else. Zero is a real Unix timestamp meaning 1970-01-01, not a way
of saying "unknown", so it is never persisted, compared, or re-read as a date. An alias inherits the
creation evidence of the model it targets when that model was discovered.

### 16. Model resolution precedence and discovery on the request path

An enabled alias, then a discovered model, then the configured pass-through runtime. Aliases resolve
with zero I/O, so an alias-only deployment never pays for discovery on the request path. Discovered
identifiers consult the cache, refreshing on the completion path if needed, so a model never works
"only after someone called `/v1/models`".

"The catalogue was consulted and the model is absent" (404) is distinguished from "no runtime could be
asked" (502). Reporting the first when the second is true is the misleading evidence this product
exists to remove.

An identity alias — a model name mapped to itself on a chosen runtime — is valid. Stage 0 rejected it
as a resolution cycle, reasoning about a resolver that chains alias to alias; the resolver that exists
does not chain. It is also the only way an operator can pin a model to one runtime when two offer it,
so rejecting it removed the sole deterministic override of the FR-MOD-004 tie-break.

A failed discovery is remembered for the same window as a successful one. Without that, every request
naming an unknown model waits out the connect timeout again while a runtime is down. A cancelled
refresh is not remembered, because our own impatience is not evidence about the runtime.

### 17. A no-op record sink is the Stage 1A seam

Stage 1A persists nothing (FR-DATA-001). `IExchangeRecordSink` with a discarding default is the only
way timeline evidence is observable before persistence and the administrative API exist, so the
"routing changes are represented as events" exit criterion would otherwise be untestable — and it is
the interface Stage 1C implements. Because nothing is queued, `MetadataQueued`,
`PersistenceCompleted`, and `PersistenceFailed` correctly stay absent.

### 18. A routing decision is not a body rewrite

`ModelResolution.IsRoutingChange` is true only when the identifier changes, so an alias that selects a
runtime without renaming, a tie-break between two runtimes offering the same identifier, and a
pass-through would all have been invisible. `ModelResolutionOutcome` carries `RoutingWasApplied` and
`RequiresBodyRewrite` separately, and FR-TRACE-007's event is driven by the first.

## Consequences

- Adding a `FailureClass` member requires an `ErrorCodes` member and a docs/API.md bullet in the same
  change; contract tests enforce all three.
- The known-request-field set is bound to the OpenAPI `ChatCompletionRequest` schema, so adding a
  modelled field is a two-file change.
- `Application` cannot use `HttpClient`, so a future provider must classify its own transport
  failures.
- The non-streaming path buffers whole bodies, so memory scales with concurrency times
  `maxRequestBodyBytes`. A concurrency limit is owed by Stage 1B (NFR-PERF-005).
- `ErrorCodes.InvalidUpstreamStream` and `ErrorCodes.PersistenceUnavailable` are declared and
  unemittable in this stage, because `ErrorCodeContractTests` ties the code count to `FailureClass`.
  They become reachable in Stage 1B and 1C respectively.
- Integration test collections run serially. `WebApplicationFactory` resolves a
  top-level-statements entry point through static handoff state shared across the process, so
  concurrent hosts let one factory observe another's disposed provider.

## Alternatives considered

**Reparse and re-emit the request body.** Rejected; see decision 1.

**Translate every upstream non-2xx into a gateway error.** Rejected; see decision 12.

**Enumerate runtimes at client-registration time.** Rejected; see decision 8.

**Keep the Stage 0 punctuation allowlist for model identifiers.** Rejected; see decision 9.
