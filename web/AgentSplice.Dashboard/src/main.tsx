import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient } from '@tanstack/react-query';
import { Root } from './Root';
import './styles.css';

// Evidence does not change once written, so a cached read stays correct; what changes is the set of
// exchanges. A short stale time keeps the list current without turning an open tab into a poller
// against someone's gateway.
const client = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      refetchOnWindowFocus: true,
      retry: 1,
    },
  },
});

const container = document.getElementById('root');

if (container === null) {
  throw new Error('The dashboard root element is missing from index.html.');
}

createRoot(container).render(
  <StrictMode>
    <Root client={client} />
  </StrictMode>,
);
