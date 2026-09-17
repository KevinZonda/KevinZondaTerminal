using System.Runtime.InteropServices;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal static class KimiCodeRequestHeaders
{
    internal static void AddCliIdentity(HttpRequestMessage request, KimiCodeUsageOptions options)
        => AddIdentity(request, KimiCodeCredentialStore.ResolveDeviceId(options), Environment.MachineName);

    internal static void AddIdentity(HttpRequestMessage request, string deviceId, string deviceName = "KevinZonda Terminal")
    {
        request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_code_cli");
        request.Headers.TryAddWithoutValidation("X-Msh-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Name", deviceName);
        request.Headers.TryAddWithoutValidation("X-Msh-Os-Version", Environment.OSVersion.Version.ToString());
        request.Headers.TryAddWithoutValidation(
            "X-Msh-Device-Model",
            $"{Environment.OSVersion.Platform} {RuntimeInformation.OSArchitecture}");
    }
}
