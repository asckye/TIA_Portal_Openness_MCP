using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ModelContextProtocol.Server;
using TiaMcp.Versioning;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

// Registration checks are pure managed; Bootstrap readiness probes only local registry/files and the current Windows token.
internal static class LegacyHostPassiveDiagnostics
{
    internal const string Contract = "legacy-host-passive-diagnostics-v1";
    internal const int MaxTools = 256;
    internal readonly record struct InstallationLocation(string? Path, string? Source);
    internal static JsonObject Readiness(string releaseKey) => Readiness(releaseKey, (string?)null, (string?)null);
    internal static JsonObject Readiness(string releaseKey, string? hostApiDirectory, string? hostApiDirectorySource)
        => Readiness(releaseKey, hostApiDirectory, hostApiDirectorySource, FindTiaInstallation, CurrentUserInOpennessGroup);
    internal static JsonObject Readiness(string releaseKey, Func<string, string?> installationProbe, Func<bool> groupProbe)
        => Readiness(releaseKey, null, null, key => {
            var path = installationProbe(key);
            return new InstallationLocation(path, path == null ? null : "registry");
        }, groupProbe);
    internal static JsonObject Readiness(string releaseKey, string? hostApiDirectory, string? hostApiDirectorySource,
        Func<string, InstallationLocation> installationProbe, Func<bool> groupProbe)
    {
        var release = TiaVersionCatalog.Get(releaseKey);
        InstallationLocation installation;
        if (!string.IsNullOrWhiteSpace(hostApiDirectory))
            installation = new InstallationLocation(ExactApiDirectory(release, hostApiDirectory), hostApiDirectorySource ?? "host-option");
        else installation = installationProbe(releaseKey);
        string? installPath = installation.Path;
        bool installed = !string.IsNullOrWhiteSpace(installPath);
        bool groupOk = groupProbe();
        bool ready = installed && groupOk;
        string cause = !installed
            ? $"TIA Portal {release.DisplayName} or its Openness API files were not found."
            : groupOk ? "" : "Current user is not in the required Siemens TIA Openness group.";
        string fix = !installed
            ? $"Install TIA Portal {release.DisplayName} with the Openness option, or set TiaPortalLocation to its installation folder; run `tia doctor` for details."
            : groupOk ? "" : "Add the current Windows user to the local 'Siemens TIA Openness' group, sign out and back in, then restart the MCP client.";
        string fixZh = !installed
            ? $"请安装 TIA Portal {release.DisplayName} 并选择 Openness 选项，或将 TiaPortalLocation 设置为安装目录；运行 `tia doctor` 查看详情。"
            : groupOk ? "" : "请将当前 Windows 用户添加到本机“Siemens TIA Openness”组，注销并重新登录，然后重启 MCP 客户端。";
        string next = !installed ? "(install TIA Portal)" : !groupOk ? "EnsureOpennessUserGroup" : "ConnectPortal";
        string reason = ready ? "TIA Portal and Openness group are ready. Call ConnectPortal to connect to a TIA Portal process." : cause + " " + fix;
        var environment = new JsonObject
        {
            ["tiaVersionInUse"] = release.MajorVersion, ["tiaVersionDetected"] = installed ? release.MajorVersion : null,
            ["opennessGroupOk"] = groupOk, ["tiaInstallPath"] = installed ? installPath : null,
            ["installSource"] = installation.Source,
            ["transport"] = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio",
            ["ready"] = ready, ["cause"] = ready ? null : cause,
            ["recommendedFix"] = ready ? null : fix, ["recommendedFixZh"] = ready ? null : fixZh
        };
        return new JsonObject
        {
            ["ready"] = ready, ["environment"] = environment,
            ["recommendedNextTool"] = next, ["recommendedReason"] = reason,
            ["cause"] = ready ? null : cause, ["recommendedFix"] = ready ? null : fix,
            ["recommendedFixZh"] = ready ? null : fixZh
        };
    }

    internal static void AddReadiness(JsonObject result, JsonObject readiness)
    {
        foreach (var pair in readiness) result[pair.Key] = pair.Value?.DeepClone();
        if (result["probes"] is JsonObject probes && readiness["environment"] is JsonObject environment)
        {
            probes["installedTia"] = environment["tiaInstallPath"] == null ? "not-found" : "found";
            probes["groupMembership"] = environment["opennessGroupOk"]?.GetValue<bool>() == true ? "member" : "not-member";
        }
    }

    private static InstallationLocation FindTiaInstallation(string releaseKey)
    {
        if (!OperatingSystem.IsWindows()) return new InstallationLocation(null, null);
        var release = TiaVersionCatalog.Get(releaseKey);
        string? configured = Environment.GetEnvironmentVariable("TiaPortalLocation");
        if (ExactApiPath(release, configured) is { } exact) return new InstallationLocation(exact, "environment");
        string version = release.Key == "15.1" ? "15_1" : release.MajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var section in new[] { "TIA_Opns", "Global" })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP" + version + "\\" + section);
                if (ExactApiPath(release, key?.GetValue("Path") as string) is { } fromRegistry) return new InstallationLocation(fromRegistry, "registry");
            }
            catch (UnauthorizedAccessException) /* swallow(env-probe): the other registry view remains available for the exact release lookup. */ { }
            catch (System.Security.SecurityException) /* swallow(env-probe): the other registry view remains available for the exact release lookup. */ { }
            catch (IOException) /* swallow(env-probe): an unreadable registry view does not rule out the other view or default install folder. */ { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (ExactApiPath(release, Path.Combine(root, "Siemens", "Automation", release.InstallFolder)) is { } fromDefault) return new InstallationLocation(fromDefault, "default-folder");
        }
        return new InstallationLocation(null, null);
    }

    private static string? ExactApiPath(TiaVersionDescriptor release, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            string full = Path.GetFullPath(root);
            string? driveRoot = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(driveRoot) || new DriveInfo(driveRoot).DriveType == DriveType.Network) return null;
            var apiDirectory = release.FindApiDirectory(full);
            return apiDirectory == null || !ApiMatchesRelease(release, apiDirectory) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            /* swallow(env-probe): an unusable local install path is not evidence of a matching release installation. */
            return null;
        }
    }

    private static string? ExactApiDirectory(TiaVersionDescriptor release, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            string full = Path.GetFullPath(directory);
            string? driveRoot = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(driveRoot) || new DriveInfo(driveRoot).DriveType == DriveType.Network) return null;
            var apiDirectory = release.FindApiDirectory(full);
            return apiDirectory != null && ApiMatchesRelease(release, apiDirectory) ? apiDirectory : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            /* swallow(env-probe): an unusable host API directory cannot establish release readiness. */
            return null;
        }
    }

    private static bool ApiMatchesRelease(TiaVersionDescriptor release, string apiDirectory)
    {
        string assemblyPath = Path.Combine(apiDirectory, release.ApiAssembly);
        string? fileVersion = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            string versionPrefix = fileVersion.Split('.')[0];
            if (int.TryParse(versionPrefix, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int encodedVersion) && encodedVersion >= 1000)
            {
                int expectedVersion = release.MajorVersion * 100 + (release.Key == "15.1" ? 1 : 0);
                return encodedVersion == expectedVersion;
            }
        }
        string? folderRelease = ApiFolderRelease(apiDirectory);
        return folderRelease == null || folderRelease == release.Key;
    }

    private static string? ApiFolderRelease(string apiDirectory)
    {
        var segments = apiDirectory.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            foreach (var release in TiaVersionCatalog.All)
            {
                if (string.Equals(segment, release.ApiFolder, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "V" + release.MajorVersion, StringComparison.OrdinalIgnoreCase)
                    || (release.Key == "15.1" && string.Equals(segment, "V15_1", StringComparison.OrdinalIgnoreCase)))
                    return release.Key;
            }
        }
        return null;
    }

    private static bool CurrentUserInOpennessGroup()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.Groups == null) return false;
            foreach (var sid in identity.Groups)
            {
                try
                {
                    string? name = sid.Translate(typeof(NTAccount)).Value;
                    if (string.Equals(name, "Siemens TIA Openness", StringComparison.OrdinalIgnoreCase)
                        || name?.EndsWith("\\Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) == true) return true;
                }
                catch (IdentityNotMappedException) /* swallow(env-probe): an unresolved token SID cannot establish group membership. */ { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or PlatformNotSupportedException)
        {
            /* swallow(env-probe): unavailable token group data is not proof of Openness group membership. */
            return false;
        }
        return false;
    }

    internal static JsonObject Inspect(string releaseKey, bool nativeSessionConfigured, IReadOnlyList<McpServerTool> tools)
    {
        var release = TiaVersionCatalog.Get(releaseKey);
        if (tools.Count > MaxTools) throw new InvalidOperationException();
        var names = tools.Select(t => t.ProtocolTool.Name).ToArray();
        bool unique = names.Distinct(StringComparer.Ordinal).Count() == names.Length;
        bool schemas = tools.All(t => ObjectSchema(t.ProtocolTool.InputSchema));
        bool mappings = FoundationTools.Definitions.Where(d => FoundationTools.Available(d, releaseKey)).All(d => names.Contains(FoundationV4Tool.Name(d.Name), StringComparer.Ordinal) && (d.ResponseMember == "ImportStaging" || WorkerOperations.Names.Contains(d.Operation)));
        var roster = new JsonArray();
        foreach (var tool in tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal))
        {
            var name = tool.ProtocolTool.Name;
            if (name.Length > 128) throw new InvalidOperationException();
            var definition = FoundationTools.Definitions.SingleOrDefault(d => FoundationV4Tool.Name(d.Name) == name);
            roster.Add(new JsonObject { ["name"] = name, ["execution"] = definition == null || definition.ResponseMember == "ImportStaging" ? "host-only" : "worker-protocol", ["wiredOperation"] = definition?.ResponseMember == "ImportStaging" ? null : definition?.Operation, ["objectSchemaContractValid"] = ObjectSchema(tool.ProtocolTool.InputSchema) });
        }
        var probes = new JsonObject();
        foreach (var key in new[] { "installedTia", "installedPublicApi", "sdkCompatibility", "groupMembership", "permission", "connectReadiness", "workerAvailability", "connectionState", "portalProcesses", "projectState", "automationContext", "nativeAcceptance" }) probes[key] = "not-probed";
        return new JsonObject {
            ["contract"] = Contract, ["scope"] = "managed host registration and structural schema checks only",
            ["upstreamResponseCompatible"] = false, ["nativeCertified"] = false,
            ["selectedRelease"] = new JsonObject { ["key"] = release.Key, ["displayName"] = release.DisplayName, ["catalogState"] = release.SupportState, ["catalogMeaning"] = "Build-target metadata only; not installed or accepted capability." },
            ["host"] = new JsonObject { ["profile"] = "plc-foundation", ["productionAccepted"] = false, ["nativeSessionConfigured"] = nativeSessionConfigured, ["nativeCallsDisabledByDefault"] = false, ["nativeCallsDisabledByConfiguration"] = !nativeSessionConfigured, ["nativeGateMeaning"] = "Configuration only; this diagnostic never invokes the worker even when enabled." },
            ["checks"] = new JsonObject { ["uniqueRegisteredNames"] = unique, ["objectSchemaContracts"] = schemas, ["foundationOperationsInSourceAllowlist"] = mappings, ["passed"] = unique && schemas && mappings, ["meaning"] = "Structural checks only; not complete JSON Schema validation, tool execution, PLC semantics or native readiness." },
            ["registeredToolCount"] = tools.Count, ["registeredTools"] = roster, ["probes"] = probes,
            ["sideEffects"] = new JsonObject { ["workerInvoked"] = false, ["tiaLaunchedOrAttached"] = false, ["groupInspectedOrRepaired"] = false, ["persistentSettingsChanged"] = false },
            ["recommendedNextTool"] = "RunCapabilitySelfTest"
        };
    }
    // Deliberately bounded structural contract, not a JSON Schema evaluator.
    private static bool ObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.GetString() != "object" || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind != JsonValueKind.False) return false;
        if (!schema.TryGetProperty("required", out var required)) return true;
        return required.ValueKind == JsonValueKind.Array && required.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && props.TryGetProperty(x.GetString()!, out _));
    }
}

internal static class LegacyHostToolRegistry
{
    internal static IReadOnlyList<McpServerTool> Create(IFoundationWorker worker, string releaseKey, bool nativeSessionConfigured,
        string? apiDirectory = null, string? apiDirectorySource = null)
    {
        TiaVersionCatalog.Get(releaseKey);
        var tools = FoundationTools.Create(worker, releaseKey).Concat(OfflineXmlTools.Create()).Concat(OfflineCompositionTools.Create()).Concat(OfflineBlockCompositionTools.Create()).Concat(OfflineSymbolManifestTools.Create()).Concat(OfflineLadderTools.Create()).ToList();
        tools.Add(new ImportOrderTool());
        Func<JsonObject> readiness = () => LegacyHostPassiveDiagnostics.Readiness(releaseKey, apiDirectory, apiDirectorySource);
        tools.AddRange(LegacyHostPassiveDiagnosticTools.Create(releaseKey, nativeSessionConfigured, () => tools, readiness));
        tools.Add(new ToolUsageTool(releaseKey, () => tools));
        var wrapped = new McpServerTool[tools.Count];
        Parallel.For(0, tools.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            // Gate only the bundled worker: an explicit --worker-exe fixture must reach its own dispatch path.
            Func<JsonObject>? readinessForTool = worker is WorkerClient { Bundled: true } ? readiness : null;
            wrapped[i] = new UsageHintTool(new FoundationV4Tool(tools[i], releaseKey, null, readinessForTest: readinessForTool));
        });
        tools.Clear(); tools.AddRange(wrapped);
        return tools.AsReadOnly();
    }
}
