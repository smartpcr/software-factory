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
  },
  plugins: [
    (monacoEditorPlugin as unknown as { default: typeof monacoEditorPlugin }).default({
      languageWorkers: ['editorWorkerService'],
    }),
  ],
});
