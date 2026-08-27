using System.Net.WebSockets;
using KevinZonda.Terminal.Server.UserAuth;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Server;
using KevinZonda.Terminal.Server.Dashboard;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;

if (args.Length > 0 && string.Equals(args[0], "auth", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await ServerAuthCommand.RunAsync(args[1..]);
    return;
}

if (ConsoleThemeHelper.TryRun(args, out var helperExitCode))
{
    Environment.ExitCode = helperExitCode;
    return;
}

var (launcherPipeName, serverArgs) = LauncherPipeConnection.ExtractArguments(args);
await using var launcherPipe = launcherPipeName is null
    ? null
    : await LauncherPipeConnection.ConnectAsync(launcherPipeName);

var builder = WebApplication.CreateBuilder(serverArgs);
var serverStartedAtUtc = DateTimeOffset.UtcNow;
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
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "kterm.dashboard.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.HeaderName = "X-KTerm-CSRF";
});
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

var dashboardStatusEndpoint = app.MapGet(
    "/api/dashboard/status",
    (HttpContext context, BrowserTerminalRuntimeRegistry registry, IAntiforgery antiforgery) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!serverAuthentication.Enabled)
        {
            return Results.Json(new
            {
                enabled = false,
                reason = "Password authentication is required to enable server management."
            });
        }

        var runtimes = registry.GetDashboardSnapshot();
        var csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken;
        return Results.Json(new DashboardServerSnapshot(
            Enabled: true,
            Version: typeof(BrowserTerminalRuntime).Assembly.GetName().Version?.ToString() ?? "unknown",
            StartedAtUtc: serverStartedAtUtc,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            StartingDirectory: startingDirectory,
            RuntimeRetentionMinutes: runtimeRetention.TotalMinutes,
            RuntimeCount: runtimes.Count,
            ConnectedRuntimeCount: runtimes.Count(runtime => runtime.Connected),
            SessionCount: runtimes.Sum(runtime => runtime.Sessions.Count),
            CsrfToken: csrfToken,
            Runtimes: runtimes));
    });
if (serverAuthentication.Enabled)
{
    dashboardStatusEndpoint.RequireAuthorization();

    app.MapDelete(
        "/api/dashboard/runtimes/{runtimeId}",
        async (HttpContext context, string runtimeId, BrowserTerminalRuntimeRegistry registry, IAntiforgery antiforgery) =>
        {
            if (!await IsValidDashboardRequestAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "Invalid dashboard CSRF token." });
            }

            if (!await registry.CloseRuntimeFromDashboardAsync(runtimeId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            app.Logger.LogWarning(
                "Dashboard closed browser runtime {RuntimeId} from {RemoteAddress}",
                runtimeId,
                context.Connection.RemoteIpAddress);
            return Results.NoContent();
        }).RequireAuthorization();

    app.MapDelete(
        "/api/dashboard/runtimes/{runtimeId}/sessions/{sessionId}",
        async (
            HttpContext context,
            string runtimeId,
            string sessionId,
            BrowserTerminalRuntimeRegistry registry,
            IAntiforgery antiforgery) =>
        {
            if (!await IsValidDashboardRequestAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "Invalid dashboard CSRF token." });
            }

            if (!await registry.CloseSessionFromDashboardAsync(runtimeId, sessionId).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            app.Logger.LogWarning(
                "Dashboard closed session {SessionId} in runtime {RuntimeId} from {RemoteAddress}",
                sessionId,
                runtimeId,
                context.Connection.RemoteIpAddress);
            return Results.NoContent();
        }).RequireAuthorization();
}

var dashboardAssetsEndpoint = app.MapGet("/dashboard/{**path}", async context =>
{
    var path = context.Request.RouteValues["path"] as string ?? string.Empty;
    if (!EmbeddedDashboardAssets.TryOpen(path, out var content, out var contentType) || content is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await using (content)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = EmbeddedDashboardAssets.IsImmutable(path)
            ? "public,max-age=31536000,immutable"
            : "no-cache";
        await content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
});
if (serverAuthentication.Enabled)
{
    dashboardAssetsEndpoint.RequireAuthorization();
}

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
if (launcherPipe is not null)
{
    app.Lifetime.ApplicationStarted.Register(() =>
        launcherPipe.StartControl(app.Lifetime, app.Logger));
}
await app.RunAsync();

static async Task<bool> IsValidDashboardRequestAsync(HttpContext context, IAntiforgery antiforgery)
{
    try
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return true;
    }
    catch (AntiforgeryValidationException)
    {
        return false;
    }
}

namespace KevinZonda.Terminal.Server
{
    internal sealed record ServerOptions(string StartingDirectory, TimeSpan RuntimeRetention);
}
