import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': resolve(__dirname, './src'),
    },
  },
  build: {
    lib: {
      entry: resolve(__dirname, 'src/components/Graph/index.ts'),
      name: 'NVLGraph',
      formats: ['es', 'umd'],
      fileName: (format) => `nvl-graph.${format}.js`,
    },
    rollupOptions: {
      external: ['react', 'react-dom', '@xyflow/react'],
      output: {
        globals: {
          react: 'React',
          'react-dom': 'ReactDOM',
          '@xyflow/react': 'ReactFlow',
        },
      },
    },
  },
  server: {
    port: 3000,
    open: true,
  },
});
