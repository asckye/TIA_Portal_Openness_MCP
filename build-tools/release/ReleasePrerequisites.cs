using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class ReleasePrerequisites
{
    internal static string RequireEcosystemPython(string executable, string bridge, string python, SuiteProcess? process = null)
    {
        var code = "import runpy,sys; assert sys.version_info >= (3,12); import reportlab; m=runpy.run_path(sys.argv[1]); group,errors=m['load_groups'](); assert not errors, errors; assert len(m['catalog'](group)) >= 49";
        var result = (process ?? DotnetSuites.RunProcess)(executable, ["-B", "-c", code, bridge], Path.GetDirectoryName(bridge)!, null);
        ProcessRunner.RequireSuccess(result, $"Ecosystem catalogue/dependencies unavailable at '{executable}'; prepare it with 'tia install-plc-tools' and pass -EcosystemPython / -CompanionPython or set TIA_MCP_PLC_TOOLS_PYTHON");
        return executable;
    }

    internal static string GetToken(string value, string gitExecutable, string root, bool localOnly, int? timeoutMilliseconds = null)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var environment = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(environment)) return environment;
        if (localOnly) return "";
        var result = ProcessRunner.Run(gitExecutable, ["credential", "fill"], root,
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never" },
            standardInput: "protocol=https\nhost=github.com\n\n", timeoutMilliseconds: timeoutMilliseconds);
        if (result.ExitCode != 0) return "";
        var match = Regex.Match(result.StandardOutput, "(?m)^password=(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
