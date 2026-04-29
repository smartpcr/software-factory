import { defineConfig, devices } from '@playwright/test';

// Phase 18.5 / 18.6 — Playwright config for the UI smoke tests.
//
// The vitest specs in `src/views/shifts.spec.ts` already cover every
// shift / pill / lead-pill contract at the pure-logic layer. This
// Playwright run is the visual smoke that closes the loop: it boots a
// real browser against the dev Vite server (which proxies /api to the
// running Forge.Api on :5180) and asserts the rendered DOM.
//
// We deliberately do NOT spawn Forge.Api ourselves — it's a long-running
// dotnet process the developer keeps running. Tests assume it's up at
// http://localhost:5180; the runner pings /api/me before each spec and
// fails fast with a useful error when it's down. The Vite dev server is
// spawned by Playwright via `webServer` and reused if already running.

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
  globalSetup: './e2e/globalSetup.ts',
  globalTeardown: './e2e/globalTeardown.ts',
  use: {
    baseURL: 'http://localhost:5181',
    trace: 'on-first-retry',
    // The Forge.Api dev mode resolves the caller from the X-Forge-User
    // header. Default identity here is the seeded PO; individual tests
    // override via `browser.newContext({ extraHTTPHeaders: ... })` when
    // they need the non-PO path.
    extraHTTPHeaders: {
      'X-Forge-User': 'e2e-po@forge.local',
    },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:5181',
    reuseExistingServer: true,
    timeout: 60_000,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
