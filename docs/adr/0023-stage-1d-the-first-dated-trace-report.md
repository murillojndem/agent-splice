# ADR 0023 — Stage 1D: the first dated trace report

- Status: Accepted
- Date: 2026-10-17
- Related: [ADR 0006](0006-durable-core-and-adapter-lifecycle.md) (evidence before adapters),
  [ADR 0013](0013-stage-1c-metadata-store.md) (what the store holds and the administrative API serves),
  [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the package the report ran),
  [ADR 0019](0019-stage-1d-sample-client-configurations-and-a-quickstart.md) (the OpenCode sample, and
  the streaming gap the report met)

## Context

`docs/SPECIFICATION.md` section 22 lets the first public alpha be tagged only once "at least one real
client/runtime trace report is published" (criterion 17), and the roadmap's Stage 1D lists a first dated
trace report and asks that "one real client/runtime interaction is captured without semantic
normalization". Everything else Stage 1 proves, it proves against a deterministic fake upstream; this
is the one requirement only a real client and a real runtime on a real machine can meet.

Nothing defined what a trace report is. `docs/CONFORMANCE.md` says what any published report must hold
to be reproducible, and `docs/BENCHMARKING.md` what an environment snapshot records, but there is no
conformance runner yet, and a report from one machine is not a conformance result. The risk the threat
model names for reports — estimates presented as measurements, runs left out, one environment
generalized — applies to a trace report as much as to a benchmark, and so does the privacy rule: a
report about real sessions is published, and must carry none of what those sessions said.

## Decisions

### 1. A report is a folder of three files

`docs/reports/<yyyy-mm-dd>-<client>-<runtime>/`, dated by the UTC day its first session began and named
for its client and runtime, with `-<n>` after them for a second report of the same day, holds `README.md` — the report a person reads — `report.json`, and
`exchanges.json`, and nothing else. `docs/reports/README.md` lists every report, states these rules,
names the keys of both JSON files, and says which rules the contract test checks and which it cannot.

### 2. The evidence is the administrative API's own answers

`exchanges.json` is `GET /api/v1/system`, every page of `GET /api/v1/exchanges`, and for every exchange
listed, `GET /api/v1/exchanges/{id}` and its timeline, unchanged. Nothing is summarized into it or
dropped from it, and the export adds only the time it was made: an analysis belongs in `README.md`, and
a reader can check it against the evidence. Keeping the list's own pages lets a reader, and the
contract test, see that every exchange the list named is there. Every exchange belongs to exactly one
attempt in `report.json`, so none is left out unexplained — a failed attempt is as much a result as a
successful one.

### 3. Every value of the environment says where it came from

`report.json` gives each value of the environment as `{ "value", "source" }`: the gateway's build,
commit, package digest, and configuration; the machine and its driver; the runtime, its version and
engine; each model's file, digest, quantization, context, offload, parallelism, attention, cache, and
speculative-decoding settings; and the client, its version, invocation, and configuration. A value
nothing reported is `"unknown"`, with the reason, and is never inferred from a default. This is
`docs/CONFORMANCE.md`'s reproducibility list, with the provenance rule the measurements already follow.

### 4. The gateway is a package CI built, with its shipped settings

A report runs a package CI built for a named commit, checked against its digest, with the settings it
ships except where the report says otherwise, so that what it shows is what a release of that commit
would carry. The first report ran the Windows package of a commit on `main`, before any release, with
one setting added, the administrative token. A CI artifact expires and needs a sign-in to download; the
commit and the digest the report records do not.

### 5. The evidence holds no content, and the report says how that was checked

Content capture stays off, and every exchange records `contentRetentionState: metadata_only`. The
scenario's prompt is a fixture written for the report, so the report publishes it. Each attempt's
prompt carries a random marker of the form `trace-canary-<hex digits>`; the report states where the
markers were searched for — the gateway's log, its store, and the evidence — and where the same search
found them, as a control that the search works. The markers themselves are not published: one in the report would make that check meaningless.
No user name, host name, path under a home directory, or token appears anywhere in a report.

### 6. A report claims nothing

Every report says that it makes no compatibility claim. It records what one machine did, once per
model and client environment; it is not a conformance run, and it does not evaluate a model or the
task's result, which is Stage 3's. `docs/COMPATIBILITY_MATRIX.md` changes status only with a conformance
report, and while none exists, every row holds only untested values.

### 7. A report is a record

Once published, a report's findings are not edited to match a later build. A correction is a dated
note added to its `README.md`; a defect it found is fixed in the code and recorded in the changelog,
and the report keeps describing the build it ran.

### 8. The first report

`docs/reports/2026-10-17-opencode-lm-studio/` ran OpenCode 1.18.7 through the Windows package of
commit `b17d169` to LM Studio 0.4.20 with two models. Its findings become follow-ups rather than
changes here, because none of them is about what a report is:

- a runtime error carried inside a `200` stream is recorded as a completion — LM Studio could not
  render the first model's chat template for OpenCode's agent requests;
- the ADR 0019 gap, met in a real session: a stream that timed out before its first byte reached
  OpenCode as a closed socket, and OpenCode retried, recording the failure in its log but not in its
  session output;
- an exchange records an error code that no envelope delivered;
- `/api/v1/system` reported stage `1C`.

## Consequences

- SPECIFICATION section 22 criterion 17 is met, and the roadmap's first dated trace report is done.
- A report is produced by hand, on a machine CI never sees, and nothing re-runs it. The contract test
  checks what a program can in the files; the rest — that the export was complete, that no value was
  guessed, that prose names nobody — is the author's and the reviewer's, and `docs/reports/README.md`
  says which is which.
- A new report means a new folder and a row in the index: the format and its checks exist, and the
  export is the documented API calls, made by hand.
- Reports publish hardware details. The maintainer chose to publish the full snapshot for the first
  one; a report with less says what it left out.
- No runtime behaviour changes.

## Tests proving these decisions

- `TraceReportContractTests`:
  - at least one report is published, and the index has one row per report, giving its date, client,
    runtime, and models as `report.json` does;
  - each report is exactly its three files, named for the UTC day of its first attempt and for its
    client and runtime, with an optional `-<n>`; the time it covers is its attempts', every timestamp
    states its time zone, and the evidence was exported after the sessions;
  - the evidence's top level and each exchange hold only the API's answers and the export time; it
    holds every exchange its own list names, once; system information, every exchange, every
    measurement, and every observation has what the OpenAPI document requires; statuses, terminations,
    failure classes, protocols, units, observation types, and sources are the domain's; usage keeps its
    provenance; each timeline runs in sequence from `request_accepted` at the exchange's start to
    `persistence_completed`, or `metadata_queued` where the completion stamp failed; and every exchange
    is `metadata_only`;
  - no key in the evidence is on a list of names an API puts content under, and no value holds four
    consecutive words of the scenario's prompt or a file it names;
  - every exchange belongs to exactly one attempt, inside that attempt's time, asked of its model and
    served by a model the environment describes;
  - every value of the environment and the scenario has a source that names one, none is empty, an
    unknown one has a source long enough to give a reason, the environment gives everything decision 3
    lists, its version and content capture are the evidence's, its commit is the version's build
    metadata when there is any, and its package digest is a digest;
  - the report says that it makes no compatibility claim, names its commit, has its sections, and its
    exchanges table's Exchange column names every exchange once;
  - no file of a report holds a path under a home directory, in any case, with or without a drive, a
    bearer token, a 64-digit value that is not a declared digest, a session marker, or, outside CI, the
    account or machine name of whoever runs the test;
  - every row of the compatibility matrix holds only untested values while no conformance report
    exists.

## Alternatives considered

- **A report as one Markdown document.** Easier to write, but the evidence would be whatever the author
  copied into it, and nothing could check it against the API's own output.
- **A collector the gateway serves, or a script in the repository.** A report is written once per
  release at most, and its export is a few documented API calls; a tool would be a second thing to keep
  correct before there is a second report. An issue-bundle export is a later stage's.
- **Fixing the report's findings in the same change.** Each changes what the gateway records or sends,
  and each deserves its own tests and decision; the report describes the build it ran either way.
- **Leaving failed attempts out.** The report would read better and say less: three of its four
  attempts failed, and what failed is what the evidence is for.
