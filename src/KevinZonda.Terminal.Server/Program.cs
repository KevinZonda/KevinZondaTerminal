using System.Net.WebSockets;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Server;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Web;

if (ConsoleThemeHelper.TryRun(args, out var helperExitCode))
{
    Environment.ExitCode = helperExitCode;
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:7132");
}

var startingDirectory = builder.Configuration["working-directory"];
if (string.IsNullOrWhiteSpace(startingDirectory) || !Directory.Exists(startingDirectory))
{
    startingDirectory = Environment.CurrentDirectory;
}
startingDirectory = Path.GetFullPath(startingDirectory);

builder.Services.AddSingleton(new SettingsStore());
builder.Services.AddSingleton(new ServerOptions(startingDirectory));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("A WebSocket connection is required.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var runtime = new BrowserTerminalConnection(
        socket,
        context.RequestServices.GetRequiredService<SettingsStore>(),
        context.RequestServices.GetRequiredService<ServerOptions>());
    var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
    using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        lifetime.ApplicationStopping);
    await runtime.RunAsync(connectionLifetime.Token);
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/{**path}", async context =>
{
    var path = context.Request.RouteValues["path"] as string ?? string.Empty;
    if (!EmbeddedWebAssets.TryOpen(path, out var content, out var contentType) || content is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await using (content)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = EmbeddedWebAssets.IsImmutable(path)
            ? "public,max-age=31536000,immutable"
            : "no-cache";
        await content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
});

app.Logger.LogInformation("Shell sessions will start in {StartingDirectory}", startingDirectory);
await app.RunAsync();

namespace KevinZonda.Terminal.Server
{
    internal sealed record ServerOptions(string StartingDirectory);
}
