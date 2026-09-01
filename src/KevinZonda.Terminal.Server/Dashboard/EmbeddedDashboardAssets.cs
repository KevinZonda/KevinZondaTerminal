using System.Reflection;

namespace KevinZonda.Terminal.Server.Dashboard;

public static class EmbeddedDashboardAssets
{
    private const string ResourcePrefix = "KevinZonda.Terminal.Server.Dashboard/";

    private static readonly Assembly ResourceAssembly = typeof(EmbeddedDashboardAssets).Assembly;
    private static readonly IReadOnlyDictionary<string, string> ResourceNames = ResourceAssembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(
            name => name[ResourcePrefix.Length..].Replace('\\', '/'),
            StringComparer.Ordinal);

    public static bool TryOpen(string requestPath, out Stream? content, out string contentType)
    {
        var path = requestPath.TrimStart('/');
        if (path.Length == 0)
        {
            path = "index.html";
        }

        if (!ResourceNames.TryGetValue(path, out var resourceName))
        {
            content = null;
            contentType = "text/plain; charset=utf-8";
            return false;
        }

        content = ResourceAssembly.GetManifestResourceStream(resourceName);
        contentType = GetContentType(path);
        return content is not null;
    }

    public static bool IsImmutable(string requestPath) =>
        requestPath.TrimStart('/').StartsWith("assets/", StringComparison.Ordinal);

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };
}
