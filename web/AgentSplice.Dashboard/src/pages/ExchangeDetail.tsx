import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { NAMES, UNKNOWN, measurement, measurementValue, timestamp } from '../format/evidence';
import { Loading, Problem } from '../components/Status';
import { MeasurementTable } from '../components/MeasurementTable';
import { RetentionNotice } from '../components/RetentionNotice';
import { Timeline } from '../components/Timeline';
import { Waterfall } from '../components/Waterfall';

/**
 * One exchange in full: what was sent structurally, where the time went, and how it ended
 * (FR-DASH-004, the Stage 1C exit criterion).
 *
 * Usage and throughput are shown apart from the latency phases rather than folded into one summary
 * row. Token counts come from the runtime and durations from AgentSplice's clock, and a layout that
 * put them side by side under one heading would invite reading a reported number as a measured one.
 */
export function ExchangeDetail() {
  const { exchangeId = '' } = useParams();

  const detail = useQuery({
    queryKey: ['exchange', exchangeId],
    queryFn: ({ signal }) => api.exchange(exchangeId, signal),
    retry: false,
  });

  const timeline = useQuery({
    queryKey: ['timeline', exchangeId],
    queryFn: ({ signal }) => api.timeline(exchangeId, signal),
    retry: false,
  });

  if (detail.isPending) return <Loading />;
  if (detail.error) return <Problem error={detail.error} />;

  const exchange = detail.data;
  const generation = measurement(exchange.measurements, NAMES.generationThroughput);

  return (
    <>
      <p className="crumbs"><Link to="/exchanges">Exchanges</Link> / <code>{exchange.exchangeId}</code></p>

      <h1>
        <code>{exchange.clientModelId ?? UNKNOWN}</code>
        <span className={`pill pill-${exchange.status}`}>{exchange.status}</span>
      </h1>

      <RetentionNotice state={exchange.contentRetentionState} />

      <section className="panel">
        <dl className="facts">
          <div><dt>Started</dt><dd>{timestamp(exchange.startedAt)}</dd></div>
          {/* Null on a terminal exchange means it ended and the moment was not observed. Showing the
              start time again, or a dash with no explanation, would both read as a bug. */}
          <div>
            <dt>Completed</dt>
            <dd data-testid="completed-at">
              {exchange.completedAt === null ? `${UNKNOWN} not observed` : timestamp(exchange.completedAt)}
            </dd>
          </div>
          <div><dt>Runtime</dt><dd>{exchange.runtimeId ?? UNKNOWN}</dd></div>
          <div><dt>Upstream model</dt><dd>{exchange.upstreamModelId ?? UNKNOWN}</dd></div>
          <div>
            <dt>Streaming</dt>
            <dd data-testid="streaming">
              {exchange.streaming === null ? `${UNKNOWN} never stated` : exchange.streaming ? 'yes' : 'no'}
            </dd>
          </div>
          <div><dt>Stream ended</dt><dd>{exchange.streamTermination.replace(/_/g, ' ')}</dd></div>
          <div>
            <dt>Upstream status</dt>
            <dd>{exchange.upstreamStatusCode ?? UNKNOWN}</dd>
          </div>
          <div>
            <dt>Failure</dt>
            <dd data-testid="failure">
              {exchange.errorCode ?? exchange.failureClass ?? 'none'}
            </dd>
          </div>
        </dl>
      </section>

      <Waterfall measurements={exchange.measurements} />

      <section className="panel">
        <h2>Tokens and generation</h2>
        <dl className="facts">
          <div>
            <dt>Prompt tokens</dt>
            <dd>{measurementValue(measurement(exchange.measurements, NAMES.promptTokens))}</dd>
          </div>
          <div>
            <dt>Completion tokens</dt>
            <dd>{measurementValue(measurement(exchange.measurements, NAMES.completionTokens))}</dd>
          </div>
          <div>
            <dt>Generation throughput</dt>
            <dd data-testid="generation-throughput">{measurementValue(generation)}</dd>
          </div>
        </dl>
        <p className="note">
          Generation throughput is measured over the decode window only. There is no prompt-throughput
          figure: nothing observable marks the end of prompt processing, so any such number would be
          time-to-first-token wearing another name.
        </p>
      </section>

      <section className="panel">
        <h2>Measurements</h2>
        <MeasurementTable measurements={exchange.measurements} />
      </section>

      <section className="panel">
        <h2>What was sent</h2>
        {exchange.structuralSummary === undefined ? (
          <p className="empty">No structural summary was built for this request.</p>
        ) : (
          <StructuralSummary summary={exchange.structuralSummary} />
        )}
      </section>

      <section className="panel">
        <h2>Timeline</h2>
        {timeline.isPending && <Loading />}
        {timeline.error && <Problem error={timeline.error} />}
        {timeline.data && <Timeline observations={timeline.data} />}
      </section>
    </>
  );
}

/**
 * Renders the stored summary as the shapes and counts it is.
 *
 * The document is open by contract, so this walks it rather than naming fields — a summary that gains
 * one should show it without a dashboard release. Nothing in it is content: the gateway stores counts,
 * closed-vocabulary tokens, and hashed field names, and this renders whatever of those it finds.
 */
function StructuralSummary({ summary }: { summary: Record<string, unknown> }) {
  return (
    <dl className="facts" data-testid="structural-summary">
      {Object.entries(summary).map(([key, value]) => (
        <div key={key}>
          <dt>{key.replace(/([A-Z])/g, ' $1').toLowerCase()}</dt>
          <dd>{render(value)}</dd>
        </div>
      ))}
    </dl>
  );
}

function render(value: unknown): string {
  if (value === null || value === undefined) return UNKNOWN;
  if (Array.isArray(value)) return value.length === 0 ? 'none' : value.map(render).join(', ');
  if (typeof value === 'object') {
    return Object.entries(value as Record<string, unknown>)
      .map(([key, nested]) => `${key}: ${render(nested)}`)
      .join(', ');
  }

  return String(value);
}
