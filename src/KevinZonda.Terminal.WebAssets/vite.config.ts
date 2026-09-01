import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

const diagnosticsChannelShim = fileURLToPath(
  new URL('./src/diagnostics-channel-shim.ts', import.meta.url)
);

export default defineConfig({
  base: './',
  resolve: {
    // addon-ligatures' beta browser bundle currently retains lru-cache's
    // optional Node diagnostics import. Browser builds do not publish those
    // diagnostics, so replace it with the equivalent no-subscriber channel.
    alias: {
      'node:diagnostics_channel': diagnosticsChannelShim
    }
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
    target: 'chrome120'
  }
});
