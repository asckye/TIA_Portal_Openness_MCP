using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi;

// The fixture has no Siemens event objects; keep the content-token boundary managed.
internal static class HmiExactAccess
{
    internal static string Token(string scope, Dictionary<string, object?> content)
        => HmiContentTokenPolicy.Compute("runtime-settings-fixture", scope, content, HmiValues.Serialize);

    internal static void RequireToken(string? expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new PlcSoftwareException("InvalidState", "Content changed or previewToken missing; read/preview again.");
    }
}
