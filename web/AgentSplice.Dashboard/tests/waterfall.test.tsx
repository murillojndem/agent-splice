import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Waterfall } from '../src/components/Waterfall';
import { MeasurementTable } from '../src/components/MeasurementTable';
import { RetentionNotice } from '../src/components/RetentionNotice';
import { Timeline } from '../src/components/Timeline';
import type { Measurement, TimelineObservation } from '../src/api/types';

const measured = (name: string, value: number): Measurement => ({
  name,
  value,
  unit: 'milliseconds',
  provenance: 'measured',
});

describe('the latency waterfall', () => {
  it('lists a phase that was never observed and marks it unknown', () => {
    // A request served from a warm connection pool establishes no connection. Dropping the row would
    // read as "does not apply" and a zero-width bar as "took no time"; the absence is the finding.
    render(<Waterfall measurements={[measured('exchange.total.duration', 1000)]} />);

    expect(screen.getByTestId('phase-value-Upstream connect')).toHaveTextContent('not observed');
  });

  it('shows wall-clock time as the prominent figure', () => {
    render(<Waterfall measurements={[measured('exchange.total.duration', 47_000)]} />);

    expect(screen.getByTestId('wall-clock')).toHaveTextContent('47.00 s');
  });

  it('shows unknown wall-clock rather than zero when the total was never derived', () => {
    render(<Waterfall measurements={[measured('gateway.routing.duration', 3)]} />);

    expect(screen.getByTestId('wall-clock')).toHaveTextContent('—');
  });

  it('never presents the time to first output as prompt throughput', () => {
    // Nothing observable marks the end of prompt processing, so that interval covers the prompt, the
    // queue, and the network together (FR-OBS-005).
    render(<Waterfall measurements={[measured('exchange.first_semantic_event.duration', 900)]} />);

    // No phase row and no value is offered under that name. The note below the waterfall says the
    // words, which is the point: it explains the absence rather than filling it.
    expect(screen.queryByTestId('phase-Prompt throughput')).toBeNull();
    expect(screen.getByText(/cannot observe where prompt processing ends/i)).toBeInTheDocument();
  });
});

describe('the measurement table', () => {
  it('labels a value that is an estimate rather than an observation', () => {
    render(
      <MeasurementTable
        measurements={[
          { name: 'usage.prompt.tokens', value: 41, unit: 'tokens', provenance: 'estimated' },
        ]}
      />,
    );

    expect(screen.getByText('estimated')).toBeInTheDocument();
  });

  it('states where every value came from', () => {
    render(
      <MeasurementTable
        measurements={[
          { name: 'usage.prompt.tokens', value: 41, unit: 'tokens', provenance: 'upstream_reported' },
          measured('exchange.total.duration', 120),
        ]}
      />,
    );

    expect(screen.getByText('reported by runtime')).toBeInTheDocument();
    expect(screen.getByText('measured')).toBeInTheDocument();
  });

  it('says so when nothing was derivable rather than showing an empty table', () => {
    render(<MeasurementTable measurements={[]} />);

    expect(screen.getByText(/no measurement was derivable/i)).toBeInTheDocument();
  });
});

describe('the retention notice', () => {
  it('states that content was never stored rather than leaving its absence to be guessed at', () => {
    render(<RetentionNotice state="metadata_only" />);

    expect(screen.getByTestId('retention')).toHaveTextContent(/never stored/i);
  });

  it('distinguishes a deployment that retained nothing at all', () => {
    render(<RetentionNotice state="disabled" />);

    expect(screen.getByTestId('retention')).toHaveTextContent(/nothing was retained/i);
  });
});

describe('the timeline', () => {
  const at = (sequence: number, type: string, iso: string): TimelineObservation => ({
    sequence,
    type,
    timestamp: iso,
    source: 'gateway',
  });

  it('orders by recorded sequence rather than by timestamp', () => {
    // A host clock that steps backwards mid-exchange orders two timestamps impossibly. The gateway
    // keeps that visible on purpose, so re-sorting by time would hide the anomaly it preserved.
    render(
      <Timeline
        observations={[
          at(1, 'upstream_headers_received', '2026-08-02T10:00:00.000Z'),
          at(0, 'request_accepted', '2026-08-02T10:00:05.000Z'),
        ]}
      />,
    );

    const rows = screen.getByTestId('timeline').querySelectorAll('li');

    expect(rows[0]).toHaveTextContent('request_accepted');
    expect(rows[1]).toHaveTextContent('upstream_headers_received');
  });
});
