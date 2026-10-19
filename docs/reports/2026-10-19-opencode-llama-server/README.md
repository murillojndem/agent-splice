# Trace report: OpenCode through AgentSplice to llama-server, 2026-10-19

The second dated trace report (ADR 0023). OpenCode 1.18.7 ran the first report's coding task through
AgentSplice 0.2.1-alpha to llama.cpp's `llama-server`, build b10662. It used the same Windows machine
and the same two model files, on 2026-10-19 between 20:18 and 20:22 UTC. The gateway was the
released package, unchanged, with its only runtime provider, `lmstudio`, pointed at llama-server.
AgentSplice recorded every exchange, and `exchanges.json` holds what its administrative API answered
for each one, unchanged.

**This report makes no compatibility claim.** It records what one machine did, once per model. It is
not a conformance run, and it evaluates neither the models, the runtime, nor the task's result. An
exchange recorded as `completed` is not evidence that anything worked.

## Summary

- **The shipped provider served llama-server without a change.** Discovery found each model under the
  alias llama-server was given. All eleven exchanges completed with `[DONE]` and every boundary of
  their timeline. The usage and tool calls AgentSplice recorded agree with what OpenCode counted
  (findings 1, 4, and 5).
- **llama-server rendered the chat template LM Studio could not.** It was the same
  `qwen3-coder-30b-apex` file. OpenCode got through six agent steps and finished the task, and
  llama-server's prompt log shows the `tojson | safe` that stopped LM Studio applied to `todowrite`'s
  parameter (finding 2).
- **On llama-server, a stream's headers wait for a free slot.**
  - With one slot, the first agent step's headers came 7.8 seconds in, when the title released the
    slot. Its first byte came after 5.8 more seconds of prompt processing. The time in the queue fell
    on the `responseHeaders` budget and the prompt on `firstByte`; on LM Studio both had fallen on the
    wait for the first byte.
  - With four slots, nothing waited for the title, but the title's ten tokens took 6.5 seconds beside
    the agent step's prompt (finding 3).
- **The gateway's own share stayed small.** It flushed ten of the eleven first events to the client
  within a quarter of a millisecond of receiving them (finding 5).
- **No prompt or output reached AgentSplice's log, store, or evidence** (finding 6).

## What was run

**Gateway.** The Windows package of release `v0.2.1-alpha`, `agentsplice-0.2.1-alpha-win-x64.zip`,
which CI built for commit `39dfc7819d195beb5288621ee84d76cb7e893c9c` in release workflow run
39214008235.

- It was downloaded from the release, checked against the release's `SHA256SUMS` and its attestation,
  and extracted into a new folder, so its store began empty.
- It ran with its shipped settings and three arguments:
  - the administrative token's variable, as in the first report;
  - `--agentsplice:runtimes:0:baseUrl=http://127.0.0.1:8080/v1`, which points the shipped runtime at
    llama-server;
  - `--agentsplice:runtimes:0:id=llama-server-local`, so that the records name the server they
    describe.
- The runtime's provider stayed `lmstudio`, the only one this build has. Each exchange records
  `provider.type: lmstudio` beside `runtime.id: llama-server-local`.
- Content capture was off, as it is by default.
- The shipped timeouts (ADR 0028):
  - 5 seconds to connect;
  - 5 minutes for the response headers, and another 5 minutes from them to a stream's first byte;
  - 60 seconds between reads after that;
  - 10 minutes in all.

**Runtime.** llama.cpp's `llama-server`, release b10662: commit `18443257a`, published on 2026-08-27.
It is the llama.cpp release that LM Studio's ROCm engine 2.31.2 is built on, the engine the first
report ran.

- It was the release's Vulkan build for Windows, `llama-b10662-bin-win-vulkan-x64.zip`, checked against
  the digest GitHub publishes for it.
- It served on `127.0.0.1:8080`, one model at a time.
- Each model was loaded with `--alias`, so that llama-server's model list names it as the first
  report's LM Studio did.
- Each model was loaded with the settings the first report recorded for it, all on the command line.
  llama-server's log reports every value below, including the two the first report could not read:

| Model | File | Context | Parallel | Flash attention | KV cache |
| --- | --- | --- | --- | --- | --- |
| `qwen3.6-35b-a3b` | `unsloth/Qwen3.6-35B-A3B-GGUF`, UD-IQ3_XXS, with its vision projector | 262,144 | 1 | on | `q8_0` |
| `qwen3-coder-30b-apex` | `mudler/Qwen3-Coder-30B-APEX-GGUF`, APEX I-Compact | 32,768, shared by the slots | 4 | auto, resolved to on | `f16`, llama-server's default |

- **The model files.** Both are the ones the first report ran: their digests are the same.
- **Shared settings.** Both models had full GPU offload and 6 threads; the first model also had 32
  context checkpoints, as in the first report. llama-server's prompt cache, by default up to 8 GiB of
  host memory, was on for both.
- **Logging.** llama-server ran with `-lv 4`, for the load details. Its prompt logging,
  `--log-prompts-dir`, was on as a control for the marker search (finding 6).
- `report.json` holds both command lines.

Two builds would not run on this machine before this one:

- **The ROCm build.** The same release's ROCm build would have matched the first report's backend.
  It needs `hipblas.dll`, which its archive does not carry and this machine does not have, and
  llama-server listed no device.
- **The Vulkan build, at first.** It crashed while loading any model it was given, and the latest
  release's Vulkan build, b11739, crashed the same way. It ran once the implicit Vulkan layers that
  other applications install on this machine were disabled for its process, with
  `VK_LOADER_LAYERS_DISABLE=~implicit~`.

So the backend is not the first report's: LM Studio ran llama.cpp on ROCm, and this report's runtime
numbers are Vulkan's.

**Machine.** The first report's: Windows 11 Pro 10.0.26200, AMD Ryzen 7 5800X3D, 64 GiB of memory,
and an AMD Radeon RX 7900 XT with 20 GiB, driver 32.0.32015.2008, and Vulkan loader 1.4.350. Every
value, and where it came from, is in `report.json`.

**Client.** OpenCode 1.18.7, run as in the first report's attempts 3 and 4:
`opencode run --format json --model agentsplice/<model id> "<prompt>"`, with nothing on standard input.

- It ran from a new directory holding `calc.py` and an `opencode.json` made from
  `examples/opencode/opencode.json`: the AgentSplice provider, the loaded model as both `model` and
  `small_model`, `bash` and `webfetch` denied, `edit` allowed, and sharing disabled.
- The terminal integration's variables, `OPENCODE_CONFIG_DIR` among them, were unset.
- OpenCode found the same 18 skills, and every agent request carried their list in its system
  message. The list was 15,806 characters as llama-server rendered it, between 35% and 39% of each
  agent prompt. The title requests carry none.

**Task.** The first report's prompt and `calc.py`, unchanged, with a random marker that was different
in each attempt:

> calc.py's mean() returns the wrong result. Fix the bug, then add test_calc.py with unittest tests for
> mean() and clamp(). Do not run any commands. Session marker: trace-canary-…

**Attempts.**

| Attempt | Model | Slots | Exchanges | OpenCode |
| --- | --- | --- | --- | --- |
| 1 | `qwen3.6-35b-a3b` | 1 | 4 | exited 0 after three agent steps |
| 2 | `qwen3-coder-30b-apex` | 4 | 7 | exited 0 after six agent steps |

## Exchanges

Durations are AgentSplice's own (`measured`):

- **headers** and **first byte** are counted from the moment it opened the request to llama-server;
- **first semantic event** and **total** are counted from the moment it accepted the client's.

Tokens are as llama-server reported them (`upstream_reported`). Every exchange answered `200` with
`text/event-stream` and records `upstreamOutcome: success`.

| Attempt | Exchange | Request | Status, termination | Headers | First byte | First semantic event | Total | Finish | Tool calls | Prompt / completion tokens |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `63661e2b` | title, no tools | `completed`, `protocol_terminator_received` | 14 ms | 3,562 ms | 3,603 ms | 8,510 ms | `stop` | 0 | 606 / 612 |
| 1 | `cbe02b7b` | agent, 8 tools, 2 messages | `completed`, `protocol_terminator_received` | 7,830 ms | 13,589 ms | 13,591 ms | 14,579 ms | `tool_calls` | 1 | 10,146 / 119 |
| 1 | `2fd2152b` | agent, 8 tools, 4 messages | `completed`, `protocol_terminator_received` | 25 ms | 301 ms | 305 ms | 6,381 ms | `tool_calls` | 2 | 10,506 / 730 |
| 1 | `fb4e6eaf` | agent, 8 tools, 7 messages | `completed`, `protocol_terminator_received` | 29 ms | 164 ms | 164 ms | 1,020 ms | `stop` | 0 | 11,265 / 102 |
| 2 | `07c33c53` | title, no tools | `completed`, `protocol_terminator_received` | 4 ms | 1,315 ms | 1,318 ms | 7,795 ms | `stop` | 0 | 591 / 10 |
| 2 | `c2ebe45c` | agent, 8 tools, 2 messages | `completed`, `protocol_terminator_received` | 642 ms | 7,073 ms | 7,073 ms | 7,935 ms | `tool_calls` | 1 | 9,878 / 72 |
| 2 | `84071087` | agent, 8 tools, 4 messages | `completed`, `protocol_terminator_received` | 151 ms | 337 ms | 337 ms | 1,129 ms | `tool_calls` | 1 | 10,028 / 105 |
| 2 | `050a11ba` | agent, 8 tools, 6 messages | `completed`, `protocol_terminator_received` | 56 ms | 338 ms | 339 ms | 949 ms | `tool_calls` | 1 | 10,360 / 80 |
| 2 | `5605e601` | agent, 8 tools, 8 messages | `completed`, `protocol_terminator_received` | 52 ms | 807 ms | 807 ms | 2,552 ms | `tool_calls` | 1 | 10,457 / 230 |
| 2 | `3a035392` | agent, 8 tools, 10 messages | `completed`, `protocol_terminator_received` | 43 ms | 125 ms | 126 ms | 3,223 ms | `tool_calls` | 1 | 10,704 / 399 |
| 2 | `0a7d4c44` | agent, 8 tools, 12 messages | `completed`, `protocol_terminator_received` | 52 ms | 134 ms | 135 ms | 1,208 ms | `stop` | 0 | 11,121 / 139 |

AgentSplice derived each stream's generation throughput. It divided the completion tokens
llama-server reported by the window it measured itself, from the first semantic event to the end of
the stream:

- between 119 and 125 tokens per second with the first model;
- between 83 and 133 for the second model's agent steps;
- 1.5 for the second model's title, which finding 3 explains.

It labels them `upstream_reported`, the weaker of their two inputs. It records no prompt-processing
throughput, by design (`docs/OBSERVABILITY.md`).

llama-server's log does record it:

- 1,755 tokens per second for the first model's 10,146-token agent prompt;
- 1,536 for the second model's 9,878;
- 171 for the 606-token title, the first prompt after the first model loaded.

The same log shows each later agent step evaluating only the new end of its prompt and taking the
rest from the slot's cache: 242 and 30 tokens with the first model, and 102, 228, 18, 18, and 19 with
the second.

Those are the runtime's numbers, not AgentSplice's evidence, and they are Vulkan's on this machine.
Set beside the first report's, they would compare two servers and two backends at once, so this report
does not compare them.

## Findings

### 1. The shipped provider served llama-server without a change

Pointed at llama-server, the `lmstudio` provider read its model list and found each model under its
alias:

- every exchange's `model_resolved` boundary records `resolution.source: discovered`;
- each exchange's `upstreamModelId` is the id the client asked for.

llama-server's list carries more than the provider reads: an `aliases` array, a `meta` object, and a
second list under `models`. None of it reached a record. No exchange records a dropped or unknown
field.

All eleven exchanges:

- answered `200` with `text/event-stream`;
- ended with `[DONE]`: termination `protocol_terminator_received` and `stream.terminator: observed`;
- record `upstreamOutcome: success`.

The records name the runtime `llama-server-local`, which the configuration chose, and the provider
`lmstudio`, which served it.

### 2. llama-server rendered the chat template LM Studio could not

The first report's finding 1 was that LM Studio could not render the chat template embedded in
`qwen3-coder-30b-apex`'s file for OpenCode's agent requests:

- the template applies `tojson | safe` to a tool parameter whose schema holds a nested mapping;
- OpenCode's `todowrite` has such a parameter;
- LM Studio's template engine has no `safe` filter.

This report ran the same file: its digest is the first report's, and its header holds the same
template, with its two `tojson | safe`. llama-server renders chat templates with llama.cpp's own Jinja
engine, which this build enables by default. Each of the second attempt's six agent requests declared
eight tools, `todowrite` among them, and completed with native tool calls, and OpenCode finished the
task.

llama-server's prompt log, read for its structure only, shows the filter applied: `todowrite`'s `todos`
parameter is rendered with its nested schema as JSON, and no other tool's declaration has such a part.

This shows what one build of one runtime did with one template. It says nothing about LM Studio today,
which this report did not run.

### 3. On llama-server, the wait splits at the headers

In both attempts, llama-server sent a stream's response headers when one of its slots took the
request, as its log shows, and not when the request arrived. That moves part of the wait in the first
report's finding 2 from one budget to another.

**With one slot** (attempt 1, the first report's setting for this model):

1. OpenCode sent its title request and its first agent request 0.8 seconds apart, and the title took
   the slot.
2. The agent request's headers came 7.8 seconds after AgentSplice opened it, as the title finished.
   llama-server's log shows the slot taking the agent's task 0.1 seconds after releasing the title's.
3. Its first byte came 5.8 seconds later, the time llama-server's log gives for processing its 10,146
   prompt tokens.

So on this runtime, a request's time in the queue falls on the `responseHeaders` budget and its prompt
processing on `firstByte`. With LM Studio, which sent the headers at once, both fell on the wait for
the first byte.

Both budgets are 5 minutes in this build (ADR 0028), and nothing came near either. The longest wait
for a first byte was 13.6 seconds, against the first report's minute. Neither difference is the
gateway's doing:

- the title took 8.5 seconds and 612 tokens here, against 38.6 seconds and 1,881 there;
- llama-server processed the agent's prompt at 1,755 tokens per second, by its log.

**With four slots** sharing one KV cache (attempt 2), the title and the first agent request ran side
by side:

- The agent request's headers came after 0.64 seconds, when llama-server had finished processing the
  title's prompt, as its log shows.
- Its first byte came after 7.1 seconds, once its 9,878 prompt tokens had been processed.
- Meanwhile the title produced 10 tokens in 6.5 seconds, 1.5 tokens per second by AgentSplice's
  derivation. llama-server's log shows it generating while the other slot processed the agent's
  prompt, at 1.39 tokens per second by its own count.

Nothing waited for the title in this attempt; the title was slow instead.

What AgentSplice cannot tell is the same as in the first report. Its timeline shows a wait before the
headers and a wait before the first byte, not what the runtime did during either. Here the two waits
line up with the queue and the prompt because that is how llama-server answers, and the runtime's log
is what shows it.

### 4. Usage and tool calls agree with the client

**Tokens.** For each of the nine agent steps, the prompt and completion tokens AgentSplice recorded as
`upstream_reported` are what OpenCode recorded for the same step. For the second step of attempt 1,
they were 10,506 and 730.

OpenCode also split each later step's prompt into tokens it read as cached and new ones: 10,264 and
242 for that step. That matches the reuse llama-server's log shows. AgentSplice records the prompt
tokens as one number, as reported, and does not split them.

**Tool calls.** The native tool calls AgentSplice counted are the tools OpenCode then ran:

| Attempt | Calls per step | Tools OpenCode ran |
| --- | --- | --- |
| 1 | 1, 2, 0 | a `read`; then an `edit` and a `write` |
| 2 | 1, 1, 1, 1, 1, 0 | a `glob`, a `read`, a `glob`, an `edit`, and a `write` |

The finish reasons agree as well.

**The title.** The first model's title took 612 completion tokens, which include whatever it reasoned
before it answered. AgentSplice records completion tokens as reported, and does not split reasoning
from the answer.

### 5. The timeline holds every boundary, and the gateway's share is small

Each exchange records, in order:

1. `request_accepted`
2. `request_body_read`
3. `validation_completed`
4. `structural_summary_created`
5. `model_resolved`
6. `upstream_request_opened`
7. `upstream_headers_received`
8. `first_upstream_byte`
9. `client_response_started`
10. `first_client_event_flushed`
11. `first_decoded_event`
12. `first_semantic_event`
13. a `native_tool_call_observed` for each tool call
14. `upstream_completed`
15. `client_completed`, whose detail says the response was `relayed` and its terminator `observed`
16. `metadata_queued`
17. `persistence_completed`

A new connection to llama-server adds `upstream_connection_started` and
`upstream_connection_established`. Nine of the eleven exchanges have them, each connection made in
under a millisecond. Most of the first report's exchanges reused a pooled connection to LM Studio
instead.

In every stream, the first semantic event came within a quarter of a millisecond of the first byte.
The one exception was the first stream after the gateway started, at 9.7 milliseconds. llama-server
sent nothing before its first output.

The gateway's own time:

- **First events.** AgentSplice flushed each stream's first event to OpenCode within 0.25 milliseconds
  of receiving it from llama-server for ten of the eleven streams, and within 5.6 for the first after
  it started.
- **Opening the request.** Between accepting a request and opening it to llama-server, it spent under
  3.5 milliseconds, except on the first request after it started, which took 31.
- **Writing metadata.** After the response, writing the metadata took 3 to 14 milliseconds, except for
  the first exchange after startup, which took 264.

### 6. Content stayed out

Each attempt's prompt carried its own random marker. Neither marker, and none of the words `calc.py`,
`unittest`, or `mean()`, appear in:

- the gateway's log;
- its SQLite store: the database, its write-ahead log, and its shared-memory file;
- `exchanges.json`;
- llama-server's own log.

The same search, as a control, finds:

- each marker once in every prompt llama-server's prompt log wrote: four for the first attempt and
  seven for the second, one per exchange;
- each marker in OpenCode's session storage;
- the words in OpenCode's output.

Content is the runtime's and the client's to keep, and AgentSplice kept none of it. Every exchange
records `contentRetentionState: metadata_only`.

## What this report does not show

- **General compatibility.** Whether either model or llama-server works with OpenCode in general: this
  was one machine, one task, one attempt per model, and no conformance suite.
- **The task's result.** OpenCode changed `calc.py` and wrote `test_calc.py` both times; nothing here
  ran or judged them.
- **A comparison with LM Studio.** The server, the GPU backend, and each server's defaults all differ
  between the two reports, so their timings are not a comparison.
- **An error from llama-server.** None occurred, so neither its error responses nor the rule of ADR
  0026 met one here.
- **llama-server's SSE ping.** It sends one every 30 seconds by default, and no wait here reached 30
  seconds, so this report does not show what AgentSplice records for one. ADR 0028 names the case as
  a known limitation: a runtime that sends anything before it processes the prompt ends the
  first-byte wait early, and the processing then runs against the idle budget.
- **Other hardware and prompts.** Throughput or latency for anyone else's hardware, or for another
  account's prompt. The numbers are this machine's, with the configurations above.

## Reproducing

1. **Start the gateway.**
   - Download `agentsplice-0.2.1-alpha-win-x64.zip` and `SHA256SUMS` from the `v0.2.1-alpha` release.
   - Check them with `sha256sum --check --ignore-missing` and `gh release verify-asset`.
   - Extract the package into a new folder.
   - Start `AgentSplice.Api.exe` with a token in `AGENTSPLICE_ADMIN_API_KEY` and these arguments:
     - `--agentsplice:administration:apiKeyEnvironmentVariable=AGENTSPLICE_ADMIN_API_KEY`
     - `--agentsplice:runtimes:0:baseUrl=http://127.0.0.1:8080/v1`
     - `--agentsplice:runtimes:0:id=llama-server-local`
2. **Start llama-server.**
   - Download `llama-b10662-bin-win-vulkan-x64.zip` from llama.cpp's b10662 release, check it against
     the digest in `report.json`, and extract it.
   - Start `llama-server` with a model's command line from `report.json`. On a machine whose implicit
     Vulkan layers crash it, set `VK_LOADER_LAYERS_DISABLE=~implicit~` in its environment.
   - Wait for `GET /health` to answer `200` and for `GET /v1/models` to name the alias.
3. **Run OpenCode.**
   - Use a new directory holding `calc.py` and `opencode.json` as above, with `OPENCODE_CONFIG_DIR`
     unset and nothing on standard input.
   - Run `opencode run --format json --model agentsplice/<model id> "<prompt>"`. In Git Bash, which
     these attempts used, end the line with `< /dev/null`; in PowerShell, start it with `$null |`.
   - Before the second model, give the gateway's model list time to catch up: it keeps a runtime's list
     for 30 seconds.
4. **Export the evidence**, with `Authorization: Bearer <token>`:
   - `GET /api/v1/system`;
   - every page of `GET /api/v1/exchanges`;
   - for each exchange, `GET /api/v1/exchanges/{id}` and `GET /api/v1/exchanges/{id}/timeline`.

   `exchanges.json` holds them as `system`, `list`, and `exchanges[].detail` and
   `exchanges[].timeline`, beside `exportedAt`, the one value the export adds.

## Files

- `report.json` — the environment and the scenario, each value with where it came from, llama-server's
  command line for each model, and the attempts.
- `exchanges.json` — what the administrative API answered: its system information, every page of its
  exchange list, and for every exchange listed, its detail and its timeline, with the time of the
  export.
