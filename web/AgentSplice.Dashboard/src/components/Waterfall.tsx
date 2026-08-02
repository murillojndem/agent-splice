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

  const phases = [
    { name: 'Validation', measurement: measurement(measurements, NAMES.validation) },
    { name: 'Routing', measurement: measurement(measurements, NAMES.routing) },
    { name: 'Upstream connect', measurement: measurement(measurements, NAMES.connect) },
    { name: 'Upstream headers', measurement: measurement(measurements, NAMES.headers) },
    { name: 'First upstream byte', measurement: measurement(measurements, NAMES.firstByte) },
    { name: 'First output event', measurement: measurement(measurements, NAMES.firstSemanticEvent) },
    { name: 'First client event', measurement: measurement(measurements, NAMES.firstClientEvent) },
    { name: 'Persisted', measurement: measurement(measurements, NAMES.persistence) },
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
          <PhaseRow key={phase.name} name={phase.name} phase={phase.measurement} total={total} />
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
  phase,
  total,
}: {
  name: string;
  phase: Measurement | undefined;
  total: Measurement | undefined;
}) {
  if (phase === undefined) {
    return (
      <li className="phase phase-unknown" data-testid={`phase-${name}`}>
        <span className="phase-name">{name}</span>
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
      <span className="phase-name">{name}</span>
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
