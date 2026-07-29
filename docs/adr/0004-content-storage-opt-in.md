# ADR 0004: Prompt and response storage is opt-in

Status: Accepted

## Context

Agent traffic may contain private repositories, credentials, personal data, or regulated content.

## Decision

Store operational metadata by default but no prompt or response body. Replay requires explicit content retention and sanitization configuration.

## Consequences

Some diagnostics are unavailable retrospectively unless enabled. This is an intentional privacy trade-off.
