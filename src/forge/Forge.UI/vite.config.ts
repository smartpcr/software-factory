/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    host: 'localhost',
    port: 5181,
    strictPort: true,
    allowedHosts: ['localhost', '127.0.0.1', '[::1]'],
    proxy: {
      '/api': {
        target: 'http://localhost:5180',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5180',
        changeOrigin: true,
        ws: true,
      },
    },
  },
  test: {
    // Phase 18.6 / 23.8 — vitest covers `src/**/*.spec.ts` (pure-logic UI
    // tests, no DOM). Playwright e2e specs in `e2e/` use the Playwright
    // test runner and must NOT be discovered here.
    exclude: ['**/node_modules/**', '**/e2e/**', '**/dist/**'],
  },
});
