import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: 'dist',
  },
  server: {
    port: 5555,
    proxy: {
      '/api': 'http://localhost:5226',
      '/swagger': 'http://localhost:5226',
      '/hubs': {
        target: 'http://localhost:5226',
        ws: true,
      },
    },
  },
});
