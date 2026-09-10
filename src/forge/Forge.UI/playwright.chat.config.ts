import { defineConfig, devices } from '@playwright/test';

// Isolated component exercise: all API requests are intercepted. Never contacts the running Forge host.
export default defineConfig({
  testDir: './e2e/chat',
  workers: 1,
  retries: 0,
  reporter: 'list',
  outputDir: '../../../.tmp-build/chat-stage1s/browser-results',
  use: { baseURL: 'http://127.0.0.1:5389', ...devices['Desktop Chrome'], channel: process.env.FORGE_CHAT_BROWSER_CHANNEL || undefined, screenshot: 'only-on-failure' },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 5389',
    url: 'http://127.0.0.1:5389/e2e/chat/fixture.html',
    reuseExistingServer: false,
    timeout: 60_000,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
