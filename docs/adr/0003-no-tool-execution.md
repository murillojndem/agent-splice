# ADR 0003: The core gateway does not execute tools

Status: Accepted

## Context

Tool execution creates major security, sandboxing, identity, approval, and policy responsibilities.

## Decision

AgentSplice normalizes tool calls but returns them to the client. The client remains responsible for approval and execution.

## Consequences

AgentSplice can be deployed with lower privilege and a narrower threat surface. A future tool-policy service must be a separate architecture decision.
