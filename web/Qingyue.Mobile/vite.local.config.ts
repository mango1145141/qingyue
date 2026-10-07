// Local visual/build preview only. Production uses vite.config.ts and the real SDK.
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
export default defineConfig({
  plugins: [react()],
  base: './',
  resolve: { alias: { '@appdeploy/client': fileURLToPath(new URL('./tools/localSdk.ts', import.meta.url)) } },
  build: { outDir: 'dist-local' },
});
