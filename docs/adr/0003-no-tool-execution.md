# ADR 0003: The core gateway does not execute tools

Status: Accepted, its wording amended by [ADR 0027](0027-stage-1d-what-a-local-gateway-exposes-is-closed-or-said.md)

> **Amended.** The decision below said AgentSplice normalizes tool calls. No stage implemented so far
> normalizes one: Stage 1 forwards a tool call as data, unchanged (CLAUDE.md, ADR 0008), and recovering
> a call a model wrote as text is a later-stage adapter (ADR 0006). What this ADR decides stands: tool
> calls are returned to the client, which approves and executes them. The text is kept as written.

## Context

Tool execution creates major security, sandboxing, identity, approval, and policy responsibilities.

## Decision

AgentSplice normalizes tool calls but returns them to the client. The client remains responsible for approval and execution.

## Consequences

AgentSplice can be deployed with lower privilege and a narrower threat surface. A future tool-policy service must be a separate architecture decision.
