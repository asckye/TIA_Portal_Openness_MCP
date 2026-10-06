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

        public static IEnumerable<string> Required(string root, string key)
        {
            var release = TiaVersionCatalog.Get(key);
            var host = Host(root, key);
            var directory = Path.GetDirectoryName(host);
            yield return host;
            yield return Path.Combine(directory, "TiaMcp.Logic.dll");
            yield return Path.Combine(directory, "ModelContextProtocol.dll");
            yield return Path.Combine(directory, "ModelContextProtocol.Core.dll");
            if (release.IsFullEngine)
            {
                yield return host + ".config";
                yield return Path.Combine(directory, "TiaMcp.Runtime.dll");
            }
            if (!release.IsFullEngine)
            {
                yield return Path.Combine(directory, "release-key.txt");
                yield return Path.ChangeExtension(host, ".dll");
                yield return Path.ChangeExtension(host, ".deps.json");
                yield return Path.ChangeExtension(host, ".runtimeconfig.json");
                yield return TiaOpenness.Shared.BundleLayout.WorkerPath(root, key);
                yield return Path.Combine(directory, "TiaMcp.WorkerChannel.dll");
                yield return Path.Combine(directory, "worker", "TiaMcp.Adapters.Contracts.dll");
            }
        }
    }

    public sealed class EnvironmentCheckContext
    {
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
    }

    public sealed class WorkbenchEnvironmentChecks
    {
        private readonly EnvironmentCheckContext context;
        private readonly EnvironmentProbeSources sources;
        public WorkbenchEnvironmentChecks(EnvironmentCheckContext context, EnvironmentProbeSources sources)
        { this.context = context; this.sources = sources; }

        public static IReadOnlyList<string> Ids => new[] { "installations", "membership", "framework", "runtime" }
            .Concat(TiaVersionCatalog.All.Select(v => "engine-" + v.Key))
            .Concat(new[] { "data", "port", "url", "firewall", "confirmation" }).ToArray();

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
    }
}
