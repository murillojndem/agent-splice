# Trace report: OpenCode through AgentSplice to LM Studio, 2026-10-17

The first dated trace report (`docs/SPECIFICATION.md` section 22, criterion 17; ADR 0023). OpenCode
1.18.7 ran a small coding task through AgentSplice 0.2.0-alpha to LM Studio 0.4.20 on one Windows
machine, with two models, on 2026-10-17 between 01:02 and 01:25 UTC. AgentSplice recorded every
exchange, and `exchanges.json` holds what its administrative API answered for each one, unchanged.

**This report makes no compatibility claim.** It records what one machine did, once per model and
client environment. It is not a conformance run, and it evaluates neither the models nor the task's
result. An exchange recorded as `completed` is not evidence that anything worked — finding 1 is an
example.

## Summary

- **One model never got past OpenCode's first agent step.** LM Studio could not render the chat
  template embedded in the `qwen3-coder-30b-apex` file for either of OpenCode's agent requests, and
  reported that inside an HTTP `200` stream. AgentSplice recorded those exchanges as `completed` with
  `normal_completion`; the evidence that they were not is in the record, but not in its status
  (finding 1).
- **The other model finished the task, after a stream that AgentSplice cut off.** With
  `qwen3.6-35b-a3b`, OpenCode's first agent request waited 38 seconds behind its own title request and
  then spent the rest of a minute in prompt processing. AgentSplice's `idleStream` timeout fired at 60
  seconds, before the first byte: OpenCode received no response at all and retried, which its log
  records and its session output does not, and the retry succeeded, partly from what LM Studio had
  cached during the attempt that was cut off (finding 2). The record names an error code the client
  never received (finding 3).
- **Cancellation reached the runtime.** When a session failed, OpenCode abandoned a request still in
  flight; AgentSplice recorded `client_cancelled`, and LM Studio's log shows it stopped that task
  (finding 4).
- **Every exchange that streamed recorded every boundary, and AgentSplice's own share was small.** It
  forwarded each first event within 2 milliseconds of receiving it, its usage matched what OpenCode
  counted, and its tool-call counts matched the tools OpenCode ran. The exchange that timed out
  records nothing of how its client's response ended (findings 3, 5, and 6).
- **No prompt or output reached AgentSplice's log, store, or evidence** (finding 7).

## What was run

**Gateway.** The Windows package CI built for commit `b17d169ec0d6cbbfca1a07d734b698cbaa0817eb`, in
run 38987013797, downloaded from that run and checked against its `SHA256SUMS`, started with its
shipped settings and an administrative token. Content capture was off, as it is by default. The
artifact needs a GitHub sign-in to download and expires on 2027-01-15; the commit and the package's
digest in `report.json` are what last.

**Runtime.** LM Studio 0.4.20 with its llama.cpp ROCm engine 2.31.2, serving on `127.0.0.1:1234`, one
model loaded at a time with full GPU offload:

| Model | File | Context | Parallel | Flash attention | KV cache |
| --- | --- | --- | --- | --- | --- |
| `qwen3-coder-30b-apex` | `mudler/Qwen3-Coder-30B-APEX-GGUF`, APEX I-Compact | 32,768 | 4, LM Studio's default | unknown | unknown |
| `qwen3.6-35b-a3b` | `unsloth/Qwen3.6-35B-A3B-GGUF`, UD-IQ3_XXS | 262,144 | 1 | on | `q8_0` |

The second model was loaded with the configuration saved for it in LM Studio, which also sets 32
context checkpoints. The values that read "unknown" were neither set nor reported anywhere this
report could read them.

**Machine.** Windows 11 Pro 10.0.26200, AMD Ryzen 7 5800X3D, 64 GiB of memory, AMD Radeon RX 7900 XT
with 20 GiB and driver 32.0.32015.2008. Every value, and where it came from, is in `report.json`.

**Client.** OpenCode 1.18.7, as `opencode run --format json --model agentsplice/<model id> "<prompt>"`,
from a new directory holding `calc.py` and an `opencode.json` made from
`examples/opencode/opencode.json`: the AgentSplice provider, the loaded model as both `model` and
`small_model`, `bash` and `webfetch` denied, `edit` allowed, sharing disabled. The file is in
`report.json`; attempts 1 and 2 gave the model another display name, which OpenCode does not send.

**Task.** The same prompt every time, with a random marker that was different in each attempt:

> calc.py's mean() returns the wrong result. Fix the bug, then add test_calc.py with unittest tests for
> mean() and clamp(). Do not run any commands. Session marker: trace-canary-…

`calc.py` started as:

```python
"""Small statistics helpers."""


def mean(values):
    """Return the arithmetic mean of a non-empty sequence of numbers."""
    total = 0
    for value in values:
        total += value
    return total / (len(values) - 1)


def clamp(value, low, high):
    """Return value limited to the closed range [low, high]."""
    return max(low, min(value, high))
```

**Attempts.**

| Attempt | Model | Client environment | Exchanges | OpenCode |
| --- | --- | --- | --- | --- |
| 1 | `qwen3-coder-30b-apex` | with a terminal integration | 2 | exited 1: `UnknownError` |
| 2 | `qwen3.6-35b-a3b` | with a terminal integration | 0 | stopped by hand |
| 3 | `qwen3.6-35b-a3b` | without it | 6 | exited 0 after four agent steps |
| 4 | `qwen3-coder-30b-apex` | without it | 2 | exited 1: `UnknownError` |

The account runs OpenCode inside a terminal integration that points `OPENCODE_CONFIG_DIR` at a
configuration directory of its own, which holds a plugin. In attempt 2, OpenCode logged its own
initialization and then nothing for 14 minutes, with no connection open; it never reached
AgentSplice, and it was stopped. Attempts 3 and 4 unset that directory and the integration's
variables, so that no terminal integration was part of what ran.

Every agent request also carried, in its system message, the descriptions of 18 skills OpenCode found:
17 installed for the account and one built in. They are 15,322 characters — about 63% of that system
message, and between 35% and 39% of each agent request's body — and the title requests carry none. A reproduction on another account
sends a different, most likely much shorter, prompt, so its token counts, and the timing finding 2
rests on, can differ.

## Exchanges

Durations are AgentSplice's own (`measured`): **headers** and **first byte** from the moment it opened
the request to LM Studio, **first semantic event** and **total** from the moment it accepted the client's.
Tokens are as LM Studio reported them (`upstream_reported`).

| Attempt | Exchange | Request | Status, termination | Headers | First byte | First semantic event | Total | Finish | Tool calls | Prompt / completion tokens |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `0cefe890` | title, no tools | `cancelled`, `client_cancelled` at 1,324 ms | 14 ms | — | — | — | — | — | — |
| 1 | `2c1a02eb` | agent, 8 tools | `completed`, `normal_completion` | 6 ms | 38 ms | — | 51 ms | none | 0 | not reported |
| 3 | `af90733b` | title, no tools | `completed`, `protocol_terminator_received` | 7 ms | 2,697 ms | 2,704 ms | 38,637 ms | `stop` | 0 | 600 / 1,881 |
| 3 | `d8f5a743` | agent, 8 tools | `failed`, `timeout` at 60,011 ms | 5 ms | — | — | — | — | — | — |
| 3 | `dc091e35` | the same request again | `completed`, `protocol_terminator_received` | 7 ms | 9,133 ms | 9,137 ms | 13,211 ms | `tool_calls` | 2 | 9,828 / 125 |
| 3 | `cf1fdd40` | agent, 8 tools, 5 messages | `completed`, `protocol_terminator_received` | 9 ms | 740 ms | 741 ms | 4,273 ms | `tool_calls` | 1 | 10,048 / 114 |
| 3 | `a49c74bd` | agent, 8 tools, 7 messages | `completed`, `protocol_terminator_received` | 6 ms | 1,398 ms | 1,398 ms | 24,907 ms | `tool_calls` | 2 | 10,411 / 730 |
| 3 | `33df5efa` | agent, 8 tools, 10 messages | `completed`, `protocol_terminator_received` | 7 ms | 538 ms | 540 ms | 4,091 ms | `stop` | 0 | 11,170 / 104 |
| 4 | `14a88306` | title, no tools | `cancelled`, `client_cancelled` at 1,006 ms | 6 ms | — | — | — | — | — | — |
| 4 | `8f1665ea` | agent, 8 tools | `completed`, `normal_completion` | 7 ms | 35 ms | — | 36 ms | none | 0 | not reported |

AgentSplice also derived the generation throughput of each stream that produced output: the
completion tokens LM Studio reported, over the window it measured itself from the first semantic event
to the end of the stream — 52.3 tokens per second for the title, and between 29.3 and 32.3 for the agent
steps. It labels them `upstream_reported`, the weaker of their two inputs. It records no
prompt-processing throughput, by design: nothing it observes marks where prompt processing ends
(`docs/OBSERVABILITY.md`). LM Studio's own log does report it — 237.5 tokens per second for the title's
600-token prompt, and 214.3 for the 1,636 tokens the retry evaluated — and shows each later agent step
evaluating only the new end of its prompt: 96, 250, and 30 tokens. Those are the runtime's numbers, not
AgentSplice's evidence.

## Findings

### 1. A runtime error inside a `200` is recorded as a completion

In attempts 1 and 4, LM Studio answered OpenCode's first agent request `200` with `text/event-stream`,
sent one event of 1,098 bytes, and closed the stream without `[DONE]`. Its own log names the cause:
`Error rendering prompt with jinja template: "Unknown StringValue filter: safe"`. The chat template
embedded in this GGUF file applies `tojson | safe` in two places: to a tool parameter whose schema
holds a nested mapping beyond its type, description, enum, and required fields, and to a tool's
declared return. Of the eight tools OpenCode declared, only `todowrite` has such a parameter, and LM
Studio's template engine has no `safe` filter. The title request, which declares no tools, rendered.
Both agent requests failed the same way, in both client environments, so neither session got past its
first agent step. This says nothing about the model with other tools, with another copy of its
template, or with a template LM Studio is told to use instead; none was tried.

AgentSplice relayed the event unchanged, which is what a transparent gateway should do, and OpenCode
reported `UnknownError`. But the exchange is recorded as `completed`, with termination
`normal_completion`, no failure class, and no error code. What says otherwise is all in the record —
`choiceCount: 0`, no finish reason, no usage, and `stream.terminator: absent` on `client_completed` —
and `docs/CLIENTS.md` already describes LM Studio reporting errors this way. It is still exactly the
case the project's rule names: a `200` recorded as if it proved something worked. **Follow-up:** recognize
an error object in a stream and record the exchange as the failure it is.

### 2. A request that waited and processed for a minute met `idleStream`

OpenCode sent its title request and its first agent request within a second of each other. With
`parallel` 1, LM Studio served them one after the other: the title — `qwen3.6-35b-a3b` reasons before
it answers — took 1,881 tokens and 38.6 seconds. The agent request had its response headers within 6
milliseconds and then nothing, because LM Studio sends nothing while a request waits or while it
processes the prompt. Its log shows it starting on the request at 01:22:19 UTC, as the title finished,
about 38 seconds in, and then processing prompt tokens at about 390 per second at first, falling to
about 320 as the prompt grew. At 60,011 milliseconds AgentSplice's `idleStream` timeout fired, with
`timeout.phase: IdleStream`, and it closed the request to LM Studio, which stopped after 8,192 prompt
tokens.

OpenCode received no response: its log records `AI_APICallError: Cannot connect to API: The socket
connection was closed unexpectedly`. Two seconds later it sent the request again, and that one
finished in 13.2 seconds. LM Studio reported 9,828 prompt tokens for it and logged evaluating only
1,636 of them: the other 8,192 are exactly the tokens the cut-off attempt had processed. That last
part is LM Studio's log, not AgentSplice's evidence; AgentSplice reads no runtime log in this stage.

Three things follow. This is the gap ADR 0019 records and the roadmap lists, met in a real session
rather than a test: a stream that times out before its first byte reaches the client as a bare
connection reset. OpenCode's session output shows nothing of it — the retry is silent there — though
its log records the closed socket and the new request. And AgentSplice's timeline shows the minute
only as silence after the headers: it cannot tell a request waiting in the runtime's queue from one
being processed, and LM Studio's log is what shows it was both. With a runtime that serves one request
at a time, raising `timeouts:idleStream` as `docs/CLIENTS.md` shows is what avoids the cut. About two
fifths of this request were the skill descriptions OpenCode sent; a shorter prompt would have been
processed sooner, though it would have waited for the title all the same.

### 3. The error code of the cut-off exchange is one the client never received

Exchange `d8f5a743` records `errorCode: agentsplice_upstream_timeout`. The field is documented as the
code the client received, but the client received nothing: the exchange forwarded 0 bytes, has no
`first_client_event_flushed`, and OpenCode logged a closed socket. Its timeline ends `timeout_fired`,
`metadata_queued`, `persistence_completed`: nothing records how the client's response ended, which is
why this finding rests on OpenCode's log. The record is right about what happened and wrong about what
was delivered. **Follow-up:** record an error code only when an envelope carrying it was sent, or close
the ADR 0019 gap so that it always is.

### 4. Cancellation propagated to the runtime

In attempts 1 and 4, OpenCode abandoned its title request about a second in, when the session failed.
Both exchanges record `client_cancelled` with `cancellation.source: client`, termination
`client_cancelled`, and failure class `request_cancelled`. LM Studio's log shows each time `Client
disconnected. Stopping generation` and `stop: cancel task`: the cancellation reached the task
processing the prompt.

### 5. The timeline holds every boundary of a stream, and the gateway's share is small

Each exchange that streamed records, in order: `request_accepted`, `request_body_read`,
`validation_completed`, `structural_summary_created`, `model_resolved`, `upstream_request_opened`,
`upstream_headers_received`, `first_upstream_byte`, `first_client_event_flushed`,
`first_decoded_event`, `first_semantic_event` when there was one, a `native_tool_call_observed` for
each tool call, `upstream_completed`, `client_completed`, `metadata_queued`, and
`persistence_completed`. A new connection to LM Studio adds `upstream_connection_started` and
`upstream_connection_established`, and a pooled one does not, so most exchanges have neither. The
error streams of finding 1 have `first_decoded_event` and no `first_semantic_event`, which is right:
an error object is not output. The exchange that timed out is the exception finding 3 describes.

AgentSplice flushed each stream's first event to OpenCode within 2 milliseconds of receiving it from LM
Studio, and for six of the seven within a quarter of a millisecond. Between accepting a request and
opening it to LM Studio it spent under 7 milliseconds, except on the first request after it started,
which took 41. Writing the metadata, after the response, took 4 to 10 milliseconds, except for the first
two exchanges after startup, which took 272 and 333.

### 6. Usage and tool calls agree with the client

For each of the four agent steps of attempt 3, the prompt and completion tokens AgentSplice recorded
as `upstream_reported` are the input, and the output plus reasoning, that OpenCode recorded for the
same step — for the first, 9,828, and 61 + 64 = 125. The native tool calls AgentSplice counted, 2, 1,
2, and 0, are the tools OpenCode then ran: two `glob`, one `read`, an `edit` and a `write`, and none.
The finish reasons agree as well. The title's 1,881 completion tokens include whatever the model
reasoned before it answered: AgentSplice records completion tokens as reported, and does not split
reasoning from the answer.

### 7. Content stayed out

Each attempt's prompt carried its own random marker. None of the four markers, and none of the words
`calc.py`, `unittest`, or `mean()`, appear in the gateway's log, its SQLite store — the database, its
write-ahead log, and its shared-memory file — or `exchanges.json`. The same search finds the three
markers that were sent, attempt 2 having sent nothing, and the words, in LM Studio's server log, which
on this machine is set to record request bodies; it finds the words in OpenCode's own output too.
Content is the runtime's and the client's to keep, and AgentSplice kept none of it. Every exchange
records `contentRetentionState: metadata_only`.

### 8. The gateway reported the wrong stage

`GET /api/v1/system` answered `stage: "1C"`. The build is Stage 1D's.

## What this report does not show

- Whether either model or runtime works with OpenCode in general: one machine, one task, one attempt
  per model and client environment, and no conformance suite.
- Whether the task's result is correct. OpenCode changed `calc.py` and wrote `test_calc.py`; nothing
  here ran or judged them.
- Throughput or latency for anyone else's hardware, or for another account's prompt. The numbers are
  this machine's, warm, with the configurations above.
- Anything about LM Studio's own logging beyond what it wrote here.

## Reproducing

1. Download the `agentsplice-win-x64` artifact of CI run 38987013797, signed in to GitHub and before it
   expires, check it against its `SHA256SUMS` and the digest in `report.json`, extract it, and start
   `AgentSplice.Api.exe` with
   `--agentsplice:administration:apiKeyEnvironmentVariable=AGENTSPLICE_ADMIN_API_KEY` and a token in
   that variable.
2. Start LM Studio's server with `lms server start --port 1234 --bind 127.0.0.1`, and load the model:
   `lms load qwen3-coder-30b-apex --gpu max --context-length 32768 -y`, or `lms load qwen3.6-35b-a3b
   --gpu max -y` with the configuration in `report.json` saved for it, its 32 context checkpoints
   included.
3. In a new directory holding `calc.py` and `opencode.json` as above, with `OPENCODE_CONFIG_DIR` unset
   and nothing on standard input, run `opencode run --format json --model agentsplice/<model id>
   "<prompt>"`: in Git Bash, which these attempts used, end the line with `< /dev/null`; in PowerShell,
   start it with `$null |`.
4. Export the evidence, with `Authorization: Bearer <token>`: `GET /api/v1/system`, every page of
   `GET /api/v1/exchanges`, and for each exchange `GET /api/v1/exchanges/{id}` and
   `GET /api/v1/exchanges/{id}/timeline`. `exchanges.json` holds them as `system`, `list`, and
   `exchanges[].detail` and `exchanges[].timeline`, beside `exportedAt`, the one value the export adds.

## Files

- `report.json` — the environment and the scenario, each value with where it came from, and the
  attempts.
- `exchanges.json` — what the administrative API answered: its system information, every page of its
  exchange list, and for every exchange listed, its detail and its timeline, with the time of the
  export.

## Notes

- **2026-10-18.** Findings 2 and 3 were fixed after this report (ADR 0025). A later build answers a
  stream that fails before its first byte with a `504` or `502` and every correlation header, records an
  error code only for an envelope it wrote, and ends every timeline with `client_completed`,
  `client_cancelled`, or `client_response_abandoned`. The records here are what the build this report
  ran wrote: exchange `d8f5a743` names a code its client never received and has no client-side
  boundary, which is how a record made before ADR 0025 can be told apart from a later one.
- **2026-10-18.** Finding 1 was fixed after this report (ADR 0026). A later build still records an
  exchange like attempt 1's first agent request as `completed`, because the gateway did not fail, and
  records the runtime's failure beside it: termination `upstream_error`, `upstreamOutcome:
  error_reported`, and an `upstream_error_reported` boundary. The records here keep `normal_completion`
  and no outcome, as the build that ran wrote them.
