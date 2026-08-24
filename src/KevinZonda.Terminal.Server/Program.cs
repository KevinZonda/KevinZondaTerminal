using System.Net.WebSockets;
using KevinZonda.Terminal.Server.UserAuth;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Server;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Web;
using Microsoft.AspNetCore.Authentication;

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
var serverAuthentication = await ServerAuthentication.LoadAsync(builder.Configuration);

builder.Services.AddSingleton(new SettingsStore());
var runtimeRetentionMinutes = builder.Configuration.GetValue<double?>("runtime-retention-minutes") ?? 30;
var runtimeRetention = TimeSpan.FromMinutes(Math.Clamp(runtimeRetentionMinutes, 0.1, 24 * 60));
builder.Services.AddSingleton(new ServerOptions(startingDirectory, runtimeRetention));
builder.Services.AddSingleton(services => new BrowserTerminalRuntimeRegistry(
    services.GetRequiredService<SettingsStore>(),
    services.GetRequiredService<ServerOptions>()));
ServerAuthentication.AddServices(builder.Services, serverAuthentication);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
if (serverAuthentication.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/auth/login", async context =>
    {
        await context.SignInAsync(
            ServerAuthentication.CookieScheme,
            context.User,
            new AuthenticationProperties
            {
                AllowRefresh = true,
                IsPersistent = false
            });
        context.Response.Redirect(ServerAuthentication.SafeReturnUrl(
            context.Request.Query["returnUrl"].FirstOrDefault()));
    }).RequireAuthorization(policy =>
    {
        policy.AddAuthenticationSchemes(ServerAuthentication.BasicScheme);
        policy.RequireAuthenticatedUser();
    });
}

var webSocketEndpoint = app.Map("/ws", async context =>
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
        context.RequestServices.GetRequiredService<BrowserTerminalRuntimeRegistry>());
    var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
    using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        lifetime.ApplicationStopping);
    await runtime.RunAsync(connectionLifetime.Token);
});
if (serverAuthentication.Enabled)
{
    webSocketEndpoint.RequireAuthorization();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var webAssetsEndpoint = app.MapGet("/{**path}", async context =>
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
if (serverAuthentication.Enabled)
{
    webAssetsEndpoint.RequireAuthorization();
}

app.Logger.LogInformation("Shell sessions will start in {StartingDirectory}", startingDirectory);
app.Logger.LogInformation("Disconnected browser runtimes will be retained for {RuntimeRetention}", runtimeRetention);
if (serverAuthentication.FellBackToNoPassword)
{
    app.Logger.LogWarning("No Pass Hash, fallback to No Pass.");
}
else if (serverAuthentication.Enabled)
{
    app.Logger.LogInformation(
        "Password authentication is enabled using {AuthenticationFile}",
        serverAuthentication.ConfigurationPath);
}
await app.RunAsync();

namespace KevinZonda.Terminal.Server
{
    internal sealed record ServerOptions(string StartingDirectory, TimeSpan RuntimeRetention);
}
