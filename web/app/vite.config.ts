import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiTarget = 'http://localhost:47913';
const processEnvironment = globalThis as typeof globalThis & {
  process?: { env: Record<string, string | undefined> };
};

export default defineConfig(({ command }) => {
  // React selects its development entry point from NODE_ENV, not Vite's production mode.
  // Set it before creating the plugin so inherited host values cannot leak into a build.
  if (command === 'build' && processEnvironment.process) processEnvironment.process.env.NODE_ENV = 'production';
  return {
    plugins: [react()],
    server: {
      port: 47914,
      proxy: {
        '/v1': apiTarget,
        '/ws': { target: apiTarget, ws: true },
        '/decks': apiTarget,
        '/health': apiTarget,
      },
    },
  };
});
