import { render, screen, within } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Root } from '../src/Root';
import type { CatalogModel, ExchangePage, RuntimeHealth, RuntimeSummary, SystemInfo } from '../src/api/types';

// Each screen rendered through the real root with the gateway's answers stubbed, so a screen that
// stopped showing what the API returned fails here; before, the only render test left every request
// pending and checked navigation links alone.
describe('the screens', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, '', '/');
  });

  const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const answering = (routes: Record<string, unknown>) =>
    vi.stubGlobal('fetch', (path: string) => {
      const route = Object.keys(routes)
        .sort((left, right) => right.length - left.length)
        .find((candidate) => path.startsWith(candidate));

      return Promise.resolve(
        route === undefined
          ? new Response(null, { status: 404 })
          : new Response(JSON.stringify(routes[route]), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            }),
      );
    });

  const system = (persistenceFailures?: SystemInfo['persistenceFailures']): SystemInfo => ({
    version: '0.2.0-alpha+test',
    stage: '1D',
    enabledModules: ['openai-chat-completions', 'lmstudio'],
    contentRetentionEnabled: false,
    metadataRetentionEnabled: true,
    telemetryExportEnabled: false,
    ...(persistenceFailures === undefined ? {} : { persistenceFailures }),
  });

  const health: RuntimeHealth[] = [
    { runtimeId: 'lmstudio-local', status: 'healthy', checkedAt: '2026-10-17T01:22:19Z', servedFromStaleCache: false },
  ];

  const page: ExchangePage = { items: [], nextCursor: null };

  it('shows what the system endpoint reports on the overview', async () => {
    answering({ '/api/v1/system': system({ queueSaturated: 0, writeFailed: 0 }), '/api/v1/health/runtimes': health, '/api/v1/exchanges': page });
    window.history.replaceState(null, '', '/dashboard/');

    render(<Root client={client()} />);

    expect(await screen.findByTestId('metadata-retention')).toHaveTextContent('retained');
    expect(screen.getByTestId('telemetry-export')).toHaveTextContent('not exported');
    expect(await screen.findByTestId('health-lmstudio-local')).toHaveTextContent('healthy');

    // Nothing was lost, so nothing is said about it.
    expect(screen.queryByTestId('persistence-failures')).toBeNull();
  });

  it('says on the overview when evidence was lost, with export off', async () => {
    // The metric counting the loss reaches no backend in a default deployment, so this is where an
    // operator learns that the store has been refusing writes (ADR 0027).
    answering({ '/api/v1/system': system({ queueSaturated: 2, writeFailed: 5 }), '/api/v1/health/runtimes': health, '/api/v1/exchanges': page });
    window.history.replaceState(null, '', '/dashboard/');

    render(<Root client={client()} />);

    const notice = await screen.findByTestId('persistence-failures');

    expect(notice).toHaveTextContent('5 exchange(s) were lost because the store rejected them');
    expect(notice).toHaveTextContent('2 because the metadata queue was full');
  });

  it('lists the configured runtimes and their models', async () => {
    const runtimes: RuntimeSummary[] = [
      {
        runtimeId: 'lmstudio-local',
        provider: 'lmstudio',
        baseUrl: 'http://127.0.0.1:1234/v1',
        apiKeyEnvironmentVariable: 'LM_STUDIO_API_KEY',
        enabled: true,
        discoveryEnabled: true,
      },
    ];
    const models: CatalogModel[] = [
      { clientModelId: 'qwen3.6-35b-a3b', runtimeId: 'lmstudio-local', upstreamModelId: 'qwen3.6-35b-a3b', source: 'discovered', reachable: true, capabilityProvenance: 'unknown' },
    ];

    answering({ '/api/v1/runtimes': runtimes, '/api/v1/health/runtimes': health, '/api/v1/models': models });
    window.history.replaceState(null, '', '/dashboard/runtimes');

    render(<Root client={client()} />);

    const table = await screen.findByTestId('runtimes');

    expect(within(table).getByText('lmstudio-local')).toBeInTheDocument();
    expect((await screen.findAllByText('qwen3.6-35b-a3b', { exact: false })).length).toBeGreaterThan(0);
  });
});
