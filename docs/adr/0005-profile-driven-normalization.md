# ADR 0005: Tool normalization is profile-driven

Status: Accepted

## Context

Tool-call syntax varies by model, template, runtime, and version. A global parser risks false positives.

## Decision

Enable text-to-tool normalizers only through versioned model profiles. Native structured calls always use protocol handling rather than profile recovery.

## Consequences

Profiles require fixtures and maintenance but compatibility behavior remains explicit and testable.
