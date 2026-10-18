import type { Measurement } from '../api/types';
import { NAMES, UNKNOWN, duration, measurement, provenanceLabel, requiresLabel } from '../format/evidence';

/**
 * The latency waterfall (FR-DASH-004).
 *
 * Two rules give this component its shape.
 *
 * A phase that was not observed is **listed and marked unknown** rather than drawn as a zero-width
 * bar or dropped. A missing bar reads as "this took no time" and a missing row reads as "this does
 * not apply", and both are wrong for a phase that simply was not measured — a request served from a
 * warm connection pool establishes no connection, and that absence is itself the finding.
 *
 * Prompt processing and generation are never drawn as one bar. Nothing AgentSplice can observe marks
 * the end of prompt processing, so the interval before the first output event contains the prompt,
 * the queue, and the network together; showing it beside generation throughput as though the two were
 * comparable is the conflation the whole product exists to remove (FR-OBS-005).
 */
export function Waterfall({ measurements }: { measurements: Measurement[] }) {
  const total = measurement(measurements, NAMES.total);

  // Each phase is measured from its own starting boundary, and the rows do not share one: the upstream
  // rows start when the request to the runtime was opened, the output and client rows when the request
  // was accepted. Every bar is drawn from zero, so each row names where its zero is, or a reader takes
  // the difference between two rows for a gap that it is not (ADR 0027).
  const phases = [
    { name: 'Validation', since: 'body read', measurement: measurement(measurements, NAMES.validation) },
    { name: 'Routing', since: 'request summarised', measurement: measurement(measurements, NAMES.routing) },
    { name: 'Upstream connect', since: 'connection began', measurement: measurement(measurements, NAMES.connect) },
    { name: 'Upstream headers', since: 'upstream request opened', measurement: measurement(measurements, NAMES.headers) },
    { name: 'First upstream byte', since: 'upstream request opened', measurement: measurement(measurements, NAMES.firstByte) },
    { name: 'First output event', since: 'request accepted', measurement: measurement(measurements, NAMES.firstSemanticEvent) },
    { name: 'First client event', since: 'request accepted', measurement: measurement(measurements, NAMES.firstClientEvent) },
    { name: 'Persisted', since: 'record queued', measurement: measurement(measurements, NAMES.persistence) },
  ];

  return (
    <section className="panel" aria-labelledby="waterfall-heading">
      <div className="panel-head">
        <h2 id="waterfall-heading">Where the time went</h2>
        <p className="wall-clock" data-testid="wall-clock">
          {total === undefined ? UNKNOWN : duration(total.value)}
          <span className="wall-clock-label">wall clock</span>
        </p>
      </div>

      <ol className="waterfall">
        {phases.map((phase) => (
          <PhaseRow key={phase.name} name={phase.name} since={phase.since} phase={phase.measurement} total={total} />
        ))}
      </ol>

      <p className="note">
        The interval to the first output event covers prompt processing, queueing, and the network
        together. AgentSplice cannot observe where prompt processing ends, so it is never reported as
        prompt throughput.
      </p>
    </section>
  );
}

function PhaseRow({
  name,
  since,
  phase,
  total,
}: {
  name: string;
  since: string;
  phase: Measurement | undefined;
  total: Measurement | undefined;
}) {
  const label = (
    <span className="phase-name">
      {name}
      <span className="phase-origin" data-testid={`phase-origin-${name}`}>since {since}</span>
    </span>
  );

  if (phase === undefined) {
    return (
      <li className="phase phase-unknown" data-testid={`phase-${name}`}>
        {label}
        <span className="phase-bar-track" aria-hidden="true" />
        <span className="phase-value" data-testid={`phase-value-${name}`}>
          {UNKNOWN} <span className="phase-unknown-why">not observed</span>
        </span>
      </li>
    );
  }

  // Only drawn when there is a total to be a fraction of. Scaling a bar against a total that was
  // itself never measured would invent the proportion the bar is meant to convey.
  const width =
    total !== undefined && total.value > 0
      ? Math.max(0.5, Math.min(100, (phase.value / total.value) * 100))
      : null;

  return (
    <li className="phase" data-testid={`phase-${name}`}>
      {label}
      <span className="phase-bar-track">
        {width !== null && <span className="phase-bar" style={{ width: `${width}%` }} />}
      </span>
      <span className="phase-value" data-testid={`phase-value-${name}`}>
        {duration(phase.value)}
        {requiresLabel(phase.provenance) && (
          <span className="provenance provenance-weak">{provenanceLabel(phase.provenance)}</span>
        )}
      </span>
    </li>
  );
}
