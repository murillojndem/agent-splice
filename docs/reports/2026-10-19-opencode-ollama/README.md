# Trace report: OpenCode through AgentSplice to Ollama, 2026-10-19

The third dated trace report (ADR 0023). OpenCode 1.18.7 ran the first report's coding task through
AgentSplice 0.2.1-alpha to Ollama 0.40.6. It used the same Windows machine and the same two model files
as the first two reports, on 2026-10-19 between 21:38 and 21:46 UTC. The gateway was the released
package, unchanged, with its only runtime provider, `lmstudio`, pointed at Ollama's OpenAI-compatible
endpoint. AgentSplice recorded every exchange, and `exchanges.json` holds what its administrative API
answered for each one, unchanged.

**This report makes no compatibility claim.** It records what one machine did in three attempts. It is
not a conformance run, and it evaluates neither the models, the runtime, nor the task's result. An
exchange recorded as `completed` is not evidence that anything worked.

## Summary

- **The shipped provider served Ollama without a change.** Discovery found each model under the name
  Ollama gave it, `:latest` included. Every stream Ollama answered with `200` completed with `[DONE]`
  and every boundary of its timeline. The usage and tool calls AgentSplice recorded agree with what
  OpenCode counted (findings 1, 7, and 8).
- **Ollama handed each file's own chat template to llama.cpp.** It ran both files in llama.cpp's
  `llama-server`, release b11669, which rendered the coder's template, `tojson | safe` included.
  OpenCode finished the task with both models (finding 2).
- **On Ollama, the whole wait falls before the response headers.**
  - Ollama sent a stream's headers with its first byte, as ADR 0028 had read in its source.
  - So a request's time in the queue and in prompt processing fell on the `responseHeaders` budget,
    and none of it on `firstByte`. For the first agent step of attempt 1, that was 53.8 seconds
    (finding 3).
- **Tool calls arrived whole, at the end of their stream.** Two answers were a tool call and nothing
  else, and their headers came only once the call was generated. AgentSplice's derived generation
  throughput for them, computed over a window of microseconds, reads in the millions of tokens per
  second, and this report does not use it (finding 4).
- **Four slots meant four contexts.** Ollama gave each of the coder's four slots its own 32,768 tokens.
  The cache did not fit beside the model on the GPU, and llama.cpp moved 7.2 GiB of the model to
  system memory (finding 5).
- **With Ollama's default context, every agent step was refused, and OpenCode kept trying.**
  - On this GPU, Ollama defaults to 4,096 tokens per request. The runtime answered the 9,826-token
    agent step with a `400`, which AgentSplice relayed unchanged and recorded as
    `upstreamOutcome: error_status`.
  - OpenCode alternated a compaction request with the same refused step 24 times, until the attempt
    was stopped by hand 51 seconds after it began (finding 6).
- **The gateway's own share stayed small,** except when it renewed its list of models, which Ollama
  took 57 to 81 milliseconds to answer (finding 8).
- **No prompt or output reached AgentSplice's log, store, or evidence** (finding 9).

## What was run

**Gateway.** The Windows package of release `v0.2.1-alpha`, `agentsplice-0.2.1-alpha-win-x64.zip`,
which CI built for commit `39dfc7819d195beb5288621ee84d76cb7e893c9c` in release workflow run
39214008235.

- It was downloaded from the release, checked against the release's `SHA256SUMS` and its attestation,
  and extracted into a new folder, so its store began empty.
- It ran with its shipped settings and three arguments:
  - the administrative token's variable, as in the earlier reports;
  - `--agentsplice:runtimes:0:baseUrl=http://127.0.0.1:11435/v1`, which points the shipped runtime at
    Ollama;
  - `--agentsplice:runtimes:0:id=ollama-local`, so that the records name the server they describe.
- The runtime's provider stayed `lmstudio`, the only one this build has. Each exchange records
  `provider.type: lmstudio` beside `runtime.id: ollama-local`.
- Content capture was off, as it is by default.
- The shipped timeouts (ADR 0028):
  - 5 seconds to connect;
  - 5 minutes for the response headers, and another 5 minutes from them to a stream's first byte;
  - 60 seconds between reads after that;
  - 10 minutes in all.

**Runtime.** Ollama 0.40.6, released on 2026-10-16: commit `2476d5ead197abe04629651028f83d48dc75d2b2`.

- It was the release's Windows archive with its ROCm archive, `ollama-windows-amd64.zip` and
  `ollama-windows-amd64-rocm.zip`. Both were checked against the release's `sha256sum.txt` and the
  digests GitHub publishes, and extracted into one new folder, as Ollama's Windows documentation says
  for an AMD GPU. The Ollama installed on the machine was not used.
- For each model, Ollama started llama.cpp's `llama-server`, release b11669, as its runner. It found
  the GPU through ROCm, as `gfx1100`, with the ROCm libraries its archive carries. Vulkan was enabled,
  Ollama's default, and Ollama chose ROCm.
- It served on `127.0.0.1:11435`, because another Ollama, inside WSL, held port 11434 on this machine.
  It was started for each attempt with that attempt's settings, all of them environment variables of
  `ollama serve`, and stopped after it:

| Attempt | Model | Context per request | Slots | Flash attention | KV cache |
| --- | --- | --- | --- | --- | --- |
| 1 | `qwen3.6-35b-a3b:latest` | 262,144 | 1 | on | `q8_0` |
| 2 | `qwen3-coder-30b-apex:latest` | 32,768 | 4 | on | `f16`, Ollama's default |
| 3 | `qwen3-coder-30b-apex:latest` | 4,096, Ollama's default for this GPU | 4 | on | `f16`, Ollama's default |

- All three attempts also set two variables:
  - `OLLAMA_KEEP_ALIVE=30m`, so that no model unloaded during an attempt;
  - `OLLAMA_DEBUG_LOG_REQUESTS=1`, which writes each request body Ollama receives to a folder, as a
    control for the marker search (finding 9).

  Everything else was at Ollama's defaults. `report.json` holds each attempt's settings as Ollama's
  log printed them, and the command line Ollama gave `llama-server` each time.
- Attempts 1 and 2 used the settings the earlier reports recorded for each model, as far as Ollama
  exposes them. What Ollama chose itself differs in three places:
  - **Slots.** Ollama gives every slot the whole context. For attempt 2 it started `llama-server` with
    `-c 131072 -np 4`: four contexts of 32,768 tokens, where the earlier reports' four slots shared one
    (finding 5).
  - **Batch.** Ollama set the batch to 512 tokens, and to 1,024 in attempt 3.
  - **Context shift.** Ollama turned on `--context-shift`.

  Ollama does not set the prompt cache or the context checkpoints, so `llama-server` kept its
  defaults, as in the second report: up to 8 GiB of host memory, and 32 checkpoints.
- **The models.** The two files of the earlier reports, imported with `ollama create`:
  - Ollama copied each file into its store under its SHA-256, and the digests are the earlier
    reports'.
  - Each Modelfile named the file and set `num_thread` to 6, the earlier reports' thread count. The
    first model's also named its vision projector, which the earlier reports loaded with it.
  - Ollama named the models `qwen3.6-35b-a3b:latest` and `qwen3-coder-30b-apex:latest`, and the client
    asked for those ids.
- **Loaded in advance.** Before each attempt, its model was loaded with a `POST /api/generate` that
  named only the model, as LM Studio and `llama-server` had their models loaded before theirs. Ollama
  took 23.1, 14.6, and 12.8 seconds to start the runner.

**Machine.** The earlier reports': Windows 11 Pro 10.0.26200, AMD Ryzen 7 5800X3D, 64 GiB of memory,
and an AMD Radeon RX 7900 XT with 20 GiB, driver 32.0.32015.2008. Every value, and where it came from,
is in `report.json`.

**Client.** OpenCode 1.18.7, run as in the second report:
`opencode run --format json --model agentsplice/<model id> "<prompt>"`, with nothing on standard input
and a cap of 15 minutes.

- It ran from a new directory holding `calc.py` and an `opencode.json` made from
  `examples/opencode/opencode.json`: the AgentSplice provider, the model as both `model` and
  `small_model`, `bash` and `webfetch` denied, `edit` allowed, and sharing disabled.
- Its context limit was 262,144 for attempt 1 and 32,768 for attempts 2 and 3, so between attempts 2
  and 3 only Ollama changed.
- The terminal integration's variables, `OPENCODE_CONFIG_DIR` among them, were unset.
- OpenCode found the same 18 skills, and every agent request carried their list in its system message:
  between 34% and 40% of each agent request's body. The title and compaction requests carry none.

**Task.** The first report's prompt and `calc.py`, unchanged, with a random marker that was different
in each attempt:

> calc.py's mean() returns the wrong result. Fix the bug, then add test_calc.py with unittest tests for
> mean() and clamp(). Do not run any commands. Session marker: trace-canary-…

**Attempts.**

| Attempt | Model | Slots | Context | Exchanges | OpenCode |
| --- | --- | --- | --- | --- | --- |
| 1 | `qwen3.6-35b-a3b:latest` | 1 | 262,144 | 6 | exited 0 after five agent steps |
| 2 | `qwen3-coder-30b-apex:latest` | 4 | 32,768 each | 8 | exited 0 after seven agent steps |
| 3 | `qwen3-coder-30b-apex:latest` | 4 | 4,096 each | 50 | stopped by hand after 51 seconds (finding 6) |

## Exchanges

Durations are AgentSplice's own (`measured`):

- **headers** and **first byte** are counted from the moment it opened the request to Ollama;
- **first semantic event** and **total** are counted from the moment it accepted the client's.

Tokens are as Ollama reported them (`upstream_reported`). The **answer** is Ollama's status:

- `200` with `text/event-stream`, recorded as `upstreamOutcome: success`;
- `400` with `application/json`, recorded as `upstreamOutcome: error_status`.

The request AgentSplice was still forwarding when attempt 3 was stopped has no answer. A request
without tools is OpenCode's title when it is the first of its attempt, and its compaction otherwise;
OpenCode's log names both.

| Attempt | Exchange | Request | Answer | Status, termination | Headers | First byte | First semantic event | Total | Finish | Tool calls | Prompt / completion tokens |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `89183415` | title, no tools | `200` | `completed`, `protocol_terminator_received` | 2,255 ms | 2,268 ms | 2,440 ms | 32,697 ms | `stop` | 0 | 607 / 2,266 |
| 1 | `47e6b923` | agent, 8 tools, 2 messages | `200` | `completed`, `protocol_terminator_received` | 53,784 ms | 53,785 ms | 53,787 ms | 54,549 ms | `tool_calls` | 1 | 9,770 / 56 |
| 1 | `e1680459` | agent, 8 tools, 4 messages | `200` | `completed`, `protocol_terminator_received` | 526 ms | 526 ms | 585 ms | 2,066 ms | `tool_calls` | 1 | 9,885 / 108 |
| 1 | `63961763` | agent, 8 tools, 6 messages | `200` | `completed`, `protocol_terminator_received` | 1,464 ms | 1,464 ms | 1,465 ms | 4,290 ms | `tool_calls` | 1 | 10,226 / 207 |
| 1 | `96a09e50` | agent, 8 tools, 8 messages | `200` | `completed`, `protocol_terminator_received` | 689 ms | 689 ms | 690 ms | 8,199 ms | `tool_calls` | 1 | 10,411 / 549 |
| 1 | `0f303f77` | agent, 8 tools, 10 messages | `200` | `completed`, `protocol_terminator_received` | 1,355 ms | 1,356 ms | 1,356 ms | 2,421 ms | `stop` | 0 | 10,939 / 77 |
| 2 | `bb8c6565` | title, no tools | `200` | `completed`, `protocol_terminator_received` | 8,338 ms | 8,338 ms | 8,407 ms | 29,088 ms | `stop` | 0 | 590 / 10 |
| 2 | `2c1c846d` | agent, 8 tools, 2 messages | `200` | `completed`, `protocol_terminator_received` | 53,566 ms | 53,566 ms | 53,566 ms | 56,549 ms | `tool_calls` | 1 | 9,828 / 55 |
| 2 | `ebcfd2ee` | agent, 8 tools, 4 messages | `200` | `completed`, `protocol_terminator_received` | 2,341 ms | 2,341 ms | 2,414 ms | 5,876 ms | `tool_calls` | 1 | 10,115 / 64 |
| 2 | `edfbcb88` | agent, 8 tools, 6 messages | `200` | `completed`, `protocol_terminator_received` | 345 ms | 345 ms | 345 ms | 8,274 ms | `tool_calls` | 1 | 10,196 / 150 |
| 2 | `2994b850` | agent, 8 tools, 8 messages | `200` | `completed`, `protocol_terminator_received` | 18,396 ms | 18,397 ms | 18,397 ms | 18,397 ms | `tool_calls` | 1 | 10,363 / 339 |
| 2 | `d87a676f` | agent, 8 tools, 10 messages | `200` | `completed`, `protocol_terminator_received` | 4,549 ms | 4,549 ms | 4,611 ms | 4,612 ms | `tool_calls` | 1 | 10,720 / 78 |
| 2 | `99a482b0` | agent, 8 tools, 12 messages | `200` | `completed`, `protocol_terminator_received` | 1,897 ms | 1,897 ms | 1,897 ms | 9,780 ms | `tool_calls` | 1 | 10,882 / 144 |
| 2 | `fb527dd0` | agent, 8 tools, 14 messages | `200` | `completed`, `protocol_terminator_received` | 2,397 ms | 2,397 ms | 2,397 ms | 8,877 ms | `stop` | 0 | 11,253 / 123 |
| 3 | `5c436f7b` | title, no tools | `200` | `completed`, `protocol_terminator_received` | 595 ms | 595 ms | 662 ms | 772 ms | `stop` | 0 | 588 / 12 |
| 3 | `c2033b8b` | agent, 8 tools, 2 messages | `400` | `completed`, `not_applicable` | 187 ms | 187 ms | — | 190 ms | — | 0 | — / — |
| 3 | `179e0927` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 198 ms | 198 ms | 198 ms | 1,750 ms | `stop` | 0 | 492 / 211 |
| 3 | `1dac7d23` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 239 ms | 239 ms | — | 240 ms | — | 0 | — / — |
| 3 | `a8d1a4db` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 222 ms | 223 ms | 223 ms | 1,783 ms | `stop` | 0 | 753 / 211 |
| 3 | `e6c1ff32` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 241 ms | 241 ms | — | 243 ms | — | 0 | — / — |
| 3 | `1acfa58f` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 58 ms | 58 ms | 58 ms | 1,754 ms | `stop` | 0 | 753 / 231 |
| 3 | `83e62f79` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 239 ms | 239 ms | — | 240 ms | — | 0 | — / — |
| 3 | `a5e5fea0` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 180 ms | 180 ms | 181 ms | 1,870 ms | `stop` | 0 | 773 / 231 |
| 3 | `e7c22922` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 237 ms | 237 ms | — | 239 ms | — | 0 | — / — |
| 3 | `3f2f2648` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 55 ms | 55 ms | 55 ms | 1,734 ms | `stop` | 0 | 773 / 231 |
| 3 | `e767829f` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 212 ms | 212 ms | — | 213 ms | — | 0 | — / — |
| 3 | `02c7cdbd` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 57 ms | 58 ms | 58 ms | 1,746 ms | `stop` | 0 | 773 / 231 |
| 3 | `14b7c2c1` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 185 ms | 185 ms | — | 187 ms | — | 0 | — / — |
| 3 | `8e61f37f` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 59 ms | 59 ms | 59 ms | 1,752 ms | `stop` | 0 | 773 / 231 |
| 3 | `483c2ad0` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 181 ms | 181 ms | — | 183 ms | — | 0 | — / — |
| 3 | `e14bd498` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 54 ms | 54 ms | 55 ms | 1,734 ms | `stop` | 0 | 773 / 231 |
| 3 | `b27af594` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 233 ms | 233 ms | — | 235 ms | — | 0 | — / — |
| 3 | `abcd450b` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 56 ms | 56 ms | 56 ms | 1,750 ms | `stop` | 0 | 773 / 231 |
| 3 | `4154c8a1` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 190 ms | 190 ms | — | 191 ms | — | 0 | — / — |
| 3 | `14661c7e` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 54 ms | 54 ms | 54 ms | 1,729 ms | `stop` | 0 | 773 / 231 |
| 3 | `e0e21a3b` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 180 ms | 180 ms | — | 182 ms | — | 0 | — / — |
| 3 | `a4e586a0` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 54 ms | 54 ms | 54 ms | 1,723 ms | `stop` | 0 | 773 / 231 |
| 3 | `b3629216` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 188 ms | 188 ms | — | 190 ms | — | 0 | — / — |
| 3 | `73f6fb18` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 63 ms | 63 ms | 63 ms | 1,739 ms | `stop` | 0 | 773 / 231 |
| 3 | `458a41a0` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 182 ms | 182 ms | — | 183 ms | — | 0 | — / — |
| 3 | `0368cb26` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 54 ms | 54 ms | 54 ms | 1,727 ms | `stop` | 0 | 773 / 231 |
| 3 | `7df5d54f` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 185 ms | 185 ms | — | 187 ms | — | 0 | — / — |
| 3 | `0615d9c3` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 52 ms | 52 ms | 53 ms | 1,724 ms | `stop` | 0 | 773 / 231 |
| 3 | `b36bf3f3` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 191 ms | 191 ms | — | 193 ms | — | 0 | — / — |
| 3 | `aa524499` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 56 ms | 56 ms | 56 ms | 1,733 ms | `stop` | 0 | 773 / 231 |
| 3 | `0d4615ca` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 202 ms | 203 ms | — | 285 ms | — | 0 | — / — |
| 3 | `aabfca2e` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 51 ms | 51 ms | 51 ms | 1,726 ms | `stop` | 0 | 773 / 231 |
| 3 | `de748bb3` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 183 ms | 183 ms | — | 185 ms | — | 0 | — / — |
| 3 | `f02010e1` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 55 ms | 55 ms | 55 ms | 1,730 ms | `stop` | 0 | 773 / 231 |
| 3 | `c370751d` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 192 ms | 192 ms | — | 193 ms | — | 0 | — / — |
| 3 | `71ddb29f` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 56 ms | 56 ms | 56 ms | 1,731 ms | `stop` | 0 | 773 / 231 |
| 3 | `7636fce0` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 210 ms | 210 ms | — | 211 ms | — | 0 | — / — |
| 3 | `fb98b2e3` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 56 ms | 56 ms | 56 ms | 1,738 ms | `stop` | 0 | 773 / 231 |
| 3 | `0ed79ac7` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 236 ms | 236 ms | — | 238 ms | — | 0 | — / — |
| 3 | `bef09d3a` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 64 ms | 64 ms | 64 ms | 1,752 ms | `stop` | 0 | 773 / 231 |
| 3 | `39e63594` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 187 ms | 187 ms | — | 188 ms | — | 0 | — / — |
| 3 | `625b1959` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 60 ms | 60 ms | 60 ms | 1,737 ms | `stop` | 0 | 773 / 231 |
| 3 | `407f32a2` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 199 ms | 199 ms | — | 200 ms | — | 0 | — / — |
| 3 | `5e5e3b99` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 54 ms | 54 ms | 54 ms | 1,737 ms | `stop` | 0 | 773 / 231 |
| 3 | `52b7a0f1` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 186 ms | 186 ms | — | 187 ms | — | 0 | — / — |
| 3 | `951be062` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 57 ms | 57 ms | 57 ms | 1,735 ms | `stop` | 0 | 773 / 231 |
| 3 | `cfde7945` | agent, 8 tools, 4 messages | `400` | `completed`, `not_applicable` | 219 ms | 219 ms | — | 220 ms | — | 0 | — / — |
| 3 | `515d9382` | compaction, no tools | `200` | `completed`, `protocol_terminator_received` | 56 ms | 56 ms | 56 ms | 1,739 ms | `stop` | 0 | 773 / 231 |
| 3 | `51260ac1` | agent, 8 tools, 4 messages | — | `cancelled`, `not_applicable` | — | — | — | 98 ms | — | — | — / — |

AgentSplice derived each stream's generation throughput. It divided the completion tokens Ollama
reported by the window it measured itself, from the first semantic event to the end of the stream:

- between 72 and 75 tokens per second with the first model;
- between 18 and 19 for the other agent steps of attempt 2, and 0.5 for its title, which finding 5
  explains;
- 109 for attempt 3's title, and between 135 and 138 for its compaction requests;
- 9,237,057 and 3,529,412 for the two answers of attempt 2 that were a tool call alone, which finding 4
  explains.

It labels them `upstream_reported`, the weaker of their two inputs. It records no prompt-processing
throughput, by design (`docs/OBSERVABILITY.md`).

The runner's log, which Ollama's log carries, records the runtime's side:

- prompt processing:
  - 443 tokens per second for the first model's 9,770-token agent prompt;
  - 198 for the second model's 9,828, with part of the model in system memory;
  - 277 for attempt 1's 607-token title, the first prompt after the model loaded;
- generation:
  - between 71.4 and 74.8 tokens per second with the first model;
  - between 18.1 and 18.8 with the second model in attempt 2;
  - 99.1 for attempt 3's title, and between 134.6 and 137.8 for its compaction requests, with the
    model wholly on the GPU.

The same log shows each later agent step evaluating only the new end of its prompt and taking the rest
from the slot: 119, 345, 189, and 532 tokens with the first model, and 255, 18, 18, 19, 147, and 250
with the second.

Those are the runtime's numbers, not AgentSplice's evidence, and they are ROCm's on this machine. Set
beside the earlier reports', they would compare three servers at once, so this report does not compare
them.

## Findings

### 1. The shipped provider served Ollama without a change

Pointed at Ollama, the `lmstudio` provider read its model list and found each model under the name
Ollama gave it:

- every exchange's `model_resolved` boundary records `resolution.source: discovered`;
- each exchange's `upstreamModelId` is the id the client asked for, `:latest` included.

Ollama's list gives each model a `created` time and an `owned_by` of `library`. None of it reached a
record, and no exchange records a dropped or unknown field.

All 39 exchanges that Ollama answered with `200`:

- carried `text/event-stream`;
- ended with `[DONE]`: termination `protocol_terminator_received` and `stream.terminator: observed`;
- record `upstreamOutcome: success`.

The records name the runtime `ollama-local`, which the configuration chose, and the provider
`lmstudio`, which served it. The other 25 exchanges are attempt 3's, and finding 6 describes them.

### 2. Ollama handed each file's own chat template to llama.cpp

Ollama can render a chat template itself, with a template of its own, or leave it to `llama-server`.
For these two files it did the second:

- `ollama create` named no template it had matched, and the Modelfile Ollama keeps for each model holds
  only its placeholder, `TEMPLATE {{ .Prompt }}`;
- the template Ollama's `/api/show` returns for each model is the one embedded in its file, character
  for character: 8,057 characters for the first model and 6,896 for the second;
- Ollama's log records the choice when it loads each model: `selected=gguf_chat_template`, with no Go
  template, renderer, or parser.

Ollama also read each model's capabilities from that template: `tools`, `thinking`, `completion`, and
`vision` for the first, and `tools` and `completion` for the second.

`llama-server`, release b11669, rendered the templates with llama.cpp's Jinja engine. That is the case
the second report found working on `llama-server` b10662:

- the coder's template applies `tojson | safe` to a tool parameter whose schema holds a nested mapping,
  the case that stopped LM Studio in the first report;
- OpenCode's `todowrite` has such a parameter, and each of attempt 2's agent requests declared it among
  eight tools.

Those requests completed with native tool calls, `todowrite` among the tools OpenCode then ran, and
OpenCode finished the task with both models.

This shows what one Ollama release did with two files imported from GGUF. Models from Ollama's own
library can come with a template, renderer, or parser of Ollama's, and this report ran none.

### 3. On Ollama, the whole wait falls before the response headers

ADR 0028 recorded, from reading Ollama's source, that its OpenAI-compatible server writes a stream's
headers with its first chunk. The timeline shows it. In every stream but the first after the gateway
started, the first byte came within 1.1 milliseconds of the headers; in that one, within 13.

So on this runtime, everything before a stream's first output falls on the `responseHeaders` budget,
and none of it on `firstByte`:

- the time in the queue;
- the prompt processing;
- for an answer that is only a tool call, the generation (finding 4).

**With one slot** (attempt 1):

1. OpenCode sent its title request and its first agent request 1.1 seconds apart, and the title took
   the slot.
2. The title reasoned for 2,266 tokens and took 32.7 seconds.
3. The agent request's headers came 53.8 seconds after AgentSplice opened it:
   - 31.6 seconds while the title held the slot;
   - 22.0 seconds of processing its 9,770 prompt tokens, by the runner's log;
   - then its first token.
4. Its first byte came 1 millisecond after the headers.

On `llama-server`, in the second report, the queue fell on `responseHeaders` and the prompt on
`firstByte`. On LM Studio, in the first, both fell on the wait for the first byte. Here both fell on
the response headers. That budget is 5 minutes in this build (ADR 0028), and nothing came near it: the
longest wait was 53.8 seconds.

What AgentSplice cannot tell is the same as in the earlier reports. Its timeline shows the wait, not
what the runtime did during it. The runner's log is what splits the wait into the queue and the
prompt.

### 4. Tool calls arrive whole, at the end of their stream

Ten streams carried a tool call. In each, the `native_tool_call_observed` boundary came within 1.1
milliseconds of the end of the stream: Ollama sent each call once the model had finished generating
it, not while it was generated. That is what Ollama 0.40.6's code for this runner does: it gathers the
pieces of a call from `llama-server` and passes the call on only when the model's answer finishes.

Two of attempt 2's answers were a tool call and nothing else: `2994b850`, the `write` that created
`test_calc.py`, 339 tokens, and `d87a676f`, 78 tokens. For each:

- the headers, the first byte, the first semantic event, and the end of the stream came within 0.4
  milliseconds of one another;
- the 18.0 and 4.2 seconds the runner's log gives for generating them fell before the headers.

AgentSplice's derived generation throughput divides the completion tokens by the window from the first
semantic event to the end of the stream:

- for these two, the window was 36 and 22 microseconds, and the result 9,237,057 and 3,529,412 tokens
  per second;
- the runner's log gives 18.7 and 18.5.

The arithmetic is right, and the number says nothing about generation, so this report does not use
it. Its provenance, `upstream_reported`, does not tell a reader that.

It also moves where a long tool call's generation falls:

- with nothing before it, on the `responseHeaders` budget, as above;
- after the model's other output, on the 60-second idle budget, because nothing flows while the call
  is generated.

The longest stream here that carried other output before a tool call lasted 7.9 seconds from its first
event to its end, so neither came near its limit.

### 5. Four slots, four contexts

Ollama gives each slot a context of the size it was asked for. For attempt 2's four slots of 32,768
tokens, it started `llama-server` with 131,072 tokens of cache, 12,288 MiB in `f16`. The earlier
reports' four slots shared 32,768.

That did not fit beside the model:

- llama.cpp projected 26,328 MiB for a GPU with 20,301 MiB free;
- it kept the cache on the GPU and moved part of the expert weights of 28 layers to system memory;
- 6,693 MiB of the model stayed on the GPU, and 7,398 MiB went to host memory.

Ollama's `/api/ps` reported 20,165,091,654 of the model's 20,331,123,177 bytes as in VRAM. Its figure
does not show that split; the runner's log does.

The evidence shows the effect:

- **The first agent step.** Its headers came 53.6 seconds after AgentSplice opened it: 49.5 seconds of
  processing its 9,828 prompt tokens, at 198 tokens per second by the runner's log, while the title
  ran beside it.
- **The title.** Sharing the GPU with that prompt, it took 8.3 seconds to its headers and 29.1 seconds
  in all for 10 tokens: 0.44 tokens per second by the runner's log, and 0.5 by AgentSplice's
  derivation.
- **The other agent steps** generated at 18.1 to 18.8 tokens per second, by the runner's log.

Nothing waited in a queue; the title was slow instead, as with four slots in the second report.

### 6. With Ollama's default context, every agent step was refused

For attempt 3, `OLLAMA_CONTEXT_LENGTH` was unset, and nothing else changed from attempt 2, OpenCode's
configuration included.

- **The context.** Ollama chose its default for this GPU; its log reads
  `vram-based default context total_vram="20.0 GiB" default_num_ctx=4096`. With four slots, it started
  `llama-server` with four contexts of 4,096 tokens.
- **The title** completed, with 588 prompt tokens.
- **The first agent step** did not:
  - its request was 40,039 bytes, which `llama-server` counted as 9,826 tokens;
  - 187 milliseconds after AgentSplice opened it, Ollama answered `400` with `application/json`;
  - the body named the reason, `request (9826 tokens) exceeds the available context size (4096 tokens),
    try increasing it`, with the type `invalid_request_error`.

AgentSplice relayed the answer to OpenCode unchanged and recorded:

- status `completed`, `upstreamStatusCode: 400`, and `upstreamOutcome: error_status`;
- an `upstream_error_reported` boundary, and `upstreamErrorReported: true`;
- termination `not_applicable`, since the answer was not a stream;
- no usage, since Ollama reported none.

The error's text went to the client only; no record holds it.

OpenCode reported a `ContextOverflowError` and ran its compaction agent:

1. a request without tools, which completed with a summary of 211 to 231 tokens;
2. then the agent step again, with the summary: 41,135 to 41,250 bytes, 9,850 tokens by
   `llama-server`'s count, refused the same way.

The first agent request held only the system message, the eight tools, and the task, and it was
already 9,826 tokens, so no summary could make the step fit. OpenCode repeated the pair 24 times. The
attempt was stopped by hand at 21:46:01.771 UTC, 51 seconds after it began, and the agent request then
in flight was recorded as `cancelled`, with the failure class `request_cancelled`, 98 milliseconds
after it was accepted.

Here the runtime refused a prompt it could not hold; it did not cut it. No exchange answered `200` for
a prompt cut short: every one reported between 4.0 and 5.0 bytes of request per prompt token.

### 7. Usage and tool calls agree with the client

**Tokens.** For each of the 12 agent steps of attempts 1 and 2, the prompt and completion tokens
AgentSplice recorded as `upstream_reported` are what OpenCode recorded for the same step.

OpenCode also split each later step's prompt into tokens it read as cached and new ones: 9,766 and 119
for the second step of attempt 1, whose prompt AgentSplice records as 9,885. That matches the reuse the
runner's log shows. AgentSplice records the prompt tokens as one number, as reported, and does not
split them.

**Tool calls.** The native tool calls AgentSplice counted are the tools OpenCode then ran:

| Attempt | Calls per step | Tools OpenCode ran |
| --- | --- | --- |
| 1 | 1, 1, 1, 1, 0 | a `glob`, a `read`, an `edit`, and a `write` |
| 2 | 1, 1, 1, 1, 1, 1, 0 | a `read`, a `glob`, an `edit`, a `write`, a `todowrite`, and a `read` |

The finish reasons agree as well.

**Usage.** Ollama sends a stream's usage only when the request asks for it with
`stream_options.include_usage`, and OpenCode asked in every request. Every exchange records
`streamOptionsPresent: true`, and every stream that answered `200` reported its usage.

**The title.** The first model's title took 2,266 completion tokens, which include whatever it
reasoned before it answered. AgentSplice records completion tokens as reported, and does not split
reasoning from the answer.

### 8. The timeline holds every boundary, and the gateway's share is small

Each stream that Ollama answered with `200` records, in order:

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
13. a `native_tool_call_observed` for each event that carried a tool call
14. `upstream_completed`
15. `client_completed`, whose detail says the response was `relayed` and its terminator `observed`
16. `metadata_queued`
17. `persistence_completed`

A new connection to Ollama adds `upstream_connection_started` and `upstream_connection_established`;
32 of the 64 exchanges have them. A refused request records, after its first byte,
`client_response_started`, `upstream_completed`, `upstream_error_reported`, and `client_completed`,
with none of the event boundaries a stream has. The cancelled one records `client_cancelled` right
after `upstream_request_opened`.

In every stream, the first semantic event came within a quarter of a millisecond of the first byte.
The one exception was the first stream after the gateway started, at 10.7 milliseconds.

The gateway's own time:

- **First events.** AgentSplice flushed each stream's first event to OpenCode within 0.25 milliseconds
  of receiving it from Ollama for 38 of the 39 streams, and within 5.3 for the first after it started.
- **Opening the request.** Between accepting a request and opening it to Ollama, it spent under 2
  milliseconds, except when it renewed its list of the runtime's models, which it keeps for 30 seconds.
  It renewed the list seven times:
  - each time, it waited for Ollama's `/v1/models`, which took 57 to 81 milliseconds by Ollama's log;
  - so those openings took 58 to 82 milliseconds, and 161 on the first request after it started.
- **Writing metadata.** After the response, writing the metadata took 2.3 to 21.5 milliseconds, except
  for the first exchange after startup, which took 242.

### 9. Content stayed out

Each attempt's prompt carried its own random marker. None of the three markers, and none of the words
`calc.py`, `unittest`, or `mean()`, appear in:

- the gateway's log;
- its SQLite store: the database, its write-ahead log, and its shared-memory file;
- `exchanges.json`;
- Ollama's own logs, which carry the runner's.

The same search, as a control, finds:

- each marker once in every request body Ollama's request logging wrote: 6 for the first attempt, 8
  for the second, and 50 for the third, one per exchange;
- each marker in OpenCode's session store;
- the words in OpenCode's output.

Content is the runtime's and the client's to keep, and AgentSplice kept none of it. Every exchange
records `contentRetentionState: metadata_only`.

## What this report does not show

- **General compatibility.** Whether either model or Ollama works with OpenCode in general: this was
  one machine, one task, one attempt per configuration, and no conformance suite.
- **The task's result.** OpenCode changed `calc.py` and wrote `test_calc.py` in attempts 1 and 2;
  nothing here ran or judged them.
- **A comparison with LM Studio or `llama-server`.** The server, the GPU backend, each server's
  defaults, and in attempt 2 the model's place in memory all differ, so the timings are not a
  comparison.
- **A cold start.** Each model was loaded before its attempt. Loading took Ollama 12.8 to 23.1 seconds,
  and on a request it would fall on the response-header budget too.
- **Ollama's own engine and library.** Ollama ran these files in `llama-server`. Models from Ollama's
  library can run on Ollama's own engine, or with a template, renderer, or parser of Ollama's; this
  report ran none of those.
- **Vulkan.** Ollama chose ROCm for this GPU, so the runtime's numbers are ROCm's.
- **Whether OpenCode would have stopped.** Attempt 3 was stopped by hand after 51 seconds, so this
  report does not show whether OpenCode would have ended its loop by itself.
- **An error inside a `200`.** Ollama refused before it streamed, so the rule of ADR 0026 did not meet
  one here.
- **Other hardware and prompts.** Throughput or latency for anyone else's hardware, or for another
  account's prompt. The numbers are this machine's, with the configurations above.

## Reproducing

1. **Start the gateway.**
   - Download `agentsplice-0.2.1-alpha-win-x64.zip` and `SHA256SUMS` from the `v0.2.1-alpha` release.
   - Check them with `sha256sum --check --ignore-missing` and `gh release verify-asset`.
   - Extract the package into a new folder.
   - Start `AgentSplice.Api.exe` with a token in `AGENTSPLICE_ADMIN_API_KEY` and these arguments:
     - `--agentsplice:administration:apiKeyEnvironmentVariable=AGENTSPLICE_ADMIN_API_KEY`
     - `--agentsplice:runtimes:0:baseUrl=http://127.0.0.1:11435/v1`
     - `--agentsplice:runtimes:0:id=ollama-local`
2. **Start Ollama.**
   - Download `ollama-windows-amd64.zip`, `ollama-windows-amd64-rocm.zip`, and `sha256sum.txt` from
     Ollama's v0.40.6 release, check the archives against `sha256sum.txt`, and extract both into one
     new folder.
   - For each attempt, start `ollama serve` from that folder with the attempt's variables from
     `report.json`.
   - Import each model with `ollama create <name> -f <Modelfile>`, with the Modelfiles in
     `report.json`.
     - On this machine, LM Studio's model folder is a directory junction to another drive.
     - A `FROM` path through that junction reached Ollama's server as a model name, refused with
       `400 Bad Request: invalid model name`.
     - A path that names the file on its own drive was taken as a file.
   - Load the attempt's model with `POST /api/generate` and a body that names only the model.
3. **Run OpenCode.**
   - Use a new directory holding `calc.py` and `opencode.json` as above, with the model's id,
     `:latest` included, `OPENCODE_CONFIG_DIR` unset, and nothing on standard input.
   - Run `opencode run --format json --model agentsplice/<model id> "<prompt>"`. In Git Bash, which
     these attempts used, end the line with `< /dev/null`; in PowerShell, start it with `$null |`.
4. **Export the evidence**, with `Authorization: Bearer <token>`:
   - `GET /api/v1/system`;
   - every page of `GET /api/v1/exchanges`;
   - for each exchange, `GET /api/v1/exchanges/{id}` and `GET /api/v1/exchanges/{id}/timeline`.

   `exchanges.json` holds them as `system`, `list`, and `exchanges[].detail` and
   `exchanges[].timeline`, beside `exportedAt`, the one value the export adds.

## Files

- `report.json` — the environment and the scenario, each value with where it came from, Ollama's
  settings and `llama-server`'s command line for each attempt, and the attempts.
- `exchanges.json` — what the administrative API answered: its system information, every page of its
  exchange list, and for every exchange listed, its detail and its timeline, with the time of the
  export.
