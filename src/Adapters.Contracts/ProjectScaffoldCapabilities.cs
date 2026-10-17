using System;

namespace TiaMcp.Adapters.Contracts;

// Each primitive borrows its API range from the family operation it executes.
// Registration requires only one supported action; composition checks every step.
public static class ProjectScaffoldCapabilities
{
    public static bool Supports(string release, string kind)
    {
        string tool = kind switch {
            "lad" => "ImportPlcBlockDocuments",
            "hmiInfo" => "GetHmiProgramInfo",
            "hmiConnect" => "EnsureUnifiedHmiConnection",
            "hmiScreen" => "EnsureUnifiedHmiScreen",
            "hmiDesign" => "ApplyUnifiedHmiScreenDesign",
            "hmiTag" => "EnsureUnifiedHmiTag",
            "connect" or "createProject" or "save" or "addDevice" or "sclImport" or "sclGenerate" or "compile"
                => "BuildProjectScaffold",
            _ => throw new ArgumentException("Unknown scaffold primitive.", nameof(kind))
        };
        return PortedFamilies.Available(release, tool);
    }

    public static string Refusal(string release, string kind)
        => $"UNSUPPORTED_CAPABILITY: scaffold step '{kind}' has no public API in release '{release}'.";

    public static void Require(string release, string kind)
    {
        if (!Supports(release, kind)) throw new NotSupportedException(Refusal(release, kind));
    }
}
