import type { UpstreamOutcome } from '../api/types';
import { UNKNOWN } from '../format/evidence';

/**
 * What the runtime's answer said about how it went, beside the exchange's status (ADR 0026).
 *
 * The status pill no longer carries success: `completed` means the gateway forwarded a request and
 * relayed an answer, and a runtime that answered 500, or reported an error inside a 200 stream,
 * completed too. Painted as a success, both read as healthy. Absent for an exchange that did not
 * complete, whose status and failure already say how it ended.
 */
export function Outcome({ outcome }: { outcome: UpstreamOutcome | undefined }) {
  if (outcome === undefined) {
    return <span className="muted">{UNKNOWN}</span>;
  }

  return (
    <span className={`pill pill-${outcome}`} data-testid="outcome">
      {outcome.replace(/_/g, ' ')}
    </span>
  );
}
