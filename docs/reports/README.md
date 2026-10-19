# Trace reports

A trace report is a dated record of real sessions through AgentSplice: a real client, a real runtime,
one machine (ADR 0023). It shows what the gateway recorded and what that evidence does and does not
say. It is never a compatibility claim: a row of `docs/COMPATIBILITY_MATRIX.md` changes status only with
a conformance report (`docs/CONFORMANCE.md`), and no conformance report exists yet.

| Date (UTC) | Client | Runtime | Models | Report |
| --- | --- | --- | --- | --- |
| 2026-10-17 | OpenCode 1.18.7 | LM Studio 0.4.20 | `qwen3-coder-30b-apex`, `qwen3.6-35b-a3b` | [OpenCode through AgentSplice to LM Studio](2026-10-17-opencode-lm-studio/README.md) |
| 2026-10-19 | OpenCode 1.18.7 | llama-server b10662 | `qwen3.6-35b-a3b`, `qwen3-coder-30b-apex` | [OpenCode through AgentSplice to llama-server](2026-10-19-opencode-llama-server/README.md) |
| 2026-10-19 | OpenCode 1.18.7 | Ollama 0.40.6 | `qwen3.6-35b-a3b:latest`, `qwen3-coder-30b-apex:latest` | [OpenCode through AgentSplice to Ollama](2026-10-19-opencode-ollama/README.md) |

## What a report holds

Each report is a folder named `<yyyy-mm-dd>-<client>-<runtime>` — the UTC day its first session began,
and the client's and the runtime's names in lower case with hyphens, followed by `-<n>` for a second
report of the same day — holding three files and nothing else:

- `README.md` — what was run, an `## Exchanges` table whose Exchange column names every exchange by the
  first eight characters of its identifier, the findings, what the report does not show, and how to
  reproduce it. It says that it makes no compatibility claim, and
  it names the commit it ran.
- `report.json` — what the report ran and when:
  - `report`, the folder's name, and `capturedFrom` and `capturedUntil`, the first attempt's start and
    the last one's end, each with its time zone stated;
  - `environment`, every value given as `{ "value", "source" }`, the source saying how it was found: the
    AgentSplice `version`, `commit`, `package` and its `packageSha256`, `configuration`, and
    `contentCapture`; the machine's `operatingSystem` and `gpuDriver`; the runtime's `name`, `version`,
    and `engine`; for each of the `models`, its `id`, `file`, `sha256`, `quantization`,
    `contextLength`, `gpuOffload`, `parallel`, `flashAttention`, `kvCacheType`, and
    `speculativeDecoding`; and the client's `name`, `version`, `invocation`, and `configuration`. That
    is what `docs/CONFORMANCE.md` asks for to reproduce a result. A value nothing reported is
    `"unknown"`, and its source says why;
  - `scenario`, with the `prompt` the sessions were given;
  - `attempts`, each with its `name`, the `model` the client asked for — a model's id, or an alias —
    its `from` and `until`, and the `exchanges` it produced. A digest anywhere in the file is the value
    of a key ending in `sha256`, with or without a `sha256:` prefix.
- `exchanges.json` — what the administrative API answered: `system` from `GET /api/v1/system`, `list`
  holding every page of `GET /api/v1/exchanges`, and for every exchange listed, its `detail` from
  `GET /api/v1/exchanges/{id}` and its `timeline` from `GET /api/v1/exchanges/{id}/timeline`, all
  unchanged, beside `exportedAt`, the one value the export adds.

## Rules

- **The gateway is a package CI built.** A report runs a package CI made for a named commit, checked
  against its digest, with its shipped settings except where the report says otherwise. A CI artifact
  expires; the commit and the digest in `report.json` do not.
- **The evidence is metadata.** Content capture stays off, and every exchange records
  `contentRetentionState: metadata_only`. The report's prompt is a fixture written for it, so it is
  published. Each attempt's prompt carries a random marker of the form `trace-canary-<hex digits>`, and
  the report says where the markers were searched for; the markers themselves are never published.
- **Nothing identifies a person or a machine beyond the hardware the report describes.** No user name,
  host name, path under a home directory, or token.
- **Unknown stays unknown.** A value is never guessed, and every value says where it came from.
- **A report is a record.** Once published its findings are not edited; a correction is a dated note
  added to its `README.md`.

## What the contract test checks, and what it cannot

`TraceReportContractTests` checks, on every build, what a program can check in the files themselves:

- **Names and times.** Each folder is named for the UTC day of its first attempt and for its client and
  runtime — with `-<n>` after them for a second report of the same day — and the time it covers is its
  attempts', every timestamp with its time zone, exported after the sessions.
- **The evidence's shape.** Its top level and each exchange hold only the API's answers and the export
  time; it holds, once each, every exchange its own list names; system information, exchanges,
  measurements, and observations have what the OpenAPI document requires; statuses, terminations,
  failure classes, protocols, units, observation types, and sources are the domain's; usage keeps its
  provenance; each timeline runs in sequence from `request_accepted`, at the exchange's start, to
  `persistence_completed`, or to `metadata_queued` where the completion stamp could not be written; and
  every exchange is `metadata_only`.
- **Content.** No key of the evidence is on a list of names an API puts content under, and no value holds
  four consecutive words of the scenario's prompt or a file it names.
- **Attempts.** Every exchange belongs to exactly one, inside its time, asked of its model, and served by
  a model the environment describes.
- **Sources.** Every value of the environment and the scenario has a source that names one, none is
  empty, an unknown one has a source long enough to give a reason, the required values are there, the
  version and content capture are the evidence's, the commit is the version's build metadata when the
  version has any, and the package digest is a digest.
- **The text.** The report has its statement, its sections, its commit, and an Exchange column naming
  every exchange once; the index has one row per report, with its date, client, runtime, and models.
- **Privacy.** No file holds a path under a home directory, a bearer token, a 64-digit value other than a
  declared digest — the value of a key ending in `sha256`, with or without a `sha256:` prefix — a session
  marker, or, outside CI, the account or machine name of whoever runs the test.
- **The matrix.** Every row of `docs/COMPATIBILITY_MATRIX.md` holds only untested values: Not tested or
  Not implemented, and Unknown, Planned, or N/A.

What no program can check stays with the author and the reviewer: that the export covered every
exchange the sessions produced and no failed attempt was left out; that a value with a plausible source
was not guessed, and that an unknown one's reason is real; that no other content reached an evidence
value, which is what the marker search is for; that prose names nobody; and that a published report is
not edited.
