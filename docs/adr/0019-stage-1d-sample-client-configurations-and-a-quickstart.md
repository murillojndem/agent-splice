# ADR 0019 — Stage 1D: sample client configurations and a quickstart

- Status: Accepted, its gap found and not fixed closed by [ADR 0025](0025-stage-1d-a-stream-commits-with-its-first-byte.md)
- Date: 2026-10-15
- Related: [ADR 0008](0008-stage-1a-transparent-request-path.md) (the client's credential is never
  forwarded; pass-through routing is opt-in),
  [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the installation guide this
  one follows)

## Context

The roadmap lists sample OpenCode, Cline, and generic client configurations for Stage 1D, and the
README said they were still to come. Nothing in the repository showed how to point a client at the
gateway beyond a sentence of the installation guide, and the facts a client's configuration depends on
were spread through the code: the gateway binds the IPv4 loopback only, ignores the client's
credential, serves two OpenAI endpoints, resolves a model by its exact ID, and can turn a timeout into
an error status only until a stream's headers have arrived.

A sample configuration is copied far more often than it is read, and whatever is wrong in it is copied
with it.

## Decisions

### 1. Static samples and a guide, not a setup helper

`examples/opencode/opencode.json` and `docs/CLIENTS.md`, with a quickstart in the README. Nothing writes
to a client's configuration: configuration helpers are Stage 5A (FR-INT-001), and CLAUDE.md keeps
OpenCode and Cline plugins out of Stage 1. Cline sets its provider in its settings panel and keeps it
in its own state rather than in a file a project carries, so its sample is a table of the fields to
set.

### 2. The IPv4 loopback literal, never a host name

Every sample and guide uses `http://127.0.0.1:5280/v1`, the address `LoopbackBindingDefault` binds. A
client given a host name can resolve it to the IPv6 loopback first and be refused. The specification's
configuration example used `localhost:5280` and now uses the literal too.

### 3. No credential in any sample

The gateway never reads, stores, or forwards a client's `Authorization` header, so a key in a client
buys nothing. The OpenCode sample has no `apiKey`, and without one its package sends no header. The
guide gives Cline, which refuses an empty key, the placeholder `not-a-secret`. A project's
`opencode.json` is a file people commit; a sample carrying the administrative token or a runtime key
would teach them to publish it.

### 4. The OpenCode sample keeps OpenCode's auxiliary requests on the gateway

OpenCode sends lightweight requests, such as a session's title, to a small model, and picks one from
another configured provider when none is named. The sample names the gateway's model for both `model`
and `small_model`, so a session's requests stay local and all appear in the evidence. Providers a user
has configured elsewhere remain reachable, and the guide says so (`docs/THREAT_MODEL.md`, "Traffic that
bypasses the gateway").

### 5. Model IDs come from the gateway, and the placeholder fails closed

The sample's model is `your-model-id`, and the guide has the reader copy an ID from the gateway's own
`GET /v1/models`: with the shipped settings that list is exactly what resolves. An unreplaced
placeholder is refused with `404` `agentsplice_model_not_found` and never forwarded, rather than routed
somewhere by a default that would hide the mistake (ADR 0008).

### 6. Chat Completions everywhere

The OpenCode sample uses `@ai-sdk/openai-compatible`; OpenCode's `@ai-sdk/openai` package would call
`/v1/responses`. The guide uses Cline's OpenAI Compatible provider; its LM Studio provider lists models
from LM Studio's own `/api/v0/models`. Neither path is served.

### 7. Examples, not claims

The guide says what to configure and makes no compatibility claim, and the compatibility matrix is
unchanged. It names the versions it was written against, OpenCode 1.18.7 and Cline 4.1.22, because
OpenCode's published schema already marks the `provider` and `small_model` keys deprecated, and a reader
of a later version needs to know which shape was current.

### 8. The guides are held to the gateway by tests

The sample and the guides are checked against the gateway's own constants and shipped settings: the
address, the headers, the settings they name, and the error codes they name. Each of those could drift
and still parse, still read correctly, and fail only in a user's client.

## Consequences

- The README opens with a quickstart, and the installation guide links the client guide.
- `docs/API.md`'s error table mapped every elapsed timeout to `504`. That holds for a request that is not
  streamed and for a stream until the runtime's headers arrive; after them the gateway has committed its
  status and abandons the connection instead, as the relay always did. The table now says both.
- **Found, not fixed here.** A stream's status is committed when the runtime's headers arrive, but
  Kestrel sends it only with the first relayed byte. LM Studio sends a stream's headers at once and its
  first byte only after processing the prompt, so a timeout during a long prompt — or a runtime that
  drops the connection then — resets the connection with no response at all, no status and no
  `x-agentsplice-exchange-id`, while the exchange records the failure correctly. The timeout case was
  reproduced by hand with a one-second `idleStream`; the in-process test host sends headers as soon as
  they are committed and cannot show it. Closing it changes the request path, so it is left to its own
  slice, which the roadmap now lists, and the guide and `docs/API.md` describe it as a known gap.
- No runtime behaviour changes. No request-path code, log, metric, or configuration validation is
  touched, so those parts of the definition of done do not apply; the timeout and cancellation
  behaviour a client sees is documented in the guide.
- The guide was exercised by hand on one Windows workstation, against LM Studio 0.4.20 with one small
  model: every `curl`, PowerShell, and Python block, and OpenCode 1.18.7 with the sample. The Compose
  override was only rendered with `docker compose config`, which places both variables in the
  `agentsplice` service where a line in `.env` does not. Cline was not run: its table was checked
  against the Cline 4.1.22 extension itself. None of that is a compatibility result, and Linux and
  macOS shells, Docker reaching LM Studio, and other client versions were not exercised.

> **Closed by ADR 0025.** The gap found above is closed: a stream's status is committed with its first
> byte, so a runtime that fails before it is answered with a `504` or `502` and every correlation
> header, and a test on a real Kestrel listener shows it.

## Tests proving these decisions

- `ClientSampleContractTests`, in the integration project because the address is
  `LoopbackBindingDefault.Urls` and the contract project does not reference `AgentSplice.Api`:
  - the OpenCode sample targets the loopback default plus `/v1` with `@ai-sdk/openai-compatible`. Fails
    against `localhost`, a wrong port, and `@ai-sdk/openai`;
  - it defines only the gateway's provider, and every `model` and `small_model` in it, at any depth,
    names a model under that provider. Fails against `small_model` removed and an agent's model pointed
    elsewhere;
  - it carries no `apiKey`, no `headers`, no provider `env` list, and no `{env:}` or `{file:}`
    substitution. Fails against an `apiKey` added and an `env` list added;
  - no guide or sample names the gateway's port on `localhost` or the IPv6 loopback, and every loopback
    address the README and the client guide name is the gateway's, or the shipped runtime's on a line
    about LM Studio. Fails against `localhost` in the guide, a wrong port in its base URL, and the
    runtime's port where a client would read it;
  - the client guide names the exchange header, and every `x-agentsplice-*` header it or the README
    names, in any case, is one the gateway declares. Fails against a misspelled header.
- `ConfigurationKeyContractTests` — every setting the user guides name, as an environment variable or a
  `--agentsplice:` argument, read whole, binds to a value rather than to nothing or to a section, and a
  guard proves the extraction. Fails against `TIMEOUT` for `TIMEOUTS`, a single underscore between
  segments, and a capitalised argument with a misspelled segment.
- `ErrorCodeContractTests` — every error code the user guides name is one the gateway can emit. Fails
  against a misspelled code in the client guide and in the installation guide.

## Alternatives considered

- **A command that writes the client's configuration.** Stage 5A, and a tool that edits another
  product's settings is a commitment to every version of that product.
- **`localhost` in the samples, as most client documentation has it.** Rejected in decision 2.
- **A realistic-looking key, such as `sk-…`, as the placeholder.** It reads as a secret, teaches people
  to paste one there, and secret scanners flag it.
- **`enabled_providers` in the OpenCode sample, so the gateway is the only provider.** Copied into a
  global configuration, it would switch off every other provider the user has, and OpenCode's schema
  already marks the key deprecated.
- **A runnable Python script under `examples/`.** No CI job runs Python, so it would rot unseen; the
  guide's snippet carries the same information.
- **An alias or `defaultRuntimeId` in the shipped settings, so that any model name works.** Rejected in
  decision 5.
- **Closing the gap above in this slice**, by flushing a stream's headers as soon as they are
  committed, or by committing only at the first byte so that an earlier timeout can still be a `504`.
  Either changes the request path and what a client sees for every stream, which is a decision for its
  own slice and not for a documentation change.
