import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiTarget = 'http://localhost:47913';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 47914,
    proxy: {
      '/api': apiTarget,
      '/ws': { target: apiTarget, ws: true },
      '/decks': apiTarget,
      '/health': apiTarget,
    },
  },
});
