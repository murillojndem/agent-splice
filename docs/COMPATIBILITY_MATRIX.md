# Compatibility matrix

This file is a template. Every status must be supported by a dated conformance report and environment snapshot.

Evidence so far: two trace reports record OpenCode sessions through AgentSplice with two models each, one [to LM Studio](reports/2026-10-17-opencode-lm-studio/README.md) and one [to llama.cpp's `llama-server`](reports/2026-10-19-opencode-llama-server/README.md), the second through the same `lmstudio` provider. They change no status below: a trace report is evidence, not a conformance run (ADR 0023).

| Client | Ingress protocol | Runtime | Model/profile | Basic chat | Streaming | Native tools | Adapter tools | Replay | Cache evidence | Status |
|---|---|---|---|---|---|---|---|---|---|---|
| OpenCode | OpenAI Chat Completions | LM Studio | Generic transparent | Unknown | Unknown | Unknown | N/A | Planned | Planned | Not tested |
| Cline | OpenAI Chat Completions | LM Studio | Generic transparent | Unknown | Unknown | Unknown | N/A | Planned | Planned | Not tested |
| Aider | OpenAI Chat Completions | LM Studio | Generic transparent | Unknown | Unknown | Unknown | N/A | Planned | Planned | Not tested |
| Custom harness | OpenAI Chat Completions | Fake upstream | Deterministic fixture | Planned | Planned | Planned | N/A | Planned | N/A | Not implemented |

## Allowed statuses

- `Verified` — all required cases for the declared scope pass;
- `Partially verified` — listed limitations or failures remain;
- `Experimental` — fixtures exist but evidence is incomplete;
- `Broken` — a required case fails reproducibly;
- `Unknown` — not tested;
- `Unsupported` — intentionally not supported.

## Evidence rule

Never mark a row supported because an endpoint returned HTTP 200 or a direct chat generated text. Verification requires the relevant protocol, streaming, cancellation, usage, and tool-call suites, with exact versions and configuration.

Basic chat, streaming, native structured tools, adapter-recovered tools, replay, and cache evidence are independent dimensions.
