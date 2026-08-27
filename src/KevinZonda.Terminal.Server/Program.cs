using System.Net.WebSockets;
using KevinZonda.Terminal.Server.UserAuth;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Server;
using KevinZonda.Terminal.Server.Dashboard;
using KevinZonda.Terminal.Server.Login;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;

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
var icpRegistration = NormalizeIcpRegistration(builder.Configuration["icp-registration"]);

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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
ServerAuthentication.AddServices(builder.Services, serverAuthentication);

var app = builder.Build();
app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
if (serverAuthentication.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/auth/login", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        var returnUrl = ServerAuthentication.SafeReturnUrl(
            context.Request.Query["returnUrl"].FirstOrDefault());
        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Response.Redirect(returnUrl);
            return;
        }

        await WriteLoginPageAsync(
            context,
            antiforgery,
            returnUrl,
            icpRegistration: icpRegistration).ConfigureAwait(false);
    }).AllowAnonymous();

    app.MapPost(
        "/auth/login",
        async (
            HttpContext context,
            IAntiforgery antiforgery,
            ServerPasswordVerifier passwordVerifier) =>
        {
            var returnUrl = "/";
            try
            {
                if (!context.Request.HasFormContentType)
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    return;
                }

                await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
                var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                returnUrl = ServerAuthentication.SafeReturnUrl(form["returnUrl"].FirstOrDefault());
                var userName = form["username"].FirstOrDefault()?.Trim() ?? string.Empty;
                var password = form["password"].FirstOrDefault() ?? string.Empty;
                var passwordMatches = password.Length is > 0 and <= 4096 &&
                    await passwordVerifier.VerifyAsync(password, context.RequestAborted).ConfigureAwait(false);
                var userNameMatches = string.Equals(
                    userName,
                    serverAuthentication.UserName,
                    StringComparison.Ordinal);
                if (!userNameMatches || !passwordMatches)
                {
                    await WriteLoginPageAsync(
                        context,
                        antiforgery,
                        returnUrl,
                        userName,
                        "The username or password is incorrect.",
                        icpRegistration,
                        StatusCodes.Status401Unauthorized).ConfigureAwait(false);
                    return;
                }

                await context.SignInAsync(
                    ServerAuthentication.CookieScheme,
                    ServerAuthentication.CreatePrincipal(serverAuthentication),
                    new AuthenticationProperties
                    {
                        AllowRefresh = true,
                        IsPersistent = false
                    }).ConfigureAwait(false);
                context.Response.Redirect(returnUrl);
            }
            catch (AntiforgeryValidationException)
            {
                await WriteLoginPageAsync(
                    context,
                    antiforgery,
                    returnUrl,
                    error: "The login form expired. Please try again.",
                    icpRegistration: icpRegistration,
                    statusCode: StatusCodes.Status400BadRequest).ConfigureAwait(false);
            }
        }).AllowAnonymous();

    app.MapPost(
        "/auth/logout",
        async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await IsValidDashboardRequestAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "Invalid dashboard CSRF token." });
            }

            await context.SignOutAsync(ServerAuthentication.CookieScheme).ConfigureAwait(false);
            app.Logger.LogInformation(
                "Dashboard user logged out from {RemoteAddress}",
                context.Connection.RemoteIpAddress);
            return Results.NoContent();
        }).RequireAuthorization();
}

app.MapGet("/auth/logged-out", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(
        """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="dark">
            <title>Logged out - KTerm Server</title>
            <style>
              :root { color: #e8edf5; background: #080b11; font-family: Inter, ui-sans-serif, system-ui, sans-serif; }
              * { box-sizing: border-box; }
              body { display: grid; min-width: 320px; min-height: 100vh; margin: 0; place-items: center; background: radial-gradient(circle at 50% 0, rgba(70,110,255,.16), transparent 34rem), #080b11; }
              main { width: min(440px, calc(100% - 32px)); padding: 34px; border: 1px solid #1e2530; border-radius: 14px; background: rgba(15,19,28,.9); box-shadow: 0 18px 50px rgba(0,0,0,.24); }
              p { margin: 10px 0 24px; color: #8e98a8; line-height: 1.55; }
              h1 { margin: 0; font-size: 28px; }
              a { display: inline-flex; min-height: 38px; align-items: center; padding: 0 14px; border: 1px solid #2e3a50; border-radius: 9px; color: #e8edf5; background: #27334a; text-decoration: none; }
              a:hover { border-color: #627ebd; }
            </style>
          </head>
          <body>
            <main>
              <h1>Logged out</h1>
              <p>Your KTerm authentication cookie has been removed.</p>
              <a href="/auth/login?returnUrl=%2Fdashboard">Sign in again</a>
            </main>
          </body>
        </html>
        """,
        "text/html; charset=utf-8");
}).AllowAnonymous();

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
        "Password authentication is enabled using {AuthenticationFile} for user {UserName}",
        serverAuthentication.ConfigurationPath,
        serverAuthentication.UserName);
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

static async Task WriteLoginPageAsync(
    HttpContext context,
    IAntiforgery antiforgery,
    string returnUrl,
    string? userName = null,
    string? error = null,
    string? icpRegistration = null,
    int statusCode = StatusCodes.Status200OK)
{
    var csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken
        ?? throw new InvalidOperationException("Unable to issue a login CSRF token.");
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; " +
        "base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await context.Response.WriteAsync(
        EmbeddedLoginPage.Render(csrfToken, returnUrl, userName, error, icpRegistration),
        context.RequestAborted).ConfigureAwait(false);
}

static string? NormalizeIcpRegistration(string? value)
{
    var registration = value?.Trim();
    if (string.IsNullOrEmpty(registration))
    {
        return null;
    }
    if (registration.Length > 128)
    {
        throw new InvalidOperationException(
            "The ICP registration number must be 128 characters or fewer.");
    }
    if (registration.Any(char.IsControl))
    {
        throw new InvalidOperationException(
            "The ICP registration number cannot contain control characters.");
    }
    return registration;
}

namespace KevinZonda.Terminal.Server
{
    internal sealed record ServerOptions(string StartingDirectory, TimeSpan RuntimeRetention);
}
