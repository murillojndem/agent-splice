import { render, screen, within } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Root } from '../src/Root';
import type { ExchangeDetail, ExchangePage } from '../src/api/types';

// `completed` says only that the transport cycle finished, so the runtime's own outcome is shown beside
// it, and the response summary — the part of the record that distinguishes an error inside a 200 — is
// shown on the exchange's page (ADR 0026).
describe('what the runtime said', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, '', '/');
  });

  const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const answering = (routes: Record<string, unknown>) =>
    vi.stubGlobal('fetch', (path: string) => {
      const route = Object.keys(routes).find((candidate) => path.startsWith(candidate));

      return Promise.resolve(
        route === undefined
          ? new Response(null, { status: 404 })
          : new Response(JSON.stringify(routes[route]), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            }),
      );
    });

  const summary = (id: string, status: 'completed' | 'failed', outcome?: 'success' | 'error_status' | 'error_reported') => ({
    exchangeId: id,
    requestId: id,
    startedAt: '2026-10-17T01:22:19Z',
    completedAt: '2026-10-17T01:22:20Z',
    status,
    ...(outcome === undefined ? {} : { upstreamOutcome: outcome }),
    clientModelId: 'local-coder',
    runtimeId: 'lmstudio-local',
    streaming: true,
    contentRetentionState: 'metadata_only' as const,
  });

  it('lists the runtime outcome beside the status, and nothing for an exchange that did not complete', async () => {
    const page: ExchangePage = {
      items: [
        summary('11111111-1111-1111-1111-111111111111', 'completed', 'success'),
        summary('22222222-2222-2222-2222-222222222222', 'completed', 'error_reported'),
        summary('33333333-3333-3333-3333-333333333333', 'failed'),
        summary('55555555-5555-5555-5555-555555555555', 'completed', 'error_status'),
      ],
      nextCursor: null,
    };

    answering({ '/api/v1/exchanges': page });
    window.history.replaceState(null, '', '/dashboard/exchanges');

    render(<Root client={client()} />);

    const succeeded = await screen.findByTestId('outcome-11111111-1111-1111-1111-111111111111');
    const reported = screen.getByTestId('outcome-22222222-2222-2222-2222-222222222222');
    const failed = screen.getByTestId('outcome-33333333-3333-3333-3333-333333333333');

    expect(within(succeeded).getByText('success')).toHaveClass('pill-success');
    expect(within(reported).getByText('error reported')).toHaveClass('pill-error_reported');
    expect(within(failed).queryByTestId('outcome')).toBeNull();
    expect(
      within(screen.getByTestId('outcome-55555555-5555-5555-5555-555555555555')).getByText('error status'),
    ).toHaveClass('pill-error_status');
  });

  it('says why a failed exchange has no outcome, and whether its failure reached the client', async () => {
    // The response summary can say the runtime reported an error while the exchange then failed, on
    // its idle budget say; a bare dash under "Runtime said" read as a contradiction of it.
    const id = '66666666-6666-6666-6666-666666666666';
    const detail: ExchangeDetail = {
      ...summary(id, 'failed'),
      ingressProtocol: 'open_ai_chat_completions',
      streamTermination: 'timeout',
      upstreamStatusCode: 200,
      failureClass: 'upstream_timeout',
      measurements: [],
      responseSummary: { choiceCount: 0, finishReasons: [], upstreamErrorReported: true },
    };

    answering({ [`/api/v1/exchanges/${id}/timeline`]: [], [`/api/v1/exchanges/${id}`]: detail });
    window.history.replaceState(null, '', `/dashboard/exchanges/${id}`);

    render(<Root client={client()} />);

    expect(await screen.findByTestId('runtime-outcome')).toHaveTextContent('not judged: the exchange failed');

    // A failure class with no code is a response abandoned or never started: no envelope reached the
    // client, and showing the class where a code goes made the two look alike.
    expect(screen.getByTestId('failure')).toHaveTextContent('upstream timeout — no error envelope reached the client');
  });

  it('says an error code was sent when one was, and that no answer was heard when none was', async () => {
    const id = '77777777-7777-7777-7777-777777777777';
    const detail: ExchangeDetail = {
      ...summary(id, 'failed'),
      ingressProtocol: 'open_ai_chat_completions',
      streamTermination: 'not_applicable',
      failureClass: 'runtime_unavailable',
      errorCode: 'agentsplice_runtime_unavailable',
      measurements: [],
    };

    answering({ [`/api/v1/exchanges/${id}/timeline`]: [], [`/api/v1/exchanges/${id}`]: detail });
    window.history.replaceState(null, '', `/dashboard/exchanges/${id}`);

    render(<Root client={client()} />);

    expect(await screen.findByTestId('failure')).toHaveTextContent('agentsplice_runtime_unavailable sent to the client');
    expect(screen.getByText('No answer was heard from the runtime, so there is no response summary.')).toBeInTheDocument();
  });

  it('shows the response summary that says the runtime reported an error', async () => {
    const id = '44444444-4444-4444-4444-444444444444';
    const detail: ExchangeDetail = {
      ...summary(id, 'completed', 'error_reported'),
      ingressProtocol: 'open_ai_chat_completions',
      streamTermination: 'upstream_error',
      upstreamStatusCode: 200,
      measurements: [],
      responseSummary: { choiceCount: 0, finishReasons: [], upstreamErrorReported: true },
    };

    answering({ [`/api/v1/exchanges/${id}/timeline`]: [], [`/api/v1/exchanges/${id}`]: detail });
    window.history.replaceState(null, '', `/dashboard/exchanges/${id}`);

    render(<Root client={client()} />);

    const response = await screen.findByTestId('response-summary');

    expect(within(response).getByText('upstream error reported')).toBeInTheDocument();
    expect(within(response).getByText('true')).toBeInTheDocument();
    expect(within(screen.getByTestId('runtime-outcome')).getByText('error reported')).toHaveClass('pill-error_reported');
  });
});
