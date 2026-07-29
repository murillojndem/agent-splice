# Open-source contribution plan

## Purpose

AgentSplice should generate upstream-quality evidence, not merely accumulate workarounds. A successful investigation may end with an adapter, an upstream issue, an accepted patch, a documentation correction, or a decision that behavior is unsupported.

## Project-level contribution standards

- Publish reproducible issues rather than anecdotal reports.
- Include exact client, runtime, model, quantization, backend, and software versions.
- Include a minimal fixture or reproduction repository when possible.
- Remove private source code, credentials, personal paths, and employer data.
- Distinguish measured facts from inference.
- Attach conformance case IDs and replay manifests.
- Include raw result artifacts or hashes where safe.

## Upstream targets

### OpenCode/Cline/Aider

Potential contributions:

- compact local-model prompts;
- capability detection;
- separate title model or title disablement;
- diagnostic timing display;
- trace/correlation ID support;
- robust native tool-call handling;
- cache-stable request serialization;
- clearer provider error reporting.

### llama.cpp and runtime stack

Potential contributions:

- regression tests;
- log clarity;
- cache/checkpoint diagnostics;
- MTP correctness/performance reproductions;
- AMD gfx1100 evidence;
- protocol/parser fixes;
- small C++ fixes before kernel work.

### LM Studio/Ollama/provider adapters

Potential contributions:

- reproducible compatibility reports;
- documentation corrections;
- tool-call parser fixtures;
- SSE behavior reports;
- cache behavior evidence;
- model metadata/capability accuracy.

## Adapter lifecycle

Before adding a workaround adapter, record:

- defect description;
- affected versions;
- conformance/replay evidence;
- upstream issue or reason none exists;
- safety and false-positive risks;
- activation constraints;
- review date;
- retirement condition.

When an upstream fix ships, add a conformance case for the fixed behavior, disable the adapter for fixed versions, and eventually deprecate it.

## Contribution evidence index

Create `docs/upstream/` entries for every submitted issue or PR containing:

- link and status;
- reproduction artifact;
- affected AgentSplice requirement;
- result and follow-up;
- adapter impact;
- date last verified.
