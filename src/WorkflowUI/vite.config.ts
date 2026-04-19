import { defineConfig } from 'vite';

export default defineConfig({
  base: '/',
  server: {
    port: 5555,
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
});
