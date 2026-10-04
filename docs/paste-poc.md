# Large paste PoC

This PoC separates large-paste behavior into two layers.

## Browser/xterm layer

Run the automated headless matrix:

```bash
node scripts/paste-cdp-poc.mjs
```

The script starts a temporary Vite server and headless Chrome. It compares:

- `terminal.paste()` (the desktop Ctrl+Shift+V path after clipboard text is read)
- a browser paste event containing only `text/plain`
- a browser paste event containing `text/plain`, `text/html`, and `text/rtf`
- one large bridge message versus a yielded 16 KiB bridge simulation

For interactive use, start `pnpm dev` in
`src/KevinZonda.Terminal.WebAssets` and open `/paste-poc.html`.

## Real Unix PTY + nano layer

Run:

```bash
dotnet run --project tests/KevinZonda.Terminal.PastePoc
```

Each scenario starts a fresh `/usr/bin/nano`, continuously drains PTY output,
pastes into an unsaved temporary buffer, and then checks whether nano responds
to Ctrl+X. The single-frame scenarios verify the normal desktop bridge path.
The throttled 512-byte scenarios remain as a slower baseline for comparing
input pacing with the native helper's duplex output draining.

On macOS, `/usr/bin/nano` is currently a symlink to UW Pico and does not enable
bracketed paste mode. The PoC detects and reports bracketed paste mode rather
than assuming it is enabled.
