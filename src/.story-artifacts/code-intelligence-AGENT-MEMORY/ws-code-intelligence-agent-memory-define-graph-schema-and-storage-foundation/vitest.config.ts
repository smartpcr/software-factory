import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';

export default defineConfig({
  resolve: {
    alias: {
      '@agent-memory/schema': resolve(__dirname, 'packages/schema/src/index.ts'),
      '@agent-memory/identity': resolve(__dirname, 'packages/identity/src/index.ts'),
      '@agent-memory/graph-store': resolve(__dirname, 'packages/graph-store/src/index.ts'),
      '@agent-memory/graph-sdk': resolve(__dirname, 'packages/graph-sdk/src/index.ts'),
    },
  },
  test: {
    include: ['packages/*/src/**/*.test.ts', 'packages/*/src/**/__tests__/*.test.ts'],
    testTimeout: 30_000,
    pool: 'forks',
  },
});
