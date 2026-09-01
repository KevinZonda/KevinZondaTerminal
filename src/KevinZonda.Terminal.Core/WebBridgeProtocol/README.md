# WebBridgeProtocol

Cross-platform protocol primitives shared by the WinForms WebView2 host, the
Avalonia NativeWebView host, and the browser Server transport.

This Core component owns the bridge envelope, protocol version, message names,
payload readers, validation, and trim-safe envelope serialization. It remains
free of UI-framework, WebView, WebSocket, terminal-session, and
application-settings dependencies; those operations belong to each host adapter
or runtime.
