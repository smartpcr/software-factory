import { defineConfig } from 'vite';
import monacoEditorPlugin from 'vite-plugin-monaco-editor';

export default defineConfig({
  base: '/',
  server: {
    host: '0.0.0.0',
    port: 5555,
    allowedHosts: true,
    proxy: {
      '/api': 'http://localhost:5226',
      '/hubs': {
        target: 'http://localhost:5226',
        ws: true,
      },
      '/swagger': 'http://localhost:5226',
    },
  },
  build: {
    outDir: 'dist',
    rollupOptions: {
      onwarn(warning, defaultHandler) {
        // @microsoft/signalr v10 ships /*#__PURE__*/ annotations in positions
        // Rollup can't interpret; the comments are dropped but tree-shaking
        // still works. Silence the noise so it doesn't look like a failure.
        if (
          warning.code === 'INVALID_ANNOTATION' &&
          warning.id?.includes('@microsoft/signalr')
        ) {
          return;
        }
        defaultHandler(warning);
      },
    },
  },
  plugins: [
    (monacoEditorPlugin as unknown as { default: typeof monacoEditorPlugin }).default({
      languageWorkers: ['editorWorkerService'],
    }),
  ],
});
