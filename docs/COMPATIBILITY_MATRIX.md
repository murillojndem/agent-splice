# Compatibility matrix

This file is a template. Every status must be supported by a dated conformance report and environment snapshot.

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
