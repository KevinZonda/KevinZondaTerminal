# KevinZonda.Terminal.WebBridgeProtocol

Cross-platform protocol primitives shared by the WinForms WebView2 host, the
Avalonia NativeWebView host, and the browser Server transport.

This project owns the bridge envelope, protocol version, message names, payload
readers, validation, and trim-safe envelope serialization. It must remain free
of UI-framework, WebView, WebSocket, terminal-session, and application-settings
dependencies; those operations belong to each host adapter or runtime.
