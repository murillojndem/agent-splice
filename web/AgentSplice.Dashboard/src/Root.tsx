import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from './App';

/**
 * The dashboard with its providers, mounted beneath the gateway's `/dashboard/` (FR-DASH-007, ADR 0015).
 *
 * The router's basename is Vite's `base`, trailing slash and all, so the Overview link is
 * `/dashboard/` rather than `/dashboard` — the URL the gateway serves the shell at, which a reload
 * therefore finds. The gateway redirects the bare `/dashboard`, where a basename with a trailing slash
 * matches nothing.
 */
export function Root({ client }: { client: QueryClient }) {
  return (
    <QueryClientProvider client={client}>
      <BrowserRouter basename={import.meta.env.BASE_URL}>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  );
}
