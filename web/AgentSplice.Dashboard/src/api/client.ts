import type {
  CatalogModel,
  ErrorEnvelope,
  ExchangeDetail,
  ExchangePage,
  RuntimeHealth,
  RuntimeSummary,
  SystemInfo,
  TimelineObservation,
} from './types';

/**
 * What went wrong, in the gateway's own vocabulary.
 *
 * The stable `code` is carried through rather than flattened into a message, because it is what the
 * dashboard branches on — a deployment that retains nothing and one that refused a filter need
 * different screens, and both are 4xx/5xx.
 */
export class GatewayError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | undefined,
    message: string,
    readonly param?: string | null,
  ) {
    super(message);
    this.name = 'GatewayError';
  }
}

/** The administrative token, held in memory only. */
let token: string | null = null;

/**
 * Sets or clears the bearer token.
 *
 * Deliberately not persisted to `localStorage`. A token in web storage survives the tab, is readable
 * by any script that gets injected into this origin, and outlives the operator's intent; keeping it
 * in a module variable means closing the tab ends the session, which is the right default for a
 * credential that reads someone's traces. A deployment that wants a longer session should put the
 * dashboard behind its own authentication rather than have this cache a bearer.
 */
export function setToken(value: string | null): void {
  token = value === null || value.length === 0 ? null : value;
}

export function hasToken(): boolean {
  return token !== null;
}

async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };

  if (token !== null) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  const response = await fetch(path, signal ? { headers, signal } : { headers });

  if (response.status === 204) {
    return undefined as T;
  }

  const body = (await response.json().catch(() => null)) as ErrorEnvelope | T | null;

  if (!response.ok) {
    const envelope = body as ErrorEnvelope | null;

    throw new GatewayError(
      response.status,
      envelope?.error?.code,
      envelope?.error?.message ?? `The gateway answered ${response.status}.`,
      envelope?.error?.param ?? null,
    );
  }

  return body as T;
}

export interface ExchangeQuery {
  cursor?: string | undefined;
  limit?: number | undefined;
  status?: string | undefined;
  runtimeId?: string | undefined;
}

export const api = {
  system: (signal?: AbortSignal) => request<SystemInfo>('/api/v1/system', signal),

  runtimes: (signal?: AbortSignal) => request<RuntimeSummary[]>('/api/v1/runtimes', signal),

  runtimeHealth: (signal?: AbortSignal) => request<RuntimeHealth[]>('/api/v1/health/runtimes', signal),

  models: (signal?: AbortSignal) => request<CatalogModel[]>('/api/v1/models', signal),

  exchanges: (query: ExchangeQuery, signal?: AbortSignal) => {
    const search = new URLSearchParams();

    // Only what the caller actually asked for. Sending `status=` would be a filter the gateway is
    // entitled to reject, for a question nobody asked.
    if (query.cursor !== undefined) search.set('cursor', query.cursor);
    if (query.limit !== undefined) search.set('limit', String(query.limit));
    if (query.status !== undefined && query.status !== '') search.set('status', query.status);
    if (query.runtimeId !== undefined && query.runtimeId !== '') search.set('runtimeId', query.runtimeId);

    const suffix = search.size > 0 ? `?${search.toString()}` : '';

    return request<ExchangePage>(`/api/v1/exchanges${suffix}`, signal);
  },

  exchange: (id: string, signal?: AbortSignal) =>
    request<ExchangeDetail>(`/api/v1/exchanges/${encodeURIComponent(id)}`, signal),

  timeline: (id: string, signal?: AbortSignal) =>
    request<TimelineObservation[]>(`/api/v1/exchanges/${encodeURIComponent(id)}/timeline`, signal),
};
