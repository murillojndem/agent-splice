import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { ExchangeStatus } from '../api/types';
import { UNKNOWN, timestamp } from '../format/evidence';
import { Loading, Problem } from '../components/Status';

const STATUSES: ExchangeStatus[] = ['completed', 'failed', 'cancelled', 'streaming', 'forwarding', 'accepted'];

/**
 * The exchange list, paged by the gateway's opaque cursor.
 *
 * Cursors are kept as a stack rather than as an index, because the gateway's cursor carries a
 * position in a keyset and no arithmetic turns "page 3" back into one. Going back means remembering
 * where you came from.
 */
export function Exchanges() {
  const [status, setStatus] = useState('');
  const [cursors, setCursors] = useState<string[]>([]);
  const cursor = cursors.length > 0 ? cursors[cursors.length - 1] : undefined;

  const query = useQuery({
    queryKey: ['exchanges', status, cursor],
    queryFn: ({ signal }) => api.exchanges({ status, cursor, limit: 50 }, signal),
    retry: false,
  });

  return (
    <>
      <h1>Exchanges</h1>

      <div className="filters">
        <label>
          Status
          <select
            value={status}
            onChange={(event) => {
              setStatus(event.target.value);
              setCursors([]);
            }}
          >
            <option value="">any</option>
            {STATUSES.map((candidate) => (
              <option key={candidate} value={candidate}>{candidate}</option>
            ))}
          </select>
        </label>
      </div>

      {query.isPending && <Loading />}
      {query.error && <Problem error={query.error} />}

      {query.data && query.data.items.length === 0 && <p className="empty">No exchange matches.</p>}

      {query.data && query.data.items.length > 0 && (
        <table className="grid" data-testid="exchanges">
          <thead>
            <tr>
              <th scope="col">Started</th>
              <th scope="col">Model</th>
              <th scope="col">Runtime</th>
              <th scope="col">Status</th>
              <th scope="col">Streaming</th>
              <th scope="col">Retained</th>
            </tr>
          </thead>
          <tbody>
            {query.data.items.map((exchange) => (
              <tr key={exchange.exchangeId}>
                <td>
                  <Link to={`/exchanges/${exchange.exchangeId}`}>{timestamp(exchange.startedAt)}</Link>
                </td>
                {/* Absent when the request was refused before its envelope was read. That is a fact
                    about the request, so it reads as unknown rather than as an empty cell. */}
                <td><code>{exchange.clientModelId ?? UNKNOWN}</code></td>
                <td>{exchange.runtimeId ?? UNKNOWN}</td>
                <td><span className={`pill pill-${exchange.status}`}>{exchange.status}</span></td>
                {/* Null is a third value: the client never stated a preference. */}
                <td data-testid={`streaming-${exchange.exchangeId}`}>
                  {exchange.streaming === null ? UNKNOWN : exchange.streaming ? 'yes' : 'no'}
                </td>
                <td className="muted">{exchange.contentRetentionState.replace(/_/g, ' ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="pager">
        <button
          type="button"
          disabled={cursors.length === 0}
          onClick={() => setCursors((stack) => stack.slice(0, -1))}
        >
          Newer
        </button>
        <button
          type="button"
          disabled={!query.data || query.data.nextCursor === null}
          onClick={() =>
            setCursors((stack) => (query.data?.nextCursor ? [...stack, query.data.nextCursor] : stack))
          }
        >
          Older
        </button>
      </div>
    </>
  );
}
