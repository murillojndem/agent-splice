# Changelog

All notable changes will be documented here.

## Unreleased

### Stage 1C review corrections

Directed review of the finished backend found three material problems, one of them a regression the
slice introduced into the repository's own container configuration.

- **Docker and Compose could not start.** `AdministrationBindingGuard` correctly treats the
  container's `0.0.0.0:5280` as network-reachable and refuses without a credential, and nothing
  shipped one. Compose now requires `AGENTSPLICE_ADMIN_API_KEY` with no default, failing at
  `docker compose config` with a message naming the variable — a default token is a token everybody
  knows. The guard was not weakened: a process inside a container cannot tell that the host published
  its port on loopback only.
- Two Dockerfile defects surfaced while proving this and had been latent since the projects existed:
  the restore stage copied four of seven manifests, so `--no-restore` publish failed with
  `NETSDK1004`, and `.editorconfig` never entered the build context, so the analyser severities this
  repository waives — `CA1848` among them — were errors inside the image.
- **Readiness consulted runtimes with the option off.** Health was evaluated before
  `requireReachableRuntime` was read, so a probe could wait out a connect timeout on a deployment
  whose configuration had explicitly said reachability was not a readiness condition — long enough for
  an orchestrator's own probe to expire and take a healthy gateway out of rotation. The early return
  is now the contract, and `reachableRuntimes` is absent rather than zero when nothing was consulted.
- **A local reverse proxy could bypass the administrative token.** Trusting a loopback remote address
  is wrong behind nginx or Caddy, which connect to Kestrel from `127.0.0.1`, so every relayed request
  looked local. Reading `X-Forwarded-For` without trusted-proxy configuration would be worse — that
  header is caller-supplied. A configured token is now required from every caller, loopback included,
  which makes a relayed request and a local one satisfy the same check.
- `AdministrationBindingGuard` now runs against `app.Configuration` after `Build()`. Reading
  `builder.Configuration` inside the composition root is the same defect as ADR 0013 decision 13 one
  layer up: a late binding was invisible to the check meant to catch it.
- The filter returns its refusal as an `IResult` instead of writing the response and returning null,
  which left the framework materialising that null onto a response that had already started.
- `401` now carries `WWW-Authenticate: Bearer`. The envelope still names the mechanism and never the
  presented token, the configured variable, or the caller.
- The OpenAPI draft gained a bearer security scheme, per-operation `400`/`401`/`404`/`500`/`503`
  responses, and a corrected description: timeline and observations return the same ordered sequence
  in Stage 1, which the document had described as a projection that has never existed.
- `.env.example` no longer offers PostgreSQL or content capture as usable; both are refused at
  startup.
- Corrected "constant-time" to what it is: `FixedTimeEquals` returns immediately on a length mismatch,
  so the configured token's length is observable. Acceptable for a random token, and stated.

### Stage 1C, part 5: administrative authentication

- Every `/api/v1` route now requires authorization, applied to the route group so a route added later
  cannot be added unprotected by forgetting. A loopback caller is allowed — that is the deployment
  AgentSplice is built for, and requiring a token there would make reading your own traces need secret
  management first. Anything else needs `Authorization: Bearer <token>`, compared in constant time
  against a value read from the environment variable named by the settings; the setting holds a
  variable name and never a secret.
- Startup refuses a network binding with no token rather than warning about it. A warning in a startup
  log is a message read once, if the level happens to be right, on the deployment least likely to be
  watched. A bare `HTTP_PORTS`, a wildcard host, and `0.0.0.0` all count as network bindings — that is
  what a container publishing a port produces, which is exactly the case worth catching.
- `/health/live` and `/health/ready` stay outside the group. A liveness probe that failed closed on a
  misconfigured token would restart a healthy process.

### Stage 1C, part 4: system, runtimes, model catalogue, and health

- `GET /api/v1/system`, `/runtimes`, `/models`, `/health/runtimes`, plus `/health/live` and
  `/health/ready`.
- Health is derived from the same discovery the request path uses rather than probed separately. A
  second prober would double the load on every runtime and could disagree with what routing sees, and
  a health page saying a runtime is fine while completions to it fail is a page an operator believes.
  A runtime nothing has consulted reports `unknown` with no `checkedAt`.
- That rule caught a defect in the catalogue: `reachable` was a required boolean, and a runtime with
  discovery disabled reported `true` having never been asked. It is nullable now — `false` would read
  as unreachable for a runtime that is fully usable through its aliases, and `true` was an assertion
  nothing supported.
- `/api/v1/models` reports `created` as absent where `/v1/models` emits `0`. The zero is a
  substitution the OpenAI schema forces and belongs to that envelope alone.
- Readiness requires a reachable runtime only when asked to, and the default is off: a gateway whose
  runtime is down is still correctly configured and is still the component able to report the outage.

### Stage 1C, part 3: the exchange, timeline, and observation APIs

The evidence became readable. Parts 1 and 2 moved it from "discarded" to "stored and reachable only
by opening the database file"; this is the surface `openapi/agentsplice-openapi.yaml` and
`docs/API.md` have specified since Stage 0.

- `GET /api/v1/exchanges`, `/exchanges/{id}`, `/exchanges/{id}/timeline`, and
  `/exchanges/{id}/observations`. The handlers read query values, call the application, and write what
  comes back; validation, pagination, status selection, and payload shape are decided in
  `ExchangeQueryService`, which returns the same `GatewayResponse` the completion endpoints use.
- Paged by an opaque cursor carrying the whole sort key rather than an offset. An offset into
  `(startedAt DESC, exchangeId DESC)` skips or repeats rows whenever an exchange is written or expired
  between two pages, which on a gateway still serving traffic is every page.
- Filters are validated, not ignored. A `status` or `runtimeId` outside the published vocabulary is
  refused with `agentsplice_invalid_query` naming the parameter — never echoing its value — because a
  silently dropped filter returns a page that looks like an answer to the question asked and is an
  answer to a different one.
- An identifier that does not parse and one that is not retained answer the same 404. They are
  indistinguishable to a caller who cannot see the store, and separating them would tell anyone
  probing the surface which of their guesses were well-formed.
- A deployment with `persistence:mode: None` answers `503 agentsplice_persistence_disabled` rather
  than an empty page. Ephemeral operation is supported, and on such a deployment "no exchanges are
  stored" and "no exchanges happened" are both true while only one answers the question.
- Administrative error codes are a separate published set from the core ones. The core set is the
  completion path's vocabulary and is deliberately the same size as `FailureClass`; reading stored
  evidence is not an exchange and has no failure class, so folding them together would have meant
  inventing one for "the caller asked for a row that is not there".
- The stored structural summaries are served through unchanged, embedded rather than reparsed. The
  schema declares them `additionalProperties: true` precisely so a summary can gain a field without a
  contract change, and a serialiser would have made the writer the thing that has to keep up.
- `ExchangeSummary.streaming` became nullable in the OpenAPI draft. A request refused before its
  envelope was read never stated a preference, and `false` would be a claim about a body nothing
  parsed.
- Normalised the working tree back to LF. Editing scripts had rewritten eleven files as CRLF, which
  `.gitattributes` forbids and which broke a contract test that reads the document it verifies.

### Stage 1C, part 2: retention sweep and the settings this build cannot honour

- `RetentionSweepService` removes exchanges past `capture:retention:metadata` in bounded batches,
  sweeping once at startup and then on `capture:retention:sweepInterval`. A gateway restarted more
  often than its interval would otherwise never sweep at all. Idempotent by construction, and
  auditable by counts and the window rather than by naming what was deleted — a log that names deleted
  evidence is a copy of it that outlives the policy.
- `contentEnabled` and `PersistenceMode.Postgres` are now refused. The first claimed prompts were
  retained under a sanitiser that does not exist; the second was silently served by SQLite.

### Stage 1C, part 1: SQLite metadata store and the write path

Exchanges survive the process. Until now every exchange produced a complete timeline, measurements
with provenance, and a structural summary, and then handed all of it to a sink that discarded it —
a gateway whose product is evidence, retaining none. Recorded in
[ADR 0013](docs/adr/0013-stage-1c-metadata-store.md), which supersedes decision 12 of ADR 0009 and
discharges the Stage 1C consequence ADR 0008 recorded.

- Added `AgentSplice.Infrastructure.Persistence`: an EF Core SQLite store with three tables, a bounded
  in-process queue, and a background writer. EF Core is referenced by `AgentSplice.Infrastructure`
  alone, which an architecture test now enforces.
- Rows are separate types from the domain records. `CompletionExchange` is immutable with private
  `init` setters and value-object identifiers, so mapping it directly would put persistence
  conventions inside `AgentSplice.Domain`. The separation also closes the gap ADR 0008 left open: a
  request refused before its envelope was read has no `CompletionExchange`, and the store can record
  it with a null model and a null streaming preference rather than not recording it at all.
- The model is provider-neutral — no SQLite type names, no raw SQL, timestamps as UTC ticks — because
  FR-DATA-003 commits to PostgreSQL through the same contracts and a model that must be rewritten to
  honour that is not a contract.
- `RecordAsync` never waits. It calls `TryWrite` and returns, so a slow store cannot become gateway
  latency; the write itself runs on a background service in short transactions.
- The persistence boundaries are stamped by whatever produced them. `MetadataQueued` comes from the
  sink as the record enters the queue, `PersistenceCompleted` from the writer after the commit
  returned — a second small transaction, because a boundary named "completed" that was stamped inside
  the transaction it describes is the defect class ADR 0010 exists to prevent. `PersistenceFailed`
  deliberately has no row: the store that rejected the write is the one it would have to live in, so
  it is a log event, a counter, and nothing pretending to be evidence.
- Three defects were found by their own tests before they could ship. `BoundedChannelFullMode.DropWrite`
  reads like the intent and is not — `TryWrite` returns `true` having discarded the record, so every
  drop was silent and the counter never moved; the channel now uses `Wait`, under which `TryWrite`
  refuses and reports. Reading `IConfiguration` at registration time to decide whether to register the
  store read it half-built, silently disagreeing with the `IOptions` value the rest of the system
  uses; the decision now happens when a service is resolved. And passing the shutdown token to
  `SaveChangesAsync` discarded, on every host stop, records that had already left the queue — the wait
  for more work is cancellable, the write it leads to is not.
- Added `agentsplice.persistence.failures` with a two-value `failure_reason` dimension, and made
  `agentsplice.persistence` a live span source. Saturation and a rejected write are different problems
  with different fixes, and one undifferentiated count would send an operator adding queue capacity to
  a database refusing every write.
- Closed three channels through which caller-chosen text reached the store with content capture
  disabled. A structural summary was bounded in length and count, and that had been treated as making
  it safe; it is not. A client picks the value of `role` and the name of every JSON property it sends,
  and a runtime picks the value of `finish_reason`, so truncating those bounded how much caller text
  was retained and nothing else. Roles and finish reasons are now matched against a closed vocabulary
  and bucketed when they do not match; unknown field names are hashed, so an operator can still ask
  whether a field was forwarded without the name itself being stored. The existing privacy test missed
  all three because it put its sentinels in `content`, which the gateway never reads — the new one
  puts them in every field AgentSplice actually records, and fails against a reintroduced copy of the
  defect.
- Removed a fault channel found in the same place: the field-name helper *threw* on a control
  character, so a hostile `role` turned client input into a failed request.
- Made vocabulary matching exact and ordinal. The first version of the fix trimmed and lower-cased
  before comparing, so `" User "` was stored as `user` — a token the client never sent. Closing a
  leak by persisting a tidied copy of the protocol and serving it as the observation is not a fix;
  a non-canonical role is a fact, and `(unrecognised)` is what reports it.
- Stopped a client forging an AgentSplice observation. The buckets are outputs and were being
  accepted back as inputs, so `{"role": "(unspecified)"}` was recorded as a message that stated no
  role. Absence now travels out of band, as a separate count the scanner supplies, because a
  dictionary key is a string and every string is forgeable while the absence of one is not.
- Stopped dropping blank finish reasons, which made `"finish_reason": ""` indistinguishable from a
  response that carried none.
- Stopped logging the client's own correlation token. `x-request-id` is accepted as up to 128
  characters of printable ASCII, which prevents header injection and nothing else — a client is free
  to put a name or a ticket subject in it. Four AgentSplice log sites wrote it, and a fifth path
  wrote it with no AgentSplice involvement at all: `IHttpClientFactory` logs request headers at
  `Trace` and the token is forwarded upstream. Logs now carry `ExchangeId`, and the header joins the
  HTTP-client redaction list. `IdentifierText`'s claim that its validation kept content out of
  observability was false and is corrected.
- Documented `ClientModelId`, `PublicRequestId`, and `UpstreamRequestId` as operational metadata
  retained verbatim: chosen by untrusted parties, potentially sensitive, kept whole because diagnosis
  needs them, and authorized like any other stored evidence.
- Stopped every persisted row claiming that nothing was retained. `ExchangeRecorder.Accept` opens each
  exchange as `Disabled` and cannot know better, so the row carried that through while holding
  summaries, observations, and measurements. The store stamps `MetadataOnly`, because the store is
  what retained — including for records that have no `CompletionExchange` to carry the state at all.
- Stopped a backwards clock producing a `persistence.duration` of zero with `Measured` provenance and
  an end preceding its own start. An impossibly ordered interval now yields no measurement, as it
  already did in the recorder's duration builder and the gateway's histogram guard; the boundaries
  stay, so the anomaly is still diagnosable.
- Made `MeasurementNames.PersistenceDuration` producible. It had been declared since Stage 1A with
  nothing able to emit it. The store now writes it queue-to-durable per exchange, rather than as the
  batch's write time — a batch covers many exchanges, and attributing its duration to each would
  report one number N times and overstate every one.
- Added the persistence-failure fixture family `docs/TESTING.md` recorded as still owed by Stage 1B.
  It asserts the policy rather than a broken file: a refusing context factory, and the failure logged
  with a stable event ID, counted per exchange lost rather than per batch, dropped rather than
  retried, with the writer still draining and nothing reaching the caller.
- Restored the coverage the test-host change removed. Integration hosts now force persistence off so
  that tests do not each create a database, which left nothing exercising the shipped persistence
  block; a contract test asserts it against `appsettings.json` directly, including that the mode named
  there has a provider in this build.
- Added an architecture test that the API never reaches for a `DbContext`. It composes persistence and
  must not query it, and the module-boundary test that confines EF Core to Infrastructure does not
  cover the API assembly.
- Quieted EF Core's command logging to `Warning` in the shipped settings, with a contract test.
  Parameters are redacted unless sensitive-data logging is enabled, so this is noise rather than
  leakage — but a store that narrates itself into the default log is one setting away from being the
  other thing.
- Removed `UnimplementedPersistenceNotice`. It existed to say the store had not shipped.
- Deferred the OpenTelemetry SDK swap from Stage 1C to Stage 1D, in `docs/OBSERVABILITY.md` and the
  architecture test that referenced it. Until an exporter exists, adopting the SDK adds a dependency
  and changes nothing an operator can see.

### Stage 1A/1B correctness review, third pass

One defect, introduced by the previous pass. Recorded in
[ADR 0012](docs/adr/0012-classification-independent-of-relayability.md), which refines ADR 0011.

- Stopped protocol classification depending on whether the content type could be relayed. Splitting
  the header into an evidence token and a relayable value was right; asking the relayable one whether
  the body is an event stream was not. `RelayableContentType` is null when the header is too long to
  write back, so a conforming `text/event-stream` carrying 1100 characters of parameters was
  classified as buffered — the client got `text/event-stream` from the fallback and read a stream,
  while the gateway ran no SSE framing, ignored `[DONE]` and waited for EOF or an idle timeout,
  recorded no decoded or semantic boundaries, and wrote `upstream.streamed = false` into the evidence.
  Metadata now carries a third value, `ParsedMediaType`, parsed from the header as received before
  any bound has discarded anything, and classification uses only that. Relayability answers whether
  the whole header may be written to the client; it never answered what the body is.
- Moved the RFC 9110 media-type grammar into `AgentSplice.Domain` so the parse can happen at the
  moment the header arrives. `OpenAiMediaTypes.IsEventStream` is now one comparison against the
  parsed media type, which keeps one implementation of the grammar rather than two.

### Stage 1A/1B correctness review, second pass

Four more defects, found by reviewing the previous pass. Two of them are the same shape as the first
round one layer down: a bound enforced in the ordinary case and skipped in the interesting one, and a
claim in the documentation that the code did not quite honour. Recorded in
[ADR 0011](docs/adr/0011-per-event-bounds-and-media-type-parsing.md), which refines ADR 0010.

- Bounded a **completed** streamed event, not only one still being assembled. The frame reader checked
  the bytes after the last complete frame, and completing a frame resets that count — so an event that
  crossed `maxStreamEventBytes` in the same append that carried its terminating blank line was
  accepted in full. The ceiling held for every event except the ones that reached it. Each frame is
  now measured as it completes, and an oversized one is neither handed out nor stepped over.
- Stopped a bound violation from retracting a completion the client already holds. The relay enforced
  the bound before draining, so a `[DONE]` that completed earlier in the same read was discarded
  unread and the exchange was recorded as `InvalidUpstreamStream` — for a stream that had terminated
  correctly, whose terminator bytes the client had already received. Draining now precedes
  enforcement, and the terminator wins. A violation *ahead* of the terminator still ends the stream:
  there is no completion to protect, and a client must not be handed a truncated stream that closes
  as though it were whole.
- Parsed the content-type parameters instead of skipping them. The previous fix took everything before
  the first semicolon and compared it, which accepted `text/event-stream; ===` — the mirror image of
  the whole-string equality it replaced. Classification now validates the full RFC 9110 grammar, with
  no `System.Net.Http` dependency, since an architecture test keeps transport types out of the
  protocol modules. `text/event-stream;` stays valid: the grammar writes the parameter itself as
  optional, and refusing it would reject a legal sender.
- Made the relayed `Content-Type` actually verbatim. It was still passing through the evidence
  sanitiser, which truncates at 256 characters — and a header cut there ends inside a quoted parameter
  or halves a multipart `boundary`, producing a header the runtime never sent. The relayed value is
  now validated rather than repaired: over 1024 characters or carrying a control character it is
  refused outright and the normalised media type is sent instead, which says less than the runtime did
  rather than something untrue. The two values are bounded for their own reasons and by their own
  rules.
- Renamed `SseFrame.IsCommentOnly` to `DispatchesClientEvent`. The logic was right — the SSE grammar
  dispatches nothing when the data buffer is empty — but the name covered a bare `id`, a `retry`
  directive, and an `event` name with no payload as well, and invited the delivered-event count to be
  read as excluding keepalives alone.

### Stage 1A/1B correctness review

Five defects found by reviewing the finished Stage 1A and Stage 1B slices. None changed what a client
received; every one changed what AgentSplice said it had observed, which for a diagnostic tool is the
more serious of the two. Two existing tests had to be replaced rather than extended, because they
asserted the defective behaviour as the contract. Recorded in
[ADR 0010](docs/adr/0010-correct-stream-boundary-and-termination-semantics.md), which supersedes
parts of ADR 0009.

- Stamped each streaming boundary at the operation that produced it. The relay took one clock reading
  *before* awaiting the upstream read and reused it for the first upstream byte, the first decoded
  event, the first semantic event, and the first client flush. Four distinct boundaries collapsed
  onto one instant, and that instant preceded all four: a runtime that thought for twenty seconds had
  its first byte dated twenty seconds early, and every interval between the four was exactly zero —
  indistinguishable from a gateway that is infinitely fast and from one that is not measuring at all.
- Separated the first client event from the first decoded frame. A comment or keepalive may set the
  decoded boundary and never the client-event boundary, since a conforming client raises no event for
  it and dating first delivery from a keepalive reports a response as having reached the client
  before it carried anything.
- Made the timeline append boundaries in the order they occurred rather than the order they were
  learned. The relay writes before it decodes, so the flush timestamp is earlier than the decode that
  revealed it; appending as learned left the timeline running backwards whenever a keepalive and a
  data event arrived in one read. A negative interval is dropped rather than reported, so the symptom
  was a whole latency phase disappearing.
- Stamped the buffered path's first upstream byte inside the body reader's callback. It was read
  after the whole body had been buffered, so the boundary really named "the body finished" — or, on
  the failure branches, "the body failed". For a long generation the two are the entire length of the
  response apart, which is exactly when the boundary is worth having.
- Moved stream media-type classification into the protocol, matching RFC 9110 rather than comparing
  the whole header for equality: `text/event-stream; charset=utf-8` is an event stream. The relay and
  the orchestrator now ask the same implementation the same question instead of repeating a literal.
- Stopped rewriting the runtime's `Content-Type` on its way to the client. The normalised token kept
  for evidence was being forwarded, which silently dropped a `charset` a client decodes by and would
  have discarded a `boundary` a body cannot be parsed without. The header now reaches the client
  verbatim; the bounded token stays where the bounded token belongs.
- Inspected `FlushResult` instead of discarding it. `PipeWriter` reports a completed or cancelled
  pipe without throwing, so a client that stopped consuming could go unnoticed: the relay kept
  reading the runtime and kept counting bytes as delivered to someone who was gone.
- Made the first valid `[DONE]` the end of the response. The relay used to read on to EOF or an idle
  timeout, which ADR 0009 accepted as costing latency but not accuracy. It cost both — completion was
  dated from whatever ended the transport, so a runtime that lingered after finishing stretched the
  upstream duration and the generation window derived from it across a stall that produced nothing.
  No read is issued after the terminator, and a repeat terminator is neither read nor forwarded: a
  second `[DONE]` is not a second completion. The price is one connection per lingering runtime,
  which is the right trade.
- Corrected the public status in `README.md` and `src/README.md`, which still described Stage 0 as
  current and the host as exposing no HTTP endpoints.

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
- Fixed a misclassification CI caught on Windows: a runtime that answered and then died partway
  through its own body was reported as `agentsplice_runtime_unavailable`. The buffered path's catch
  blocks could not tell which phase had failed, so whether a truncated body surfaced as an
  `IOException` or an `HttpRequestException` decided the reported cause — a diagnosis by race. Body
  reads now classify their own failures: once response headers have arrived, "unreachable" is
  factually impossible. The catalogue path had the same defect and the streaming path already
  handled it.
- Made two fixtures deterministic rather than leaving them to that race. A connection reset can
  outrun the bytes that preceded it, so a test pinning one error code to it was asserting a coin
  flip; the strict assertion now uses a runtime that declares a length and stops short of it, and the
  reset case asserts what is true either way. The streamed-reset test gates the reset until the
  client is blocked waiting, because ungated the client could finish reading buffered bytes and see a
  clean end — reporting success for a truncated stream about one run in three.
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
