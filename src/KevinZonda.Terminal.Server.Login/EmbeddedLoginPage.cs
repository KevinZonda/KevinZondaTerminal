using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace KevinZonda.Terminal.Server.Login;

public static class EmbeddedLoginPage
{
    private const string ResourceName = "KevinZonda.Terminal.Server.Login/LoginPage.html";
    private static readonly string Template = LoadTemplate();
    private static readonly Regex PlaceholderPattern = new(
        "\\{\\{(?:CSRF_TOKEN|RETURN_URL|USERNAME|ERROR|ERROR_HIDDEN|ICP_REGISTRATION)\\}\\}",
        RegexOptions.CultureInvariant);

    public static string Render(
        string csrfToken,
        string returnUrl,
        string? userName = null,
        string? error = null,
        string? icpRegistration = null)
    {
        return PlaceholderPattern.Replace(Template, match => match.Value switch
        {
            "{{CSRF_TOKEN}}" => WebUtility.HtmlEncode(csrfToken),
            "{{RETURN_URL}}" => WebUtility.HtmlEncode(returnUrl),
            "{{USERNAME}}" => WebUtility.HtmlEncode(userName ?? string.Empty),
            "{{ERROR}}" => WebUtility.HtmlEncode(error ?? string.Empty),
            "{{ERROR_HIDDEN}}" => error is null ? " hidden" : string.Empty,
            "{{ICP_REGISTRATION}}" => RenderIcpRegistration(icpRegistration),
            _ => throw new InvalidOperationException("The login page contains an unknown placeholder.")
        });
    }

    private static string RenderIcpRegistration(string? registration)
    {
        if (string.IsNullOrEmpty(registration))
        {
            return string.Empty;
        }

        return "<footer class=\"registration\"><a href=\"https://beian.miit.gov.cn/\" " +
            "target=\"_blank\" rel=\"noopener noreferrer\">" +
            WebUtility.HtmlEncode(registration) +
            "</a></footer>";
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(EmbeddedLoginPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded KTerm login page is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
