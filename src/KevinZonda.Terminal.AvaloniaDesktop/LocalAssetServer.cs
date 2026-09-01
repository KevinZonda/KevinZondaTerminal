using System.Net;
using System.Net.Sockets;
using System.Text;
using KevinZonda.Terminal.Web;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed class LocalAssetServer : IAsyncDisposable
{
    private const string BridgeBootstrap = """
        <script>
        (() => {
          const listeners = new Set();
          const pending = [];
          const flush = () => {
            if (typeof window.invokeCSharpAction !== 'function') return;
            while (pending.length) window.invokeCSharpAction(pending.shift());
          };
          const timer = window.setInterval(() => {
            flush();
            if (typeof window.invokeCSharpAction === 'function') window.clearInterval(timer);
          }, 10);
          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage(message) {
              pending.push(JSON.stringify(message));
              flush();
            },
            addEventListener(type, listener) {
              if (type === 'message') listeners.add(listener);
            },
            removeEventListener(type, listener) {
              if (type === 'message') listeners.delete(listener);
            }
          };
          window.__ktermReceiveNativeMessage = message => {
            const data = typeof message === 'string' ? JSON.parse(message) : message;
            const event = new MessageEvent('message', { data });
            for (const listener of listeners) listener(event);
          };
        })();
        </script>
        """;

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serveTask;
    private readonly string _pathPrefix;

    private LocalAssetServer(HttpListener listener, Uri startPage, string pathPrefix)
    {
        _listener = listener;
        StartPage = startPage;
        _pathPrefix = pathPrefix;
        _serveTask = ServeAsync();
    }

    internal Uri StartPage { get; }

    internal static Task<LocalAssetServer> StartAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FindAvailablePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                var token = Guid.NewGuid().ToString("N");
                var pathPrefix = $"/{token}/";
                return Task.FromResult(new LocalAssetServer(
                    listener,
                    new Uri($"http://127.0.0.1:{port}{pathPrefix}index.html"),
                    pathPrefix));
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Unable to start the local terminal asset server.");
    }

    private async Task ServeAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync()
                    .WaitAsync(_lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                _lifetime.IsCancellationRequested &&
                exception is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), _lifetime.Token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            if (request.HttpMethod is not ("GET" or "HEAD") ||
                !request.Url!.AbsolutePath.StartsWith(_pathPrefix, StringComparison.Ordinal))
            {
                response.StatusCode = request.HttpMethod is "GET" or "HEAD" ? 404 : 405;
                response.Close();
                return;
            }

            var assetPath = request.Url.AbsolutePath[_pathPrefix.Length..];
            if (!EmbeddedWebAssets.TryOpen(assetPath, out var content, out var contentType) ||
                content is null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            await using (content)
            {
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                if (string.Equals(assetPath, "index.html", StringComparison.Ordinal))
                {
                    bytes = InjectBridge(bytes);
                }

                response.StatusCode = 200;
                response.ContentType = contentType;
                response.ContentLength64 = bytes.Length;
                response.Headers["Cache-Control"] = EmbeddedWebAssets.IsImmutable(assetPath)
                    ? "public, max-age=31536000, immutable"
                    : "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                if (request.HttpMethod == "GET")
                {
                    await response.OutputStream.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
                }
                response.Close();
            }
        }
        catch (Exception exception) when (
            exception is IOException or HttpListenerException or OperationCanceledException)
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
    }

    private static byte[] InjectBridge(byte[] htmlBytes)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        var insertionPoint = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return Encoding.UTF8.GetBytes(insertionPoint >= 0
            ? html.Insert(insertionPoint, BridgeBootstrap)
            : BridgeBootstrap + html);
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Close();
        try
        {
            await _serveTask.ConfigureAwait(false);
        }
        catch (HttpListenerException) when (_lifetime.IsCancellationRequested)
        {
        }
        _lifetime.Dispose();
    }
}
