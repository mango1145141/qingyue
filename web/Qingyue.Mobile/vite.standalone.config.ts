import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
export default defineConfig({
  plugins: [react()],
  base: '/',
  resolve: {
    alias: {
      '@appdeploy/client': fileURLToPath(
        new URL('./tools/standaloneApi.ts', import.meta.url),
      ),
    },
  },
  build: { outDir: 'dist-standalone', sourcemap: false },
});
