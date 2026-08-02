import { GatewayError } from '../api/client';

export function Loading() {
  return <p className="loading" role="status">Loading…</p>;
}

/**
 * Reports a failure in the gateway's own words, and separates the ones an operator can act on.
 *
 * `agentsplice_persistence_disabled` is the case worth singling out: it is not a fault, it is a
 * deployment that retains nothing, and showing it as an error beside a stack of red would send
 * someone looking for a broken database that was never configured.
 */
export function Problem({ error }: { error: unknown }) {
  if (error instanceof GatewayError && error.code === 'agentsplice_persistence_disabled') {
    return (
      <div className="notice" data-testid="persistence-disabled">
        <strong>This deployment retains no exchange metadata.</strong>
        <p>
          Set <code>agentsplice:persistence:mode</code> to <code>Sqlite</code> to keep exchanges. Until
          then the gateway proxies normally and stores nothing, so there is nothing to list.
        </p>
      </div>
    );
  }

  if (error instanceof GatewayError && error.status === 401) {
    return (
      <div className="notice" data-testid="unauthorized">
        <strong>The administrative API requires a bearer token.</strong>
        <p>Enter it above. It is held in memory only and is gone when this tab closes.</p>
      </div>
    );
  }

  const message = error instanceof Error ? error.message : 'The gateway could not be reached.';

  return (
    <div className="notice notice-error" role="alert">
      <strong>{message}</strong>
      {error instanceof GatewayError && error.code !== undefined && <p><code>{error.code}</code></p>}
    </div>
  );
}
