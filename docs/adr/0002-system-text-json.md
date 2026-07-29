# ADR 0002: Use System.Text.Json

Status: Accepted

## Context

The project is .NET 8, streaming-sensitive, and must preserve JSON protocol behavior with minimal dependencies.

## Decision

Use System.Text.Json for protocol serialization and parsing. Use JsonDocument/JsonElement or Utf8JsonReader where preservation or incremental parsing requires them.

## Consequences

A provider requiring behavior unavailable in System.Text.Json needs a new ADR before adding another serializer.
