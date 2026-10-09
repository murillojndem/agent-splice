import { render, screen, within } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Root } from '../src/Root';

// The gateway serves this build at /dashboard/ (ADR 0015). The router's basename comes from Vite's
// `base`, which Vitest applies too, so these render the real root and check the URLs it produces and
// recognises, rather than a router configured by the test.
describe('the dashboard beneath the gateway mount', () => {
  beforeEach(() => {
    // Nothing answers, so every page stays in its loading state — all a routing test needs.
    vi.stubGlobal('fetch', () => new Promise<Response>(() => {}));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, '', '/');
  });

  const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const navigation = () => within(screen.getByRole('navigation'));

  it('opens a deep link beneath the mount on its own screen', () => {
    // A reload of a page the router navigated to: the gateway answers with the shell, and the router
    // must recognise the path beneath the mount.
    window.history.replaceState(null, '', '/dashboard/runtimes');

    render(<Root client={client()} />);

    expect(navigation().getByRole('link', { name: 'Runtimes' })).toHaveAttribute('aria-current', 'page');
    expect(navigation().getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current');
  });

  it('links every screen beneath the mount, the overview with its trailing slash', () => {
    // /dashboard/ is where the gateway serves the shell; /dashboard is only redirected there.
    window.history.replaceState(null, '', '/dashboard/');

    render(<Root client={client()} />);

    expect(navigation().getByRole('link', { name: 'Overview' })).toHaveAttribute('href', '/dashboard/');
    expect(navigation().getByRole('link', { name: 'Exchanges' })).toHaveAttribute('href', '/dashboard/exchanges');
    expect(navigation().getByRole('link', { name: 'Runtimes' })).toHaveAttribute('href', '/dashboard/runtimes');
  });
});
