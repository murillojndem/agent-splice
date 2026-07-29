# Glossary

**Agent client** — software that constructs model conversations, orchestrates turns, and usually executes tools, such as a coding agent.

**Runtime** — inference server hosting or exposing a model.

**Provider adapter** — AgentSplice component that communicates with one runtime API.

**Ingress/egress protocol** — client-facing or upstream request, response, error, and streaming contract.

**Exchange** — one captured client-to-runtime interaction, including metadata, observations, measurements, and termination state.

**Observation** — immutable timeline fact such as upstream headers received, first event decoded, or client cancellation.

**Measurement provenance** — origin of a value: measured, client-reported, upstream-reported, runtime-log-derived, estimated, inferred, or unknown.

**Replay artifact** — sanitized immutable representation of an exchange that can be executed again without original credentials.

**Exact replay** — replay intended to preserve the sanitized original target and request semantics.

**Adapted replay** — replay with an explicit manifest of changed target, fields, profile, or policies.

**Differential comparison** — structured comparison of two or more replay or evaluation results.

**Conformance suite** — versioned collection of cases that verifies a declared protocol or behavior.

**Compatibility declaration** — evidence-backed status for a specific client/protocol/runtime/model/profile/environment combination.

**Evaluation scenario** — versioned, assertion-driven task executed in a controlled environment.

**Regression baseline** — approved result set used to detect later correctness or performance changes.

**Profile** — versioned selection and compatibility metadata for a model/runtime combination.

**Compatibility adapter** — optional, explicit transformation or evidence parser for a specific protocol, client, model, runtime, or version.

**Tool call** — structured model output requesting a named tool with arguments.

**Tool-call recovery** — optional adapter conversion of a known alternative text encoding into a structured representation. It is not Stage 1 core behavior.

**Prefill / prompt processing** — computation over input tokens before output generation.

**Decode / generation** — sequential production of output tokens.

**TTFT** — time to first semantic output event visible to the client, when observable.

**Prefix cache** — reuse of previous computation for a stable prompt prefix.

**Cache evidence classification** — confidence-labeled observation such as probable hit, partial hit, probable miss, cold, or unknown; not direct proof of private runtime state.

**MTP** — multi-token prediction used for speculative acceleration in supported models/runtimes.

**SSE** — Server-Sent Events, a common streaming framing used by OpenAI-compatible APIs.

**Support pack** — profiles, adapters, fixtures, documentation, conformance reports, benchmark evidence, limitations, and upstream status for a model/runtime family.
