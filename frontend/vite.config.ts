import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In dev, proxy API calls to the backend so the app can use same-origin relative URLs,
// exactly like the nginx proxy does in Docker.
const target = process.env.VITE_PROXY_TARGET ?? 'http://localhost:8083';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target, changeOrigin: true },
      '/health': { target, changeOrigin: true },
    },
  },
});
