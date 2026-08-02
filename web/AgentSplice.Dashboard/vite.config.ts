// `vitest/config` rather than `vite`: it is what types the `test` block below. A triple-slash
// reference alongside it was redundant and is what the lint rule was objecting to.
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// The dashboard is served separately from the gateway and talks to it over the documented HTTP API
// (FR-DASH-002). The dev proxy exists so a developer does not have to disable CORS to look at their
// own traces; nothing about it is required in production, where the two are configured by URL.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5281,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5280', changeOrigin: true },
      '/health': { target: 'http://127.0.0.1:5280', changeOrigin: true },
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.test.ts', 'tests/**/*.test.tsx'],
  },
});
