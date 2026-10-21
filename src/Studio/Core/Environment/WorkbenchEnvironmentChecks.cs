using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Versioning;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Environment
{
    public enum EnvironmentFindingStatus { Pass, Fail, Unknown, Guidance }

    public sealed class EnvironmentFinding
    {
        public string Id { get; }
        public EnvironmentFindingStatus Status { get; }
        public string Result { get; }
        public string Evidence { get; }
        public EnvironmentFinding(string id, EnvironmentFindingStatus status, string result, string evidence = "")
        { Id = id; Status = status; Result = result; Evidence = evidence; }
    }

    // Environment checks use the same product table as client configuration.
    public static class EnvironmentBundleFiles
    {
        public static string HostExecutable(string key)
        { return TiaOpenness.Shared.BundleLayout.GetProduct(key).Executable; }

        public static string WorkerExecutable(string key) { return TiaOpenness.Shared.BundleLayout.GetProduct(key).WorkerExecutable; }

        public static string Host(string root, string key)
        { return TiaOpenness.Shared.BundleLayout.WorkbenchEnginePath(root, key, root); }

        public static string SelectHost(string root, string releaseKey, Func<string, bool> exists)
        {
            var key = SelectHostRelease(root, releaseKey, exists);
            return key == null ? null : Host(root, key);
        }

        // The release key of the host SelectHost returns; the doctor fix passes it as --release-key.
        public static string SelectHostRelease(string root, string releaseKey, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(root)) return null;
            var keys = string.IsNullOrEmpty(releaseKey) ? TiaVersionCatalog.Runnable.Select(v => v.Key) : new[] { releaseKey };
            return keys.FirstOrDefault(key => exists(Host(root, key)));
        }

        public static IEnumerable<string> Required(string root, string key)
        {
            var release = TiaVersionCatalog.Get(key);
            var host = Host(root, key);
            var directory = Path.GetDirectoryName(host);
            yield return host;
            yield return Path.Combine(directory, "TiaMcp.Logic.dll");
            yield return Path.Combine(directory, "ModelContextProtocol.dll");
            yield return Path.Combine(directory, "ModelContextProtocol.Core.dll");
            if (release.HostKind == "foundation")
            {
                yield return Path.Combine(directory, "release-key.txt");
                yield return Path.ChangeExtension(host, ".dll");
                yield return Path.ChangeExtension(host, ".deps.json");
                yield return Path.ChangeExtension(host, ".runtimeconfig.json");
                yield return TiaOpenness.Shared.BundleLayout.WorkerPath(root, key);
                yield return Path.Combine(directory, "TiaMcp.WorkerChannel.dll");
                yield return Path.Combine(directory, "worker", "TiaMcp.Adapters.Contracts.dll");
                yield return Path.Combine(directory, "worker", "TiaMcp.Adapter." + key + ".dll");
                yield return TiaOpenness.Shared.BundleLayout.WorkerPath(root, key) + ".config";
                // V20/V21 hosts keep the runtime assembly next to them; delivery-files.json requires it for these two releases.
                if (release.IsFullEngine) yield return Path.Combine(directory, "TiaMcp.Runtime.dll");
            }
        }
    }

    public sealed class EnvironmentCheckContext
    {
        public string ReleaseKey { get; set; }
        public string BundleRoot { get; set; }
        public string DataRoot { get; set; }
        public string ConfigDirectory { get; set; }
        public string LogsDirectory { get; set; }
        public string DiagnosticsDirectory { get; set; }
        public string AuditDirectory { get; set; }
        public string[] LogReadRoots { get; set; }
        public string[] AuditReadRoots { get; set; }
        public bool UserFallback { get; set; }
        public string HttpPrefix { get; set; }
        public string HttpConfigurationPath { get; set; }
        public string HttpEndpointError { get; set; }
    }

    // The same policies run with Windows sources and isolated offline fakes.
    public sealed class EnvironmentProbeSources
    {
        public Func<DoctorReport> Doctor { get; set; }
        public Func<bool?> ListedGroupMember { get; set; }
        public Func<string, bool> FileExists { get; set; }
        public Func<string, IEnumerable<string>> Directories { get; set; }
        public Func<string, bool> Writable { get; set; }
        public Func<int, string[]> PortOwners { get; set; }
        public Func<string, bool?> UrlReserved { get; set; }
        public Func<string, bool?> FirewallAllowed { get; set; }
        // Openness firewall AllowList (read only): the entry for one application name, null when absent.
        public Func<string, AllowListEntry> AllowListEntry { get; set; }
        // UTC modification time and SHA-256 of a file, as the Openness firewall computes them.
        public Func<string, KeyValuePair<DateTime, byte[]>> FileIdentity { get; set; }
    }

    public sealed class WorkbenchEnvironmentChecks
    {
        private readonly EnvironmentCheckContext context;
        private readonly EnvironmentProbeSources sources;
        public WorkbenchEnvironmentChecks(EnvironmentCheckContext context, EnvironmentProbeSources sources)
        { this.context = context; this.sources = sources; }

        public static IReadOnlyList<string> Ids => new[] { "installations", "membership", "openness-readiness", "framework", "runtime" }
            .Concat(TiaVersionCatalog.All.Select(v => "engine-" + v.Key))
            .Concat(new[] { "data", "port", "url", "firewall", "confirmation", "openness-allowlist" }).ToArray();

        public IReadOnlyList<EnvironmentFinding> Run()
        {
            DoctorReport doctor = null;
            string doctorError = "";
            try { doctor = sources.Doctor(); }
            catch (Exception ex) { doctorError = ex.Message; }
            return Ids.Select(id => Inspect(id, doctor, doctorError)).ToArray();
        }

        private EnvironmentFinding Inspect(string id, DoctorReport doctor, string doctorError)
        {
            EnvironmentFinding Finding(EnvironmentFindingStatus status, string result, string evidence = "")
                => new EnvironmentFinding(id, status, result, evidence);
            EnvironmentFinding Known(bool pass, string evidence = "")
                => Finding(pass ? EnvironmentFindingStatus.Pass : EnvironmentFindingStatus.Fail, pass ? "Pass" : "Fail", evidence);
            try
            {
                if (id == "confirmation") return Finding(EnvironmentFindingStatus.Guidance, "Guidance");
                if (id == "openness-allowlist") return AllowList(Finding);
                if (id == "openness-readiness")
                {
                    if (doctor == null) return Finding(EnvironmentFindingStatus.Unknown, "Unknown", doctorError);
                    if (string.IsNullOrEmpty(context.BundleRoot)) return Finding(EnvironmentFindingStatus.Unknown, "Unknown");
                    var host = EnvironmentBundleFiles.SelectHost(context.BundleRoot, context.ReleaseKey, sources.FileExists);
                    if (host == null) return Finding(EnvironmentFindingStatus.Unknown, "Unknown", "No bundled host is present for the selected release.");
                    var problems = doctor.Checks.Where(check => check.Id is "TIA-INSTALL" or "TIA-LAYOUT" or "TIA-GROUP")
                        .Where(check => check.Status == CheckStatus.Fail)
                        .Select(check => check.Title + ": " + check.Detail + (string.IsNullOrWhiteSpace(check.Remedy) ? "" : "\n" + check.Remedy))
                        .ToArray();
                    return problems.Length == 0
                        ? Finding(EnvironmentFindingStatus.Pass, "Pass", "Engine files are available and the environment doctor found no startup blocker.")
                        : Finding(EnvironmentFindingStatus.Fail, "NotReady", "The MCP engine can start, but TIA Openness is not ready.\n"
                            + string.Join("\n", problems) + "\nHost: " + host);
                }
                if (id == "installations" || id == "framework" || id == "membership")
                {
                    if (doctor == null) return Finding(EnvironmentFindingStatus.Unknown, "Unknown", doctorError);
                    if (id == "installations")
                        return Known(doctor.Installations.Count > 0, string.Join(", ", doctor.Installations.Select(i => "V" + i.Version + " — " + i.EngineeringDllPath)));
                    var check = doctor.Checks.Single(c => c.Id == (id == "framework" ? "ENV-NETFX" : "TIA-GROUP"));
                    if (check.Status == CheckStatus.Warn) return Finding(EnvironmentFindingStatus.Unknown, "Unknown", check.Detail);
                    if (id == "framework" || check.Status == CheckStatus.Pass) return Known(check.Status == CheckStatus.Pass, check.Detail);
                    var listed = sources.ListedGroupMember();
                    return listed == true ? Finding(EnvironmentFindingStatus.Fail, "NewLogon", check.Detail)
                        : listed == false ? Known(false, check.Detail) : Finding(EnvironmentFindingStatus.Unknown, "Unknown", check.Detail);
                }
                if (id == "data") return sources.Writable(context.DataRoot)
                    ? Finding(EnvironmentFindingStatus.Pass, context.UserFallback ? "Fallback" : "Pass", context.DataRoot + "\n" + context.ConfigDirectory + "\n" + context.LogsDirectory)
                    : Known(false, context.DataRoot);
                if (id == "runtime" || id.StartsWith("engine-", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(context.BundleRoot)) return Finding(EnvironmentFindingStatus.Unknown, "Unknown");
                    if (id != "runtime")
                    {
                        var required = EnvironmentBundleFiles.Required(context.BundleRoot, id.Substring(7)).ToArray();
                        var missing = required.Where(p => !sources.FileExists(p)).ToArray();
                        return Known(missing.Length == 0, string.Join("\n", missing.Length == 0 ? required : missing));
                    }
                    var runtime = Path.Combine(context.BundleRoot, "runtime", "dotnet");
                    bool ContainsRuntime(string directory, string assembly) => sources.Directories(directory)
                        .Any(d => Version.TryParse(Path.GetFileName(d), out var version) && version.Major == 10 && sources.FileExists(Path.Combine(d, assembly)));
                    bool ok = sources.FileExists(Path.Combine(runtime, "dotnet.exe"))
                        && ContainsRuntime(Path.Combine(runtime, "host", "fxr"), "hostfxr.dll")
                        && ContainsRuntime(Path.Combine(runtime, "shared", "Microsoft.NETCore.App"), "System.Private.CoreLib.dll")
                        && ContainsRuntime(Path.Combine(runtime, "shared", "Microsoft.AspNetCore.App"), "Microsoft.AspNetCore.dll")
                        && ContainsRuntime(Path.Combine(runtime, "shared", "Microsoft.WindowsDesktop.App"), "PresentationFramework.dll");
                    return Known(ok, runtime);
                }
                if (!string.IsNullOrEmpty(context.HttpEndpointError)) return Finding(EnvironmentFindingStatus.Unknown, "Unknown", context.HttpEndpointError);
                if (string.IsNullOrEmpty(context.HttpPrefix)) return Finding(EnvironmentFindingStatus.Unknown, "NoEndpoint");
                var prefix = new Uri(context.HttpPrefix);
                if (id == "port")
                {
                    var owners = sources.PortOwners(prefix.Port);
                    return owners.Length == 0 ? Known(true, context.HttpPrefix + "\n" + context.HttpConfigurationPath)
                        : Finding(EnvironmentFindingStatus.Fail, "PortUsed", context.HttpPrefix + "\n" + context.HttpConfigurationPath + "\n" + string.Join("\n", owners));
                }
                bool? present = id == "url" ? sources.UrlReserved(context.HttpPrefix) : sources.FirewallAllowed(context.HttpPrefix);
                return present.HasValue ? Known(present.Value, context.HttpPrefix + "\n" + context.HttpConfigurationPath)
                    : Finding(EnvironmentFindingStatus.Unknown, "Unknown", context.HttpPrefix + "\n" + context.HttpConfigurationPath);
            }
            catch (Exception ex) { return Finding(EnvironmentFindingStatus.Unknown, "Unknown", ex.Message); }
        }

        // Every shipped worker executable must be on the Openness AllowList with its current path, date and hash; a rebuilt or
        // updated worker is a new application for the firewall. Never writes the registry.
        private EnvironmentFinding AllowList(Func<EnvironmentFindingStatus, string, string, EnvironmentFinding> finding)
        {
            if (string.IsNullOrEmpty(context.BundleRoot)) return finding(EnvironmentFindingStatus.Unknown, "Unknown", "");
            if (sources.AllowListEntry == null || sources.FileIdentity == null)
                return finding(EnvironmentFindingStatus.Unknown, "Unknown", "The Openness AllowList cannot be read on this platform.");
            var workers = TiaVersionCatalog.Runnable.Select(v => Path.GetFullPath(TiaOpenness.Shared.BundleLayout.WorkerPath(context.BundleRoot, v.Key)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(sources.FileExists).ToArray();
            if (workers.Length == 0) return finding(EnvironmentFindingStatus.Unknown, "Unknown", "No bundled worker executable is present.");
            var lines = new List<string>();
            var missing = new List<KeyValuePair<string, AllowListEntry>>();
            foreach (var worker in workers)
            {
                var identity = sources.FileIdentity(worker);
                var expected = OpennessAllowList.Expected(worker, identity.Key, identity.Value);
                string name = Path.GetFileName(worker);
                string state = OpennessAllowList.Compare(expected, sources.AllowListEntry(name));
                lines.Add(name + ": " + (state == "match" ? "allowed" : state == "missing" ? "no AllowList entry" : "AllowList entry differs (" + state + ")"));
                if (state != "match") missing.Add(new KeyValuePair<string, AllowListEntry>(name, expected));
            }
            if (missing.Count == 0) return finding(EnvironmentFindingStatus.Pass, "Pass", string.Join("\n", lines));
            return finding(EnvironmentFindingStatus.Fail, "Fail", string.Join("\n", lines) + "\n" + OpennessAllowList.AdministratorSteps(missing));
        }
    }
}
