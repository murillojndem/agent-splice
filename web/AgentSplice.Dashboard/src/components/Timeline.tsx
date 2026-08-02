import type { TimelineObservation } from '../api/types';
import { timestamp } from '../format/evidence';

/**
 * The exchange timeline, in the order the boundaries were recorded.
 *
 * Ordered by `sequence` rather than by `timestamp`, deliberately. Two boundaries can share a clock
 * reading, and a host clock that steps backwards mid-exchange can order two timestamps impossibly —
 * the gateway keeps that visible on purpose rather than clamping it, and a view that re-sorted by
 * time would hide the very anomaly the timeline preserved.
 */
export function Timeline({ observations }: { observations: TimelineObservation[] }) {
  if (observations.length === 0) {
    return <p className="empty">This exchange recorded no boundaries.</p>;
  }

  const ordered = [...observations].sort((left, right) => left.sequence - right.sequence);

  return (
    <ol className="timeline" data-testid="timeline">
      {ordered.map((observation) => (
        <li key={observation.sequence}>
          <span className="timeline-seq">{observation.sequence}</span>
          <span className="timeline-type"><code>{observation.type}</code></span>
          <span className="timeline-at">{timestamp(observation.timestamp)}</span>
          <span className="timeline-source">{observation.source}</span>
          {observation.safeDetails !== undefined && Object.keys(observation.safeDetails).length > 0 && (
            <dl className="details">
              {Object.entries(observation.safeDetails).map(([key, value]) => (
                <div key={key}>
                  <dt>{key}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
            </dl>
          )}
        </li>
      ))}
    </ol>
  );
}
