import type { ContentRetentionState } from '../api/types';

/**
 * States plainly what was kept for an exchange (FR-DASH-005, FR-TRACE-010).
 *
 * The dashboard never renders prompts or model output, and this is what says so rather than leaving
 * a reader to notice their absence and wonder whether it is a bug. `metadata_only` is the normal
 * answer in every shipped build; `sanitized_content` is unreachable today and is written out anyway,
 * because the day it becomes reachable is the day this notice has to be right.
 */
export function RetentionNotice({ state }: { state: ContentRetentionState }) {
  const text: Record<ContentRetentionState, string> = {
    disabled: 'Nothing was retained for this exchange.',
    metadata_only:
      'Structural metadata only. Prompts, model output, and tool arguments were never stored, so there is nothing here to show.',
    sanitized_content: 'Sanitised content was retained under an explicit opt-in and requires authorization to view.',
    expired: 'Retained content passed its retention window and was removed.',
    deleted: 'Retained content was deleted on request.',
  };

  return (
    <p className="retention" data-testid="retention" data-state={state}>
      {text[state]}
    </p>
  );
}
