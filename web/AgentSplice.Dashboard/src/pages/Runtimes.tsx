import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { UNKNOWN, timestamp } from '../format/evidence';
import { Loading, Problem } from '../components/Status';

/**
 * Configured runtimes, their health, and the models they offer.
 *
 * Health is shown as the gateway reports it, including the two states a naive check calls healthy: a
 * runtime that answers with no models, and one that answers with something the protocol module cannot
 * read. Both break an agent client while looking fine.
 */
export function Runtimes() {
  const runtimes = useQuery({ queryKey: ['runtimes'], queryFn: ({ signal }) => api.runtimes(signal) });
  const health = useQuery({ queryKey: ['health'], queryFn: ({ signal }) => api.runtimeHealth(signal) });
  const models = useQuery({ queryKey: ['models'], queryFn: ({ signal }) => api.models(signal) });

  if (runtimes.isPending) return <Loading />;
  if (runtimes.error) return <Problem error={runtimes.error} />;

  const healthById = new Map((health.data ?? []).map((entry) => [entry.runtimeId, entry]));

  return (
    <>
      <h1>Runtimes</h1>

      <table className="grid" data-testid="runtimes">
        <thead>
          <tr>
            <th scope="col">Runtime</th>
            <th scope="col">Provider</th>
            <th scope="col">Base URL</th>
            <th scope="col">Credential</th>
            <th scope="col">Health</th>
            <th scope="col">Checked</th>
          </tr>
        </thead>
        <tbody>
          {runtimes.data.map((runtime) => {
            const state = healthById.get(runtime.runtimeId);

            return (
              <tr key={runtime.runtimeId}>
                <td><code>{runtime.runtimeId}</code></td>
                <td>{runtime.provider}</td>
                <td className="muted">{runtime.baseUrl}</td>
                {/* The name of an environment variable. The gateway never serves the value, and this
                    column exists so an operator can see which variable to check. */}
                <td data-testid={`credential-${runtime.runtimeId}`}>
                  {runtime.apiKeyEnvironmentVariable ?? 'none'}
                </td>
                <td>
                  <span className={`pill pill-${state?.status ?? 'unknown'}`}>
                    {(state?.status ?? 'unknown').replace(/_/g, ' ')}
                  </span>
                </td>
                <td className="muted">
                  {state?.checkedAt === undefined ? 'never consulted' : timestamp(state.checkedAt)}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <h2>Models and aliases</h2>
      {models.isPending && <Loading />}
      {models.error && <Problem error={models.error} />}
      {models.data && (
        <table className="grid" data-testid="models">
          <thead>
            <tr>
              <th scope="col">Client model</th>
              <th scope="col">Upstream</th>
              <th scope="col">Runtime</th>
              <th scope="col">Source</th>
              <th scope="col">Reachable</th>
              <th scope="col">Capabilities</th>
            </tr>
          </thead>
          <tbody>
            {models.data.map((model) => (
              <tr key={`${model.runtimeId}/${model.clientModelId}`}>
                <td><code>{model.clientModelId}</code></td>
                <td><code>{model.upstreamModelId}</code></td>
                <td>{model.runtimeId}</td>
                <td>{model.source.replace(/_/g, ' ')}</td>
                {/* Null when discovery has never run for this runtime: not unreachable, and not
                    known to be reachable either. */}
                <td data-testid={`reachable-${model.clientModelId}`}>
                  {model.reachable === null ? UNKNOWN : model.reachable ? 'yes' : 'no'}
                </td>
                <td className="muted">{model.capabilityProvenance}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
