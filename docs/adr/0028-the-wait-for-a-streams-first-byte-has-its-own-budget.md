# ADR 0028 — The wait for a stream's first byte has its own budget

- Status: Accepted
- Date: 2026-10-19
- Related: [ADR 0008](0008-stage-1a-transparent-request-path.md) (decision 7: timeout ownership and
  phase attribution), [ADR 0023](0023-stage-1d-the-first-dated-trace-report.md) (the trace report whose
  finding 2 this answers), [ADR 0025](0025-stage-1d-a-stream-commits-with-its-first-byte.md) (a failure
  before the first byte is answered with an envelope)
- Supersedes: in decision 8 of [ADR 0009](0009-stage-1b-streaming-relay.md), the idle budget's coverage
  of the wait before a stream's first byte, and the classifier's order

## Context

Stage 1 gave each runtime four timeout phases: connect (5 seconds), response headers (30 seconds), idle
stream (60 seconds), and total (10 minutes). The idle budget was re-armed before every read of a
stream, the first included, so one budget bounded two different waits: the wait from the runtime's
headers to its first byte, and the gap between two reads after it.

The first trace report met the difference (its finding 2). OpenCode's first agent request to LM Studio
had its response headers within 6 milliseconds and then nothing: it waited about 38 seconds behind the
session's title request, because LM Studio served one request at a time, and then processed its prompt
at about 390 tokens per second, falling to 320. At 60,011 milliseconds the idle budget fired, after
8,192 prompt tokens. OpenCode retried without saying so, and the retry took 13.2 seconds. ADR 0025
turned the reset the client received into a `504`; the cut itself stayed.

On local hardware the wait before a first byte — the request queued behind another, a model loading, a
prompt processed, reasoning the runtime does not stream — legitimately takes minutes. A stream that
started and then stopped is a different signal, and it is the one an idle budget is for. A single
budget is either too short for the first wait or too long for the second.

The same wait falls on the response-header budget for a runtime that sends its headers late. LM Studio
sends a non-streamed completion's headers only with the finished answer (`docs/CLIENTS.md`). Ollama's
OpenAI-compatible server writes a stream's headers with its first chunk; that comes from reading its
HTTP server's source, where Go's `net/http` sends headers with the first body write, and not from a
trace report. At 30 seconds, a long prompt is cut there too.

The maintainer decided this after the 0.2.0-alpha release, as a change to Stage 1 behaviour made while
Stage 2A is the stage in progress. `GET /api/v1/system` keeps reporting `2A`.

## Decisions

### 1. A stream's wait for its first byte has its own budget

A new per-runtime setting, `timeouts:firstByte`, 5 minutes by default, bounds the wait on a stream from
the runtime's response headers to the first byte of its body. Its cancellation source is linked to the
total budget, and through it to the client, because until the first byte a read waits on this budget
alone. It is armed when the headers arrive, beside the response-header budget's disarm, and disarmed
by the first read that returns bytes. A response that is not streamed has no first-byte budget: its
body is bounded by the total, as before.

### 2. The idle budget covers only the gaps after the first byte

`timeouts:idleStream`, still 60 seconds, is armed before every read after the first byte and not before
it, so it measures what it was meant to: a stream that started and then stopped.

### 3. The phase is `FirstByte`

A first-byte timeout is recorded as `timeout_fired` with `timeout.phase: FirstByte`, a new member of
`TimeoutPhase` with the value 5. It can only happen before the client's status is committed, so it is
always answered with the `504` `agentsplice_upstream_timeout` envelope and every correlation header
(ADR 0025). An `IdleStream` timeout can now only happen after the first byte, so it always abandons a
response that has started and records no error code.

### 4. The response-header budget defaults to 5 minutes

`timeouts:responseHeaders` goes from 30 seconds to 5 minutes, so a runtime that sends its headers only
when it starts answering — or, for a request that is not streamed, when it has finished — has the same
allowance as one that sends them at once. Model discovery uses the same budget and inherits it: a
runtime that accepts a connection and never answers its model list now holds discovery, and the
completions waiting on it, for up to 5 minutes instead of 30 seconds. That is accepted; a budget of its
own for discovery waits for evidence that it is needed.

### 5. The new budget is validated like the others

`firstByte` must be greater than zero and must not exceed `total`, the rule every phase follows; a
phase equal to the total is allowed, as before. `connect` and `total` are unchanged.

### 6. The classifier checks the latest phase first

After the client and the total, the classifier checks idle, then first byte, then response headers,
then falls back to connect. A budget whose timer elapses in the instant its phase ends — headers, or a
first byte, arriving just as time ran out — stays signalled into the next phase, because disarming a
source that has already fired does nothing. Only the budget of the phase in progress can have ended a
read, and it is the latest one signalled, so checking an earlier phase first would report a stall in the
middle of a stream as a slow start. It is ADR 0009 decision 8's reason for checking idle before headers,
extended by one phase.

### 7. Nothing else changes

No error code, error type, metric name, span attribute, API field, OpenAPI schema, or dashboard text
changes: none of them carries the phase. No relayed byte changes.

## Consequences

- The trace report's request would have completed: its first byte came at about a minute, inside five.
  That is inferred from the report's recorded timings, not from a session run again.
- A runtime that stalls before its first byte is now detected after 5 minutes rather than 60 seconds; a
  stall after it, after 60 seconds as before.
- **A configuration whose `total` is under 5 minutes, and which does not also set `responseHeaders` and
  `firstByte`, no longer starts.** The defaults then exceed the total, and validation names each phase
  that does.
- A completion slot — 64 at once, by `limits:maxConcurrentCompletions` — can now be held for up to 5
  minutes before a stream's first byte rather than 60 seconds, still within `total`. A client that
  leaves frees its slot at once, because the budget is linked to the client. The runtime is one the
  operator configured; no client can choose it.
- A client's own wait for response headers can end first, because the gateway sends the status with the
  first byte (ADR 0025). `docs/CLIENTS.md` says to raise the client's timeout along with the gateway's.
- A record made before this decision names `IdleStream` for the wait before the first byte. Its timeline
  has no `first_upstream_byte`, which tells it apart; the trace report's `d8f5a743` is one.
- Each stream has one more timer, and still no allocation per read.
- Known limitation: a runtime that sends anything — an empty event, a role-only chunk, a keepalive
  comment — before it processes the prompt ends the first-byte wait early, and its processing then runs
  against the idle budget. LM Studio sends nothing before its output.

## Tests proving these decisions

Each was run against its reintroduced defect and failed; the changelog lists them.

- `UpstreamStreamBudgetTests`, on the provider with a scripted body whose reads end only when the token
  they were given is cancelled: nothing after the headers is a `FirstByte` timeout; a stall after the
  first byte is an `IdleStream` one; a first byte that arrives as its budget elapses leaves a later stall
  in the idle phase; and a client that leaves before the first byte is a cancellation.
- `ChatCompletionStreamBudgetTests`, through the host against the fake upstream: a stream that waits five
  idle budgets for its first byte completes; a stream longer than its idle budget, whose gaps are
  shorter, completes; a client that leaves during the first-byte wait aborts the runtime's request and is
  recorded as cancelled, with the headers received and no timeout; and a stream whose headers and first
  byte each take most of their budgets completes.
- `ChatCompletionStreamFailureTests`: a stall before the first byte is answered with a `504` and records
  `FirstByte`, with an idle budget shorter than the first-byte one; a stall after the first byte records
  `IdleStream` and neither earlier phase; a total that elapses before the first byte records `Total`.
- `AgentSpliceOptionsValidatorTests`, `ConfigurationDefaultsTests`, `NullSettingValidatorTests`,
  `RuntimeRegistryTests`, and `HostStartupTests`: the setting's validation, the defaults, a null refused,
  its projection onto the runtime, and the shipped values.

## Alternatives considered

- **One budget from the request to the first byte,** replacing the response-header one: a single
  allowance for "how long the runtime may take to start", whenever it sends its headers. It would lose
  the difference between a runtime that never answered and one that answered and never started, change
  what `responseHeaders` means for a request that is not streamed, and break every configuration that
  sets it.
- **The first output event as the boundary.** Truer to a runtime that sends an empty event early, but the
  provider does not parse SSE, so the signal would have to come from the protocol layer, which is more
  invasive. LM Studio sends nothing before its output.
- **Raising `idleStream` to 5 minutes.** One line, but a stall in the middle of a stream would take five
  minutes to notice, and the timeline still could not say which wait it was.
- **Keeping 60 seconds and documenting it.** The trace report shows what that costs: a silent retry, and
  prompt processing thrown away.
- **Running the budgets on `TimeProvider`,** so tests could drive them with virtual time. Worth doing,
  and a separate change.
