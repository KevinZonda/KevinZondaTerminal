import { defineConfig } from 'vite';

export default defineConfig({
  base: '/dashboard/',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
    target: 'chrome120'
  }
});
