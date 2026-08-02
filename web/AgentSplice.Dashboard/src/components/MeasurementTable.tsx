import type { Measurement } from '../api/types';
import { measurementValue, provenanceLabel, requiresLabel } from '../format/evidence';

/**
 * Every measurement with the provenance it arrived with (FR-DASH-004, FR-OBS-010).
 *
 * The provenance column is not optional detail. A duration AgentSplice read from its own clock and a
 * token count a runtime asserted are different kinds of claim, and a table that showed only numbers
 * would let a reader compare them as though they were the same kind.
 */
export function MeasurementTable({ measurements }: { measurements: Measurement[] }) {
  if (measurements.length === 0) {
    return <p className="empty">No measurement was derivable from this exchange.</p>;
  }

  const ordered = [...measurements].sort((left, right) => left.name.localeCompare(right.name));

  return (
    <table className="grid" data-testid="measurements">
      <thead>
        <tr>
          <th scope="col">Measurement</th>
          <th scope="col">Value</th>
          <th scope="col">Provenance</th>
        </tr>
      </thead>
      <tbody>
        {ordered.map((entry) => (
          <tr key={entry.name}>
            <td><code>{entry.name}</code></td>
            <td>{measurementValue(entry)}</td>
            <td>
              <span className={requiresLabel(entry.provenance) ? 'provenance provenance-weak' : 'provenance'}>
                {provenanceLabel(entry.provenance)}
              </span>
              {entry.confidence !== undefined && (
                <span className="confidence"> confidence {entry.confidence.toFixed(2)}</span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
