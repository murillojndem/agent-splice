# Pointing a client at AgentSplice

AgentSplice is an experimental alpha. A client talks to it as it would to an OpenAI-compatible
server's Chat Completions endpoint; AgentSplice forwards each request to LM Studio unchanged and
records where the time went. This guide shows where the address goes in OpenCode, in Cline, and in any
other client that speaks Chat Completions, and how to find the exchange a request produced.

These are examples of how to point a client at the gateway, not evidence that the client works through
it. No compatibility claim is made for any client, model, or runtime: an HTTP 200 is not evidence of
compatibility, and support claims require a dated conformance report (`docs/CONFORMANCE.md`).

Installing and starting the gateway is covered by `docs/INSTALLATION.md`. Everything below assumes it
is running with its defaults.

## What every client needs

| Setting | Value |
| --- | --- |
| Base URL | `http://127.0.0.1:5280/v1` |
| API key | Any placeholder, such as `not-a-secret`, for clients that refuse an empty one |
| Model | An `id` exactly as `GET /v1/models` lists it |

- **The address is the IPv4 loopback literal.** The gateway listens on `127.0.0.1` only. A client given
  a host name instead may resolve it to the IPv6 loopback first and be refused.
- **The API key is never used.** `/v1` requires no credential, and the gateway neither reads, stores,
  nor forwards the client's `Authorization` header (`docs/API.md`). The runtime's own key, if it has
  one, is configured on the gateway. Never put the administrative token or LM Studio's key into a
  client: the gateway would discard it, and it would sit in the client's settings, which for a project
  configuration file often means a repository.
- **The model ID is the runtime's, exactly.** With the shipped settings there are no aliases, so the
  IDs are the ones LM Studio reports, compared case-sensitively. Copy one from the gateway's own list:

  ```bash
  curl -s http://127.0.0.1:5280/v1/models
  ```

  ```powershell
  curl.exe -s http://127.0.0.1:5280/v1/models
  ```

  An ID that is not listed is refused with `404` `agentsplice_model_not_found` and is not forwarded
  to the runtime, though the gateway may first refresh its model list from LM Studio. A model just
  loaded in LM Studio can take up to 30 seconds to appear, the lifetime of the gateway's catalogue
  cache.
- **Two OpenAI endpoints exist:** `GET /v1/models` and `POST /v1/chat/completions`. Any other path
  under `/v1`, such as `/v1/completions`, `/v1/embeddings`, or `/v1/responses`, answers `404` with no
  body, so the client must be configured for Chat Completions.
- **The model needs room for an agent's prompt.** A coding agent sends a long system prompt and its
  tool definitions with every request. LM Studio can load a requested model on demand with its default
  context length, which is easily smaller than that, so load the model yourself with enough context
  before pointing an agent at it. In LM Studio's Developer tab, or with its `lms` command:

  ```bash
  lms server start --port 1234
  lms load <model-id> --context-length 32768
  ```

  `--port` matters: without it, `lms server start` reuses whichever port the server last ran on, and
  the gateway looks for LM Studio at `http://127.0.0.1:1234/v1`.

## OpenCode

[`examples/opencode/opencode.json`](../examples/opencode/opencode.json) defines a provider named
`agentsplice` and makes it OpenCode's default:

1. Copy it to the root of a project, where it applies to that project, or to
   `~/.config/opencode/opencode.json`, where it applies to every project. If a configuration already
   exists there, merge the `provider` entry, `model`, and `small_model` into it.
2. Replace `your-model-id` in its three places — the key under `models`, `model`, and `small_model` —
   with an ID from `/v1/models`.
3. Set `limit.context` to the context length the model was loaded with. OpenCode uses it to decide
   when to compact a conversation.
4. Start `opencode` in the project. The model is the default; `/models` lists it under AgentSplice.

Why the file looks the way it does:

- **`@ai-sdk/openai-compatible`** speaks Chat Completions. OpenCode's `@ai-sdk/openai` package would
  call `/v1/responses`, which the gateway does not serve.
- **No `apiKey`.** Without one, the package sends no `Authorization` header, and the gateway needs
  none. Keeping it out also keeps the file safe to commit.
- **`small_model`.** OpenCode sends lightweight requests, such as generating a session title, to a
  "small model" and may otherwise pick one from another configured provider. Naming the gateway's model
  keeps those requests local and in the gateway's evidence.
- **One prompt, several exchanges.** Each time the model calls a tool, OpenCode runs it and sends the
  result back as a new request, so a prompt produces one exchange per model turn, and a session's
  first prompt one more, for the session's title.
- **The title request comes first.** OpenCode sends it beside the session's first request. A model
  LM Studio serves one request at a time — loaded with `--parallel 1` — answers the title first and
  processes the other request's prompt after it, sending nothing on that stream meanwhile. That wait
  counts against `firstByte`, 5 minutes by default; a longer one is answered with a `504` before the
  stream's first byte (see [Troubleshooting](#troubleshooting)). The first trace report shows the wait
  ([finding 2](reports/2026-10-17-opencode-lm-studio/README.md#2-a-request-that-waited-and-processed-for-a-minute-met-idlestream)):
  the build it ran bounded it with the 60-second `idleStream`, which cut it, and reset the connection
  instead of answering; OpenCode retried, which its log records and its output does not. The AI SDK it
  uses treats a `5xx` as retryable too, which was not tried against OpenCode itself. ADR 0028 gave the
  wait its own budget. If a long prompt still outlasts it, load the model with more than one parallel
  slot, or raise `firstByte` as shown below.
- **Other providers stay available.** Any other provider OpenCode is configured with remains in its
  model picker, and requests to it do not pass through AgentSplice.

The file was written against OpenCode 1.18.7, whose provider documentation describes this shape.
OpenCode's published schema now marks `provider` and `small_model` as deprecated in favour of newer
keys; if a later version refuses the file, compare it with OpenCode's current provider documentation.

## Cline

Cline has no configuration file for a project to carry: its provider is set in Cline's settings
panel, and Cline 4.1.22 keeps it in its own state (`~/.cline/data/settings/providers.json`), which is
not meant to be edited by hand. In Cline's settings, set **API Provider** to **OpenAI Compatible** and
fill in:

| Field | Value |
| --- | --- |
| Base URL | `http://127.0.0.1:5280/v1` |
| OpenAI Compatible API Key | `not-a-secret` — Cline refuses an empty key, and the gateway ignores it |
| Model ID | An ID from `/v1/models` |
| Context Window Size (under Model Configuration) | The context length the model was loaded with |
| Max Output Tokens (under Model Configuration) | No more than the context window leaves room for |

With **Use different models for Plan and Act modes** switched on, Cline keeps a separate provider for
each mode. Set both, or requests in the other mode go wherever that mode points.

Use **OpenAI Compatible** rather than Cline's **LM Studio** provider: that one lists models from LM
Studio's own `/api/v0/models`, which the gateway does not serve.

The field names are those of Cline 4.1.22.

## Any OpenAI-compatible client

Any client that lets you set a base URL and sends Chat Completions requests is pointed at the gateway
the same way. With `curl`, a request and a streamed request:

```bash
curl -s http://127.0.0.1:5280/v1/chat/completions \
  -H "Content-Type: application/json" \
  --data-binary '{"model":"your-model-id","messages":[{"role":"user","content":"Say hello."}]}'

curl -sN http://127.0.0.1:5280/v1/chat/completions \
  -H "Content-Type: application/json" \
  --data-binary '{"model":"your-model-id","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"Say hello."}]}'
```

Add `-i` to see the response headers, among them `x-agentsplice-exchange-id`. Keep the `Content-Type`
header: without it `curl` sends a form, which the gateway refuses with `415` (ADR 0027).

A request that is not streamed has 5 minutes for the runtime's response headers, and LM Studio 0.4.20
sends them only with the finished answer, so a generation longer than that is a `504`. Stream it, or
raise `timeouts:responseHeaders` as [Troubleshooting](#troubleshooting) shows.

In PowerShell, call `curl.exe` by that name — in Windows PowerShell 5.1, `curl` is an alias for
`Invoke-WebRequest` — and send the body from a file, which avoids quoting JSON for a native command:

```powershell
Set-Content -Path request.json -Encoding ascii -NoNewline -Value '{"model":"your-model-id","messages":[{"role":"user","content":"Say hello."}]}'
curl.exe -s http://127.0.0.1:5280/v1/chat/completions -H "Content-Type: application/json" --data-binary "@request.json"
```

`--data-binary` sends the file byte for byte, where `-d` would strip its line breaks, and the quotes
around `"@request.json"` stop PowerShell from reading `@` as splatting. To read the exchange
identifier as well, in Windows PowerShell 5.1 or PowerShell 7:

```powershell
$response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri http://127.0.0.1:5280/v1/chat/completions -ContentType 'application/json' -InFile request.json
$response.Headers['x-agentsplice-exchange-id']
```

With the OpenAI Python SDK:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5280/v1", api_key="not-a-secret")

raw = client.chat.completions.with_raw_response.create(
    model="your-model-id",
    messages=[{"role": "user", "content": "Say hello."}],
)
print(raw.headers["x-agentsplice-exchange-id"])
print(raw.parse().choices[0].message.content)
```

The official OpenAI SDKs also read `OPENAI_BASE_URL` and `OPENAI_API_KEY`. Set them for one command or
one terminal session, not permanently for your account: every program on the machine that uses an
OpenAI SDK would then send its requests here, along with your real key, which the gateway discards.

```bash
OPENAI_BASE_URL=http://127.0.0.1:5280/v1 OPENAI_API_KEY=not-a-secret python app.py
```

```powershell
$env:OPENAI_BASE_URL = 'http://127.0.0.1:5280/v1'; $env:OPENAI_API_KEY = 'not-a-secret'
python app.py
```

## Finding an exchange

A completion response carries `x-agentsplice-exchange-id` whether it succeeded, failed, or was
streamed. The exceptions are a request refused for its size (`413`), for its content type (`415`), or
for load (`429`), which is turned away before an exchange begins. Open the exchange in the dashboard at
`http://127.0.0.1:5280/dashboard/exchanges/<exchange-id>`, or find it in the Exchanges list at
`http://127.0.0.1:5280/dashboard/exchanges`. Run from source, the gateway serves the dashboard only
once it is built and `--agentsplice:dashboard:rootPath` points at the build (`docs/DEVELOPMENT.md`,
"The dashboard"). The same evidence is served by the administrative API. When a token is configured,
as it always is under Docker, every call needs it in the shell. The shell that started the gateway as
the installation guide does already holds it. Under Compose, a later shell reads it from `.env`, taking
the last assignment as Compose does; after a `docker run` there is no `.env`, and the token is the value
you exported:

```bash
export AGENTSPLICE_ADMIN_API_KEY="$(sed -n 's/^AGENTSPLICE_ADMIN_API_KEY=//p' .env | tr -d '\r' | tail -n 1)"
curl -s -H "Authorization: Bearer $AGENTSPLICE_ADMIN_API_KEY" http://127.0.0.1:5280/api/v1/exchanges/<exchange-id>
curl -s -H "Authorization: Bearer $AGENTSPLICE_ADMIN_API_KEY" http://127.0.0.1:5280/api/v1/exchanges/<exchange-id>/timeline
```

```powershell
$env:AGENTSPLICE_ADMIN_API_KEY = (Select-String -Path .env -Pattern '^AGENTSPLICE_ADMIN_API_KEY=(.*)$').Matches[-1].Groups[1].Value
curl.exe -s -H "Authorization: Bearer $env:AGENTSPLICE_ADMIN_API_KEY" http://127.0.0.1:5280/api/v1/exchanges/<exchange-id>
```

With no token configured, as from source by default, the header is ignored.

`x-agentsplice-request-id` is a different value: the correlation token the gateway also forwards to the
runtime as `x-request-id`. The exchange endpoints and the dashboard take the exchange identifier. What
is recorded is structure and timing — counts, shapes, field names, boundaries — and never the prompt,
the output, or tool arguments.

## When a request is cancelled

When a client closes the connection, as `curl` does on Ctrl+C or when its `--max-time` runs out — this
was checked with `curl --max-time 3` — the gateway cancels its request to the runtime. The exchange is recorded with status `cancelled` and failure class
`request_cancelled`; for a stream, its termination is `client_cancelled`, and its timeline records the
moment as a `client_cancelled` event. Nothing is written back to a client that has gone.

A client's own stop control, such as pressing Esc twice in OpenCode or Cancel in Cline, has that
effect only if it closes the connection, which was not checked for either. The exchange's status shows
whether it did.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| The connection is refused | The gateway is not running, or the client was given a host name that resolved to the IPv6 loopback. Use `127.0.0.1`. |
| `404` `agentsplice_model_not_found` | The model ID is not one `/v1/models` lists. Copy it exactly; a newly loaded model can take 30 seconds to appear. |
| `404` with no body | The client called a path the gateway does not serve, such as `/v1/responses`. Configure it for Chat Completions. |
| `502` `agentsplice_runtime_unavailable` | LM Studio's server is not running, or not where the gateway looks: `http://127.0.0.1:1234/v1` by default. From Docker, see "Reaching LM Studio" in `docs/INSTALLATION.md`. |
| `504` `agentsplice_upstream_timeout` | The runtime did not answer in time while the gateway could still say so: on a request that is not streamed, connecting took over 5 seconds, the response headers over 5 minutes, or the whole request over 10 minutes; on a stream, the same phases until the runtime's first byte, and 5 minutes with nothing after its headers — LM Studio still queueing the request or processing a long prompt. A runtime that sends no headers until it has finished generating makes the 5-minute response-header budget the limit for the whole completion. The `504`'s message is the same for each phase; the exchange's timeline names it, in a `timeout_fired` event whose `timeout.phase` is `Connect`, `ResponseHeaders`, `FirstByte`, or `Total`. A stall after the first byte is never a `504`: see the next row. Raise the phase that fired, as below. |
| `502` `agentsplice_invalid_upstream_response` | The runtime answered and then its answer could not be read: its connection dropped, or its body was cut off or over the gateway's limit — on a stream, before its first byte. The exchange's timeline has an `upstream_failed` event saying which, such as `upstream.stream: connection.lost`. |
| A stream stops without finishing | Once the runtime's first byte has been relayed, the gateway has committed a `200`, so a later timeout or a dropped connection can only close the connection: the client sees a stream that stopped. Find the exchange in the dashboard's Exchanges list, where its status is `failed`, and open it. Its termination is `timeout` and its timeline has a `timeout_fired` event — `IdleStream` when the runtime sent nothing between two reads for 60 seconds, `Total` after 10 minutes — or its termination is `connection_lost` and its timeline has an `upstream_failed` event. Either way its client-side boundary is `client_response_abandoned`, and the exchange records no error code, because none reached the client. |
| The model ignores the conversation, or LM Studio reports a context error | The prompt does not fit the loaded context. For a stream, LM Studio 0.4.20 reports that inside a `200` response, as an event carrying an `error`, and the gateway relays it unchanged. The exchange is `completed`, because the gateway did not fail, and the runtime's failure is recorded beside it: the Exchanges list shows `error reported` under "Runtime said", its termination is `upstream_error`, and its timeline has an `upstream_error_reported` event. Load the model with a larger `--context-length`, and match the client's context setting to it. |
| `413` | The request body is over 4 MiB, the gateway's limit. |
| `415` `agentsplice_invalid_request` | The request was not sent with `Content-Type: application/json`. Every SDK sends it; `curl -d` without `-H "Content-Type: application/json"` does not. |
| `400` with no body, for every request | The client reached the gateway by a name other than `localhost`, `127.0.0.1`, or `[::1]` — a Compose service name, `host.docker.internal`, a LAN address. Name it in `AllowedHosts` (`docs/INSTALLATION.md`, "Host names"); the gateway's log names the refused host in a `NoAllowedHostMatched` line. |
| `429` `agentsplice_gateway_overloaded` | 64 completions are already in flight, the gateway's limit; retry after the `Retry-After` interval. |

Which budget a slow request meets depends on when the runtime sends its headers. LM Studio 0.4.20 sends
them with the finished answer for a non-streaming request, so `responseHeaders` bounds the whole
generation, and at once for a stream, so the time a long prompt waits and is processed counts against
`firstByte` instead. A runtime that sends a stream's headers only with its first byte spends that time
against `responseHeaders`. Both are 5 minutes by default; `idleStream`, 60 seconds, bounds only the
gaps between reads once a stream has started (ADR 0028).

Each timeout phase is a setting of the runtime, and each must stay no longer than `total`, 10 minutes
by default: a value below a default lowers it, and a wait longer than 10 minutes needs `total` raised
too. On the Windows package's command line, for example — the Linux package's takes the same
arguments, and from source they follow `dotnet run --project src/AgentSplice.Api --`:

```powershell
.\AgentSplice.Api.exe --agentsplice:runtimes:0:timeouts:responseHeaders=00:08:00 --agentsplice:runtimes:0:timeouts:firstByte=00:08:00
```

or as the environment variables `AGENTSPLICE__RUNTIMES__0__TIMEOUTS__RESPONSEHEADERS` and
`AGENTSPLICE__RUNTIMES__0__TIMEOUTS__FIRSTBYTE`. Under Docker, a line in `.env` reaches the container
only if `docker-compose.yml` passes that variable through, and it does not pass these. Put them in a
`docker-compose.override.yml` beside it instead, which Compose merges by itself:

```yaml
services:
  agentsplice:
    environment:
      AGENTSPLICE__RUNTIMES__0__TIMEOUTS__RESPONSEHEADERS: "00:08:00"
      AGENTSPLICE__RUNTIMES__0__TIMEOUTS__FIRSTBYTE: "00:08:00"
```

A client has a timeout of its own, and while a runtime processes a long prompt the gateway has not sent
the client even its status, which goes with the first byte (ADR 0025). Raise the client's along with
the gateway's.
