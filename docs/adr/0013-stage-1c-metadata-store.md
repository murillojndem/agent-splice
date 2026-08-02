# ADR 0013 — The Stage 1C metadata store and the write path

- Status: Accepted
- Date: 2026-08-01
- Supersedes decision 12 of [ADR 0009](0009-stage-1b-streaming-relay.md)
- Discharges the Stage 1C consequence recorded in [ADR 0008](0008-stage-1a-transparent-request-path.md)

## Context

Stages 1A and 1B built a proxy that gathers complete evidence for every completion exchange — an
immutable `CompletionExchange`, a sequence-ordered `ExchangeTimeline`, measurements carrying their
provenance — and then hands all of it to `NullExchangeRecordSink`, which discards it. A product whose
thesis is evidence retained none of it.

This ADR records the decisions taken in making exchanges survive the process. The administrative API
that reads the store, retention, and the dashboard are later slices of the same stage.

## Decisions

### 1. Rows are separate types from the domain records

`CompletionExchange` and `ExchangeObservation` are immutable, constructed through validating
factories, and carry value-object identifiers. Mapping them with EF Core would require parameterless
constructors, settable properties, and conversions declared against them — persistence conventions
inside `AgentSplice.Domain`, which `docs/ARCHITECTURE.md` forbids.

The separation buys a second thing that turned out to matter more. A row is not obliged to be
expressible as a `CompletionExchange`, so the store can record a request the domain refuses to model.

**Nothing maps a row back to a domain record.** The read path serves views. Reconstructing the
aggregate would force the store to invent the values a model-less request never had, which is the
same fabrication the domain declined to perform.

### 2. A request refused before its envelope was read is stored anyway

`CompletionExchange.Accept` requires a valid `ClientModelId`, so a body that does not parse produces
no exchange. ADR 0008 accepted that and recorded the consequence: `/api/v1/exchanges` would not list
such requests, a gap owned by Stage 1C.

It is discharged here rather than carried forward, and by neither of the two routes ADR 0008
anticipated. It expected `Accept` to be relaxed or a distinct rejected-request artifact to be
introduced; decision 1 makes a third route available, because the row is already not the aggregate.
`ExchangeRow.ClientModelId` and `ExchangeRow.Streaming` are nullable, so a refused request is stored
with both absent, together with its timeline and its error code, while `CompletionExchange` keeps
refusing to exist without a model. A malformed client and a client that never connected are otherwise
indistinguishable, and the first is a support question while the second is an outage.

Note what this does *not* cover: a request naming a model AgentSplice cannot resolve **does** have an
exchange, because the exchange is opened before resolution runs — deliberately, so that exactly this
case leaves evidence. Its row keeps the client's own model string and records no runtime.

### 3. Structural summaries hold vocabulary or hashes, never caller-chosen text

Found in review of this slice, and the reason it is a decision rather than a patch: the summaries were
already bounded in length and count, and that had been treated as making them safe. It does not. A
client picks the value of `role` and the name of every JSON property it sends; a runtime picks the
value of `finish_reason`. Truncating those to 64 characters bounds how much caller-chosen text is kept
and leaves every character of the remainder caller-chosen.

That was tolerable while summaries lived in process memory and a trace attribute. This slice writes
them to a database that content capture is documented as keeping empty, so `"role": "<prompt
fragment>"` became a retained prompt fragment. The privacy test did not catch it because it put its
sentinels in `content`, which the gateway never reads.

Two rules, by what the value is:

- A value with a protocol vocabulary — role, finish reason — is matched against it and bucketed when
  it does not match. The vocabulary is closed, so nothing caller-chosen survives, and folding rather
  than dropping keeps the per-role counts reconcilable with the message count.
- A value with no vocabulary — the name of an unknown request field — is hashed. There is nothing to
  match it against, and it is recorded to make transparent forwarding verifiable, which a stable
  identifier does as well as the name. An operator asking whether `top_k` was forwarded hashes `top_k`
  and compares.

A hash rather than a bucket, because bucketing every unknown name to one marker would destroy the
evidence FR-CHAT-004 and FR-TRACE-008 want. A hash rather than the name, because the threat is a third
party reading stored evidence, and that party cannot invert a digest of text it does not already have.

The same change removed a denial of service: the old helper *threw* on a control character in a name,
so `"role": "ab"` turned client input into a failed request. Input validation that rejects by
throwing, on a value the client controls, is a fault channel wearing the costume of a guard.

Also removed: `MaxRoleNames`, `RoleNamesTruncated`, `MaxFinishReasons`, `FinishReasonsTruncated`, and
`MaxFieldNameLength`. A closed vocabulary cannot exceed its own size, so those bounds and their
visibility flags became unreachable, and an unreachable contract is the thing this repository keeps
having to delete.

### 4. The store stamps its own boundaries, in two transactions

`MetadataQueued` is read by the sink as the record enters the queue. `PersistenceCompleted` is read by
the writer *after* the commit returned, which requires a second transaction.

The one-transaction alternative is cheaper and dishonest: a boundary named "completed" stamped inside
the transaction it describes reports a moment that had not happened yet. That is the defect class
[ADR 0010](0010-correct-stream-boundary-and-termination-semantics.md) exists to prevent, and the cost
of avoiding it is one small insert per batch, off the request path. When the second transaction fails,
the exchange's timeline ends at `MetadataQueued`, which reads as exactly what occurred.

`persistence.duration` is written in that same second transaction, measured queue-to-durable per
exchange rather than as the batch's write time. A batch covers many exchanges; attributing its
duration to each would report one number N times and overstate every one. The name had been declared
in `MeasurementNames` since Stage 1A with nothing able to produce it.

### 5. `PersistenceFailed` is not a stored observation

The store that rejected the write is the one the row would have to live in. A failure is a log event
with a stable ID and an `agentsplice.persistence.failures` increment; nothing pretends to be evidence
that survived.

The asymmetry is deliberate and worth stating, because a reader who finds `MetadataQueued` and
`PersistenceCompleted` in the schema will look for the third and should not conclude it was forgotten.

### 6. The queue refuses rather than drops silently

The bounded channel uses `BoundedChannelFullMode.Wait` and is never awaited. `TryWrite` under that
mode returns `false` immediately when full, which is the only configuration that both refuses to block
and reports the refusal.

`DropWrite` reads like the intent and is not: it returns `true` having discarded the record. The first
version of this code used it, and every drop was silent while the counter stayed at zero. `DropOldest`
discards a record that had nearly survived in favour of one that has not yet queued.

Full means drop, with a counter and a log line. Waiting turns a slow store into gateway latency and
then into a stalled stream; growing without limit turns it into an out-of-memory kill that takes the
proxy down with it.

### 7. A failed batch is dropped, not retried

Retrying reorders evidence behind whatever arrived while it retried, or stalls the queue forever
behind a record the store will never accept — and a stalled queue loses every exchange after it, not
only the one that failed.

A batch is up to 64 exchanges, so a single unacceptable row loses the 63 beside it. That amplification
is accepted **for as long as no per-record failure mode exists**, which is the case today: every
string the store writes is already bounded by the domain rule that produced it, the primary key is
guarded against reuse by the recorder's one-shot recording latch, and SQLite does not enforce column
lengths in any event. Every failure this build can actually suffer — a locked database, a full disk, a
missing file — fails the whole batch regardless, and isolating each record would mean 64 further
attempts against a store that has just refused one.

It stops being true when PostgreSQL ships, because that provider does enforce lengths, and
`CompletionExchange.WithEnvironmentSnapshot` accepts an identifier the domain does not bound against
the 128-character column waiting for it. The slice that adds the provider owns either bounding that
value or writing each record independently on failure.

### 8. The wait for work is cancellable; the write is not

`WaitToReadAsync` takes the stopping token. `SaveChangesAsync` takes `CancellationToken.None`.

Records that have left the queue are held nowhere else, so a token that aborted the write would
discard them — and the token available at shutdown fires exactly when the queue is most likely to hold
a backlog. This is the rule the gateway already applies when it hands a cancelled request's evidence
to the sink: the evidence for an interrupted operation is the evidence most worth keeping
(FR-DATA-009). The host's shutdown timeout still bounds how long stopping takes.

The shutdown flush lives at the end of `ExecuteAsync` rather than in `StopAsync`, so one reader drains
the channel. A second reader in `StopAsync` would race the loop whenever the shutdown timeout elapsed
first.

### 9. Whether a store exists is decided at resolution, never at registration

Reading `IConfiguration` while services are being registered reads it half-built: a host layers its
sources as it is assembled, and a test host adds its overrides after the composition root has run. The
first version did that, and the store registered and migrated while the gateway used the discarding
sink — the two halves disagreeing about the same setting.

The context factory and both hosted services are registered unconditionally and consult
`IOptions<AgentSpliceOptions>` when resolved. A registration is not a connection: with persistence off
nothing creates a context, so no provider initialises and no file appears.

### 10. A store that cannot be opened fails startup; a write that fails later does not

Different classes of problem. A store that cannot be migrated at all is a deployment fault — a bad
path, a read-only volume, a schema from a newer build — and NFR 14.2 puts that before readiness. A
write that fails at runtime is a condition the gateway must survive (FR-DATA-009).

Starting silently with a broken store would produce a gateway that proxies perfectly and retains
nothing, which is the one failure an evidence product must not have quietly.

### 11. The model is provider-neutral, and the schema is versioned

No SQLite type names, no provider-specific value generation, no raw SQL, timestamps as UTC ticks.
FR-DATA-003 commits to PostgreSQL through the same contracts, and a model that must be rewritten to
honour that commitment is not a contract. `PersistenceMode.Postgres` has no provider in this build and
is refused rather than quietly served by SQLite.

Migrations rather than `EnsureCreated`, because an existing store has to survive an upgrade and
`EnsureCreated` leaves no way to alter one.

### 12. The OpenTelemetry SDK moves from Stage 1C to Stage 1D

ADR 0009 deferred the SDK to Stage 1C, "which is when persistence and the trace API give it something
to export". Persistence now exists and the reasoning did not survive contact with it: what the SDK
adds is an *exporter*, and no exporter is configured or documented until packaging in Stage 1D. Taking
the dependency now would add a package, delete a working `ActivityListener`, and change nothing an
operator can observe.

`agentsplice.persistence` becomes a live activity source in this slice regardless, because the writer
produces spans on it — the requirement was that no source be subscribed to without a producer, and
that is now satisfied by the producer rather than by the SDK.

## Consequences

- `AgentSplice.Infrastructure` is the only project referencing EF Core, enforced by an architecture
  test rather than by review.
- `ErrorCodes.PersistenceUnavailable` stays declared and unemittable. ADR 0008 expected Stage 1C to
  make it reachable; FR-DATA-009 forbids surfacing a persistence failure to a completion client, so
  the code becomes reachable only on the administrative surface, where a store that cannot be read is
  the client's problem. That is a later slice of this stage.
- Integration hosts default to `persistence:mode: None`. Without it every test that boots the host
  inherits the shipped SQLite default and creates a database file in the test output directory, shared
  between test classes that run in parallel. The cost is that no test boots the shipped persistence
  block any more, so a contract test asserts it against `appsettings.json` directly — otherwise a
  wrong mode or a blank connection string would surface as a failed startup in production and nowhere
  else.
- The API composes persistence and must never query it. `AllProductionAssemblies` in the
  module-boundary tests excludes the API, so that direction is asserted separately in
  `EndpointBoundaryTests` alongside the other rules about what an endpoint may reach for.
- The exchange record handed to `IExchangeRecordSink` never contains the persistence boundaries, so
  they are observable only by reading the store. Tests asserting their absence from the in-memory
  record are asserting the design, not a gap.

## Alternatives considered

**Map the domain records with EF Core directly.** Rejected; see decision 1.

**Store `PersistenceFailed` rows on a best-effort basis.** Rejected. The write that would record the
failure is the write that just failed, and a row that appears only when the store is healthy enough to
accept it is not evidence of anything.

**Attribute batch write time to each exchange as `persistence.duration`.** Rejected; see decision 3.

**Await the queue when full, bounding latency with a short timeout.** Rejected. It makes the store's
health a component of request latency, and the timeout would have to be tuned against a database an
operator has not sized.
