# Replay and differential comparison specification

## Purpose

Replay converts an observed interaction into a reproducible diagnostic artifact. It allows developers to isolate whether a behavior depends on the client, request shape, profile, model, runtime, backend, or software version.

Replay is not response caching and does not execute client-side tools.

## Replay modes

### Exact replay

Exact replay preserves the sanitized protocol payload, field ordering where meaningful, target model identifier, runtime selection, profile version, and generation parameters. Credentials, timestamps, ephemeral request IDs, and secrets are replaced by safe runtime configuration.

An exact replay may still differ because model generation can be nondeterministic or a runtime may not guarantee deterministic kernels. The report must state the seed and determinism assumptions.

### Adapted replay

Adapted replay intentionally changes one or more dimensions, such as:

- runtime;
- model or quantization;
- backend;
- profile;
- ingress protocol;
- tool schema representation;
- context configuration;
- cache state;
- speculative-decoding settings.

Every changed field or policy must appear in an adaptation manifest. An adapted replay must never be labeled exact.

### Differential replay

Differential replay executes a common artifact against two or more targets and compares:

- protocol validity;
- response structure;
- tool-call structure and arguments;
- finish reasons;
- stream event sequence;
- latency phases;
- token usage and provenance;
- cache evidence;
- errors and unsupported features;
- task-level assertions when available.

## Artifact contents

A replay artifact may include:

- immutable artifact ID;
- source exchange ID;
- ingress protocol and version;
- sanitized request envelope;
- tool schemas after sanitization;
- selected runtime/model/profile identifiers;
- relevant non-secret configuration snapshot;
- expected assertions;
- source transformation manifest;
- content classification;
- sanitizer version;
- integrity hash;
- creation and expiration times.

Raw credentials, authorization headers, environment secrets, private keys, and unrestricted filesystem paths must not be embedded.

## Sanitization

Sanitization runs before persistence and export. It must support:

- header allowlists;
- configurable JSONPath or structured-field redaction;
- secret-pattern detection;
- path normalization;
- repository-name replacement;
- optional body omission;
- deterministic placeholder values when comparison requires stable structure.

The sanitizer must produce a report describing removed, replaced, retained, and unresolved fields without exposing the original secret value.

## Execution safety

Replay must not execute shell, filesystem, browser, MCP, or other agent tools. Tool calls are captured and evaluated as data unless a separate future sandboxed evaluation worker explicitly executes an approved task scenario.

Replay workers require:

- concurrency limits;
- target allowlists;
- request size limits;
- timeouts;
- cancellation;
- network policy;
- retention controls;
- audit events.

## Comparison model

A comparison must distinguish:

- identical;
- structurally equivalent;
- semantically comparable but structurally different;
- incompatible;
- inconclusive.

Text similarity alone is insufficient for tool and protocol comparisons. Structured fields, tool IDs, argument JSON, event order, usage provenance, and assertions must be evaluated independently.

## User interface

The dashboard should provide:

- source exchange summary;
- artifact sanitization status;
- exact/adapted replay selector;
- target matrix;
- per-target timeline;
- side-by-side structured diff;
- tool-call diff;
- latency waterfall;
- warnings about nondeterminism;
- export of a self-contained issue bundle.

## Retention and privacy

Replay content storage is opt-in. Metadata-only operation must remain possible. Operators may define independent retention periods for metadata, sanitized payloads, results, and exported bundles.
