# ADR 0026 — Stage 1D: a runtime error inside a success is recorded as the runtime's failure

- Status: Accepted
- Date: 2026-10-18
- Related: [ADR 0009](0009-stage-1b-streaming-relay.md) (decision 5: a malformed payload is observed,
  not failed), [ADR 0013](0013-stage-1c-metadata-store.md) (the store and its list),
  [ADR 0023](0023-stage-1d-the-first-dated-trace-report.md) (the trace report whose finding 1 this
  closes), [ADR 0025](0025-stage-1d-a-stream-commits-with-its-first-byte.md) (how an exchange ends for
  the client)
- Refines: decision 12 of [ADR 0008](0008-stage-1a-transparent-request-path.md), where success and
  error were classified from the upstream status class alone

## Context

LM Studio could not render a model's chat template for OpenCode's agent requests. It answered `200`
`text/event-stream`, sent one event carrying `{"error":{...}}`, and closed the stream without `[DONE]`.
AgentSplice relayed the event unchanged, which a transparent gateway should, and OpenCode reported
the error. The exchange was recorded as `completed` with termination `normal_completion`, no failure
class, and no error code — exactly how a stream that worked is recorded. The metrics counted it in the
same `2xx` series as a success, the span was not marked, and the dashboard painted it with the green
`completed` pill (the first trace report's finding 1). A buffered `200` whose body is an error object
was recorded the same way, and if the runtime sent `[DONE]` after the error even the absent terminator,
the one sign the record carried, disappeared.

The roadmap asked for such an error to be recorded "as the failure it is, not as a completion".
`completed` already has a meaning the domain relies on: the transport cycle finished, and AgentSplice
did not fail (ADR 0008 decision 12). A relayed `500` is completed for that reason, with no failure
class. ADR 0008 also rejected a failure class for a condition that is not AgentSplice's failure, and
every failure class forces a published error code that no envelope would carry here.

## Decisions

### 1. The protocol's own error shape is recognised, never read

An event, or a body, whose JSON is an object with a top-level `error` member that is neither null nor
false is the runtime reporting an error. That is what clients act on: the OpenAI SDKs raise on a chunk
whose `error` is present and truthy, a string included, and the AI SDK's OpenAI-compatible provider
rejects one. It is recognised by structure, the way `[DONE]` and `usage` are. Nothing in the member is
read: its message can echo the prompt or a template, and recording it would put content in the
evidence. A null or false `error`, an error nested under `choices`, and model output that happens to
spell one are not this shape.

Review found that the first version counted only an object. That relied on LM Studio sending one, and
no capture of its bytes exists to say which it sends: content capture was off for the trace report, and
the fixtures here are written to the shape, not copied from LM Studio.

The bytes are relayed unchanged, before anything looks at them, as every event is. The client's own
parser stays the authority on what the error means to the client.

### 2. The exchange still completes, and says the runtime failed

`status` stays `completed`, with no failure class and no error code, because AgentSplice did not fail
and no envelope carried a code. What the record gains:

- `responseSummary.upstreamErrorReported`, in both modes;
- the `upstream_error_reported` observation, stamped when the event was interpreted, or when a body was
  read;
- for a stream, the termination `upstream_error`, which outranks a malformed event, the protocol's
  terminator, and a clean close. Whether a terminator was observed survives in the completion details.

### 3. One rule says whether the runtime succeeded

`UpstreamOutcome` is `success` for a 2xx whose answer was read and carried no error, `error_status`
for any status outside 2xx, or `error_reported` for a 2xx whose answer carried the error shape. A
status outranks the body, because a status outside 2xx already says the runtime did not answer the
request; a redirect is relayed rather than followed, so it is what the client received. It is defined
only for a completed exchange that heard a response: one that failed or was cancelled already says
why, and the runtime's answer never completed to be judged. A 2xx whose answer was never read — no
summary could be built, as for an HTML page or an empty body — has no outcome: a success is a claim
about the answer, and unknown stays unknown.

`UpstreamOutcomeRules.Classify` is the one implementation, used by the exchange, by the metrics, and by
the store when it lists rows, so a runtime's failure cannot be counted one way and listed another. The
store classifies on read from what a row already holds. A row written before this decision holds no
answer to whether the runtime reported an error, because those builds never looked, so a 2xx among
them has no outcome rather than a success; a status outside 2xx still says enough on its own. Review
found the first version reading the missing fact as "no error", which listed the trace report's own
template failure as a success after an upgrade.

### 4. The outcome is a metric dimension, a span attribute, and a list field

`agentsplice.upstream.outcome` is attached to every exchange series of a completed exchange that heard a
response, and is what the runtime's success is classified from. The exchange span carries the same
attribute, and its status is `Error` when the outcome is an error as well as when AgentSplice failed;
`error.type` stays AgentSplice's own. `ExchangeSummary.upstreamOutcome` puts it on the list and the
detail.

### 5. The dashboard stops painting `completed` as a success

The `completed` pill is neutral, and an outcome pill beside it carries success or failure. The exchange
page shows the response summary — choices, finish reasons, usage reported, tool calls, and now the
reported error — which was in the API and nowhere on screen.

## Consequences

- An exchange like the trace report's finding 1 is now recorded as `completed`, `upstream_error`,
  `upstreamOutcome: error_reported`, with an `upstream_error_reported` boundary, and is counted and
  shown as the runtime's failure.
- The `StreamTermination` vocabulary gains `upstream_error`, the observation vocabulary gains
  `upstream_error_reported`, the response summary gains `upstreamErrorReported`, the metrics gain one
  dimension, and the list gains one field. No error code, error type, failure class, or configuration
  key changes.
- A relayed status outside 2xx is now `error_status` on every surface; before, only its status class
  said so.
- The roadmap's line is reworded: "as the runtime's failure, distinct from a success in the record, the
  metrics, and the dashboard", rather than "not as a completion", which contradicted what `completed`
  means.

## Tests proving these decisions

Each was run against its reintroduced defect and failed; the changelog lists them.

- `OpenAiStreamEventInterpreterTests` and `ChatCompletionResponseCodecTests`: a top-level `error` is
  recognised in an event and in a body, an object, a string, an array, or `true`; a null or false
  `error`, a nested error, and model output that spells one are not; a chunk with output and an error
  reports both.
- `StreamRelayBoundaryTests`: an error then end of stream ends as `upstream_error` with the terminator
  absent; an error then `[DONE]` still does, with the terminator observed; an error outranks a malformed
  event; the boundary is stamped after the read that brought it.
- `UpstreamOutcomeRulesTests`: the classification for each status and report — a redirect among the
  errors — and none for an exchange that did not complete, heard no response, or had its answer never
  read.
- `RuntimeReportedErrorTests`: the error shape relayed byte for byte and recorded as the runtime's
  error, in a stream, in a buffered body, and in an ordinary body sent in answer to a streaming request,
  and the error's message in neither the record's observations and summary nor any log.
- `ExchangeApiTests`: the list and the detail carry the outcome, a relayed error status and a success are
  listed apart, and a request refused before its model was known, a 2xx nobody could read, and a 2xx row
  stored before the fact existed have none.
- `TelemetryExportTests`: the exported metrics carry the dimension and the span is marked `Error`, with
  no `error.type`.
- `OpenApiContractTests`: the outcome's schema enum is the domain's.
- The dashboard's `outcome.test.tsx`: the list shows a success, an error status, and a reported error
  apart, and no outcome for an exchange that did not complete; the exchange page shows the response
  summary that says the runtime reported an error.

## Alternatives considered

- **`failed`, with a new failure class.** It matches the roadmap's old wording, and shows in every
  status filter. But ADR 0008 rejected a failure class for a runtime's condition, the class would force
  a published code no envelope carries, and a relayed `500` stays `completed`: two runtime failures
  recorded two ways.
- **Reuse `StreamTermination.UpstreamCancelled`.** No vocabulary change, but it conflates reporting an
  error with aborting a response, a distinction ADR 0009 reserved.
- **Call it a malformed event.** The event is valid protocol, and that is the misattribution
  `MalformedEvent` exists to avoid.
- **Infer failure from no choices, no finish reason, and no terminator.** It turns an absence into a
  claim and catches empty streams that did not fail.
- **Bucket the error's `type` or `code` against a closed vocabulary.** Possible without reading the
  message, but runtimes disagree on both, and presence is what every consumer needs first. Left for a
  conformance stage that has fixtures from more than one runtime.
- **`event: error` frames.** The Responses API's convention, which Chat Completions runtimes do not use.
