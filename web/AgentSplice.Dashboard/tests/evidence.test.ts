import { describe, expect, it } from 'vitest';
import {
  NAMES,
  UNKNOWN,
  duration,
  measurement,
  measurementValue,
  optional,
  provenanceLabel,
  requiresLabel,
  timestamp,
} from '../src/format/evidence';
import type { Measurement } from '../src/api/types';

/**
 * The rule this whole dashboard turns on: absence is displayed as absence (FR-DASH-006).
 *
 * A formatter is where that rule is easiest to lose. Returning "0 ms" for a phase nobody measured
 * looks tidy and turns "we did not observe this" into "this took no time", which is precisely the
 * misreading the gateway spends its request path refusing to produce.
 */
describe('unknown is never rendered as zero', () => {
  it('renders an absent value as unknown', () => {
    expect(optional(undefined, String)).toBe(UNKNOWN);
    expect(optional(null, String)).toBe(UNKNOWN);
  });

  it('renders a real zero as zero', () => {
    // The other half. A measured zero is a measurement, and hiding it would be the same defect
    // pointing the other way.
    expect(optional(0, String)).toBe('0');
  });

  it('renders an absent measurement as unknown rather than as 0 ms', () => {
    expect(measurementValue(undefined)).toBe(UNKNOWN);
  });

  it('renders an absent timestamp as unknown', () => {
    expect(timestamp(null)).toBe(UNKNOWN);
    expect(timestamp(undefined)).toBe(UNKNOWN);
    expect(timestamp('not a date')).toBe(UNKNOWN);
  });

  it('finds nothing for a measurement the gateway never derived', () => {
    expect(measurement([], NAMES.connect)).toBeUndefined();
  });
});

describe('durations are readable without being overstated', () => {
  it('keeps sub-millisecond precision', () => {
    expect(duration(0.25)).toBe('0.25 ms');
  });

  it('rounds milliseconds', () => {
    expect(duration(41.6)).toBe('42 ms');
  });

  it('switches to seconds where milliseconds stop meaning anything', () => {
    expect(duration(47_000)).toBe('47.00 s');
  });

  it('refuses a non-finite value', () => {
    // A value the gateway will not produce, and a display that would read as a real reading if it
    // slipped through anyway.
    expect(duration(Number.NaN)).toBe(UNKNOWN);
    expect(duration(Number.POSITIVE_INFINITY)).toBe(UNKNOWN);
  });
});

describe('provenance is carried through to the display', () => {
  it('labels the values that are somebody arithmetic rather than an observation', () => {
    expect(requiresLabel('estimated')).toBe(true);
    expect(requiresLabel('inferred')).toBe(true);
    expect(requiresLabel('client_reported')).toBe(true);
  });

  it('does not label a direct observation', () => {
    // Labelling everything is the same as labelling nothing: the point is that a reader can tell a
    // clock reading from an estimate at a glance.
    expect(requiresLabel('measured')).toBe(false);
    expect(requiresLabel('upstream_reported')).toBe(false);
  });

  it('names who made the claim', () => {
    expect(provenanceLabel('upstream_reported')).toBe('reported by runtime');
    expect(provenanceLabel('client_reported')).toBe('reported by client');
    expect(provenanceLabel('measured')).toBe('measured');
  });
});

describe('measurement units', () => {
  const of = (unit: string, value: number): Measurement => ({
    name: 'x',
    value,
    unit,
    provenance: 'measured',
  });

  it('renders each published unit in its own terms', () => {
    expect(measurementValue(of('milliseconds', 250))).toBe('250 ms');
    expect(measurementValue(of('tokens', 1024))).toBe('1,024 tokens');
    expect(measurementValue(of('tokens_per_second', 37.42))).toBe('37.4 tok/s');
    expect(measurementValue(of('bytes', 2048))).toBe('2,048 B');
  });
});
