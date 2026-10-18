// `vitest/config` rather than `vite`: it is what types the `test` block below. A triple-slash
// reference alongside it was redundant and is what the lint rule was objecting to.
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// The gateway serves this build on its own origin at /dashboard/ (FR-DASH-007, ADR 0015), so the
// client calls /api/v1 by relative path and there is no CORS and no URL to configure. Two values below
// are a contract with the gateway, and a test there fails if either drifts:
//
// - `base` is written into every asset URL in the built index.html and is the client router's
//   basename (import.meta.env.BASE_URL), so it must be the path the gateway mounts the build at;
// - `assetsDir` is where the content-hashed files go, and the only directory the gateway caches as
//   immutable.
//
// The dev server uses the same base, so a deep link means the same thing on 5281 as on the gateway,
// and its proxy reproduces the single origin: the page and /api answer from one address.
export default defineConfig({
  plugins: [react()],
  base: '/dashboard/',
  build: {
    assetsDir: 'assets',
  },
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
    // Vitest replaces CSS with an empty string unless told otherwise. The stylesheet's pill colours
    // are a contract a test reads (tests/styles.test.ts), so it is the one file kept.
    css: { include: [/styles\.css/] },
  },
});
