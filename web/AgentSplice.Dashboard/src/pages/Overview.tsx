import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { UNKNOWN, timestamp } from '../format/evidence';
import { Loading, Problem } from '../components/Status';

/**
 * What this deployment is, and whether it is keeping anything.
 *
 * The retention line is the first thing on the page on purpose. A gateway configured with
 * `persistence:mode: None` proxies perfectly and stores nothing, and an operator who does not know
 * that reads an empty exchange list as "nothing happened" rather than "nothing is kept here".
 */
export function Overview() {
  const system = useQuery({ queryKey: ['system'], queryFn: ({ signal }) => api.system(signal) });
  const health = useQuery({ queryKey: ['health'], queryFn: ({ signal }) => api.runtimeHealth(signal) });
  const recent = useQuery({
    queryKey: ['exchanges', 'recent'],
    queryFn: ({ signal }) => api.exchanges({ limit: 5 }, signal),
    retry: false,
  });

  if (system.isPending) return <Loading />;
  if (system.error) return <Problem error={system.error} />;

  const info = system.data;

  return (
    <>
      <h1>Overview</h1>

      <section className="panel">
        <dl className="facts">
          <div><dt>Version</dt><dd>{info.version}</dd></div>
          <div><dt>Stage</dt><dd>{info.stage}</dd></div>
          <div>
            <dt>Exchange metadata</dt>
            <dd data-testid="metadata-retention">
              {info.metadataRetentionEnabled ? 'retained' : 'not retained on this deployment'}
            </dd>
          </div>
          <div>
            <dt>Raw content</dt>
            <dd data-testid="content-retention">
              {info.contentRetentionEnabled ? 'retained' : 'never stored'}
            </dd>
          </div>
        </dl>

        <p className="note">
          Modules: {info.enabledModules.join(', ')}
        </p>
      </section>

      <section className="panel">
        <h2>Runtimes</h2>
        {health.isPending && <Loading />}
        {health.error && <Problem error={health.error} />}
        {health.data && (
          <ul className="health">
            {health.data.map((runtime) => (
              <li key={runtime.runtimeId} data-testid={`health-${runtime.runtimeId}`}>
                <span className={`pill pill-${runtime.status}`}>{runtime.status.replace(/_/g, ' ')}</span>
                <code>{runtime.runtimeId}</code>
                <span className="muted">
                  {runtime.checkedAt === undefined ? 'never consulted' : `checked ${timestamp(runtime.checkedAt)}`}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="panel">
        <h2>Recent exchanges</h2>
        {recent.isPending && <Loading />}
        {recent.error && <Problem error={recent.error} />}
        {recent.data && recent.data.items.length === 0 && (
          <p className="empty">No exchange is retained yet.</p>
        )}
        {recent.data && recent.data.items.length > 0 && (
          <ul className="recent">
            {recent.data.items.map((exchange) => (
              <li key={exchange.exchangeId}>
                <Link to={`/exchanges/${exchange.exchangeId}`}>
                  <code>{exchange.clientModelId ?? UNKNOWN}</code>
                </Link>
                <span className={`pill pill-${exchange.status}`}>{exchange.status}</span>
                <span className="muted">{timestamp(exchange.startedAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
