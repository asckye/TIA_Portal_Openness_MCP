using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal sealed record PrerequisiteProbe(string Name, Func<string> Check);
internal sealed record PrerequisiteResult(string Name, bool Passed, string Detail);

internal static class ReleasePrerequisites
{
    internal static IReadOnlyList<PrerequisiteResult> Evaluate(IEnumerable<PrerequisiteProbe> probes)
    {
        var results = new List<PrerequisiteResult>();
        foreach (var probe in probes)
        {
            try { results.Add(new(probe.Name, true, probe.Check())); }
            catch (Exception ex) { results.Add(new(probe.Name, false, ex.Message)); }
        }
        return results;
    }

    internal static string RequireSdk(string output, string major, string label)
    {
        var match = Regex.Match(output, $"(?m)^({Regex.Escape(major)}\\.\\d+\\.\\d+) \\[([^\\]]+)\\]");
        return match.Success ? match.Groups[1].Value : throw new ReleaseException(label + " is required");
    }

    internal static string RequirePython(string output, int major, int minor, string label)
    {
        var text = output.Trim();
        if (!Version.TryParse(text, out var version) || version.Major != major || version.Minor < minor)
            throw new ReleaseException(label + $" >= {major}.{minor} is required");
        return text;
    }

    internal static string RequirePowerShell(string output, string label)
    {
        var text = output.Trim();
        if (!int.TryParse(text, out var major) || major < 7) throw new ReleaseException(label + " 7 is required");
        return text;
    }

    internal static string RequireEcosystemPython(string executable, string bridge, string python)
    {
        var code = "import runpy,sys; assert sys.version_info >= (3,12); import reportlab; m=runpy.run_path(sys.argv[1]); group,errors=m['load_groups'](); assert not errors, errors; assert len(m['catalog'](group)) >= 49";
        var result = ProcessRunner.Run(executable, ["-B", "-c", code, bridge], Path.GetDirectoryName(bridge)!);
        ProcessRunner.RequireSuccess(result, "Ecosystem catalogue/dependencies unavailable; set TIA_MCP_PLC_TOOLS_PYTHON");
        return executable;
    }

    internal static string RequireToken(string token) =>
        !string.IsNullOrWhiteSpace(token) ? "available (value hidden)" : throw new ReleaseException("GitHub token unavailable (value never printed)");

    internal static string GetToken(string value, string gitExecutable, string root, bool localOnly)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var environment = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(environment)) return environment;
        if (localOnly) return "";
        var result = ProcessRunner.Run(gitExecutable, ["credential", "fill"], root,
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never" },
            standardInput: "protocol=https\nhost=github.com\n\n");
        if (result.ExitCode != 0) return "";
        var match = Regex.Match(result.StandardOutput, "(?m)^password=(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    internal static string RequireFreeSpace(long availableBytes, int minimumGiB)
    {
        if (minimumGiB is < 1 or > 1024) throw new ReleaseException("MinimumFreeGB must be between 1 and 1024");
        if (availableBytes < minimumGiB * 1024L * 1024 * 1024) throw new ReleaseException($"At least {minimumGiB} GiB free space is required");
        return $"{availableBytes / 1073741824d:N1} GiB free; minimum {minimumGiB} GiB";
    }

    internal static string RequirePublicApi(string directory, string canonicalDirectory, IReadOnlyList<string> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var candidate = Path.Combine(directory, assembly);
            var canonical = Path.Combine(canonicalDirectory, assembly);
            if (!File.Exists(candidate)) throw new ReleaseException($"Missing {assembly} in {directory}");
            if (!File.Exists(canonical)) throw new ReleaseException($"Multi-version build needs {assembly} in {canonicalDirectory}");
            if (!Path.GetFullPath(candidate).Equals(Path.GetFullPath(canonical), StringComparison.OrdinalIgnoreCase) &&
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(candidate)), SHA256.HashData(File.ReadAllBytes(canonical))))
                throw new ReleaseException("Full-engine and multi-version PublicAPI inputs differ");
        }
        return directory;
    }

    internal static string RequirePinnedArchive(JsonElementArchive archive, string path)
    {
        if (!Regex.IsMatch(archive.Url, "^https://builds\\.dotnet\\.microsoft\\.com/dotnet/", RegexOptions.IgnoreCase))
            throw new ReleaseException("Archive must use the pinned official Microsoft URL");
        if (!File.Exists(path)) throw new ReleaseException("Archive not cached (offline): " + path);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA512.HashData(stream));
        if (!actual.Equals(archive.Sha512, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Cached archive SHA-512 mismatch: " + path);
        return "cached; SHA-512 verified";
    }

    internal static IReadOnlyList<JsonElementArchive> ReadPinnedArchives(string path)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("archives").EnumerateArray().Select(item => new JsonElementArchive(
            item.GetProperty("name").GetString() ?? "", item.GetProperty("url").GetString() ?? "", item.GetProperty("sha512").GetString() ?? "")).ToArray();
    }
}

internal sealed record JsonElementArchive(string Name, string Url, string Sha512);
