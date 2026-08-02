import type { Measurement, MeasurementProvenance } from '../api/types';

/**
 * How this dashboard renders things it was not told.
 *
 * Every function here exists to keep one rule: an absent value is displayed as unknown and never as
 * zero (FR-DASH-006). A dashboard is where that rule is easiest to break, because a formatter that
 * takes `number | undefined` and returns `"0 ms"` looks tidy and turns "we did not observe this
 * phase" into "this phase took no time" — which is the exact misreading the gateway spends its whole
 * request path refusing to produce.
 */

/** What is shown where a value would go when there is no value. */
export const UNKNOWN = '—';

/** Renders a value that may be absent, never substituting a zero. */
export function optional<T>(value: T | null | undefined, render: (value: T) => string): string {
  return value === null || value === undefined ? UNKNOWN : render(value);
}

/**
 * Milliseconds, at a precision that does not imply more than was measured.
 *
 * Sub-millisecond durations keep two decimals; anything above a second is shown in seconds, because
 * "a 47000 ms exchange" is a number an operator has to convert before it means anything and
 * wall-clock time is meant to be the prominent figure (docs/SPECIFICATION.md 19.2).
 */
export function duration(milliseconds: number): string {
  if (!Number.isFinite(milliseconds)) return UNKNOWN;
  if (milliseconds < 1) return `${milliseconds.toFixed(2)} ms`;
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`;

  return `${(milliseconds / 1000).toFixed(2)} s`;
}

/** An absolute moment, or unknown. */
export function timestamp(value: string | null | undefined): string {
  if (value === null || value === undefined) return UNKNOWN;

  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? UNKNOWN : parsed.toISOString().replace('T', ' ').replace('Z', 'Z');
}

/**
 * Whether a provenance has to be labelled wherever the value is shown (FR-OBS-010).
 *
 * Measured and upstream-reported values are observations. The rest are somebody's arithmetic, and a
 * reader comparing two exchanges has to be able to see which is which without opening the timeline.
 */
export function requiresLabel(provenance: MeasurementProvenance): boolean {
  return provenance === 'estimated' || provenance === 'inferred' || provenance === 'client_reported';
}

/** Short human wording for a provenance. */
export function provenanceLabel(provenance: MeasurementProvenance): string {
  switch (provenance) {
    case 'measured':
      return 'measured';
    case 'upstream_reported':
      return 'reported by runtime';
    case 'client_reported':
      return 'reported by client';
    case 'runtime_log':
      return 'from runtime log';
    case 'estimated':
      return 'estimated';
    case 'inferred':
      return 'inferred';
    default:
      return provenance;
  }
}

/** Finds one measurement by name, or `undefined` when it was never derived. */
export function measurement(measurements: Measurement[], name: string): Measurement | undefined {
  return measurements.find((candidate) => candidate.name === name);
}

/**
 * Groups digits in a way that cannot be misread.
 *
 * Pinned to one locale rather than the reader's. `toLocaleString()` with no argument follows the
 * machine, and on a pt-BR host it renders 1024 tokens as "1.024" — a number an English-reading
 * operator parses as one. Evidence has to mean the same thing to whoever opens it, so the separator
 * is a property of this surface rather than of the browser that happens to be showing it.
 */
function digits(value: number): string {
  return new Intl.NumberFormat('en-US').format(value);
}

/** Renders a measurement's value with its unit, or unknown when it is absent. */
export function measurementValue(value: Measurement | undefined): string {
  if (value === undefined) return UNKNOWN;

  switch (value.unit) {
    case 'milliseconds':
      return duration(value.value);
    case 'bytes':
      return `${digits(value.value)} B`;
    case 'tokens':
      return `${digits(value.value)} tokens`;
    case 'tokens_per_second':
      return `${value.value.toFixed(1)} tok/s`;
    default:
      return digits(value.value);
  }
}

/** The published measurement names this dashboard reads. */
export const NAMES = {
  total: 'exchange.total.duration',
  validation: 'gateway.validation.duration',
  routing: 'gateway.routing.duration',
  connect: 'upstream.connect.duration',
  headers: 'upstream.headers.duration',
  firstByte: 'upstream.first_byte.duration',
  firstSemanticEvent: 'exchange.first_semantic_event.duration',
  firstClientEvent: 'exchange.first_client_event.duration',
  persistence: 'persistence.duration',
  promptTokens: 'usage.prompt.tokens',
  completionTokens: 'usage.completion.tokens',
  generationThroughput: 'usage.generation.tokens_per_second',
  responseBytes: 'stream.client.bytes',
  streamEvents: 'stream.client.events',
} as const;
