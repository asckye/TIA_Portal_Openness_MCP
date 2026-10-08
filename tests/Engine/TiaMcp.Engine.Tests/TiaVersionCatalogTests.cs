using TiaMcpServer;
using System;
using System.Linq;
using System.Text.Json;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Versioning;
using TiaMcpServer.Siemens;

namespace TiaMcp.Engine.Tests
{
    internal static class TiaVersionCatalogTests
    {
        private sealed class FileBoundaryFixture
        {
            public string Export(string path, bool option) => "legacy-string";
            public string Export(System.IO.FileInfo path, bool option) => "modern-file";
            public void AssignableOnly(object path) { }
            public void Fail() => throw new InvalidOperationException("native-sentinel");
        }
        private static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        public static void Run(Action<bool, string> check)
        {
            var referenceIdentities = new[] {
                new[] { "14sp1", "Siemens.Engineering, Version=14.0.1.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "15.1", "Siemens.Engineering, Version=15.1.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "16", "Siemens.Engineering, Version=16.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "17", "Siemens.Engineering, Version=17.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "18", "Siemens.Engineering, Version=18.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "19", "Siemens.Engineering, Version=19.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "20", "Siemens.Engineering, Version=20.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84" },
                new[] { "21", "Siemens.Engineering.Base, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b" }
            };
            foreach (var entry in referenceIdentities)
            {
                var expected = new System.Reflection.AssemblyName(entry[1]);
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "authorized-api-copy", entry[0]);
                var contract = OpennessReleaseContract.For(entry[0], expected, directory);
                check(contract.CoreAssemblyIdentity.FullName == entry[1], "recorded full SDK identity " + entry[0]);
                contract.RequireAssemblyIdentity(expected, System.IO.Path.Combine(directory, expected.Name + ".dll"));
                check(true, "explicit copied SDK directory accepted without installation-path inference " + entry[0]);
                try { contract.RequireAssemblyIdentity(expected, System.IO.Path.Combine(directory + "-other", expected.Name + ".dll")); check(false, "different SDK root rejected " + entry[0]); }
                catch (System.IO.FileLoadException) { check(true, "different SDK root rejected " + entry[0]); }
                var wrongKey = new System.Reflection.AssemblyName(entry[1].Replace(
                    entry[0] == "21" ? "29bfe5fdf4ba5d3b" : "d29ec89bac048f84", "0000000000000000"));
                try { contract.RequireAssemblyIdentity(wrongKey, System.IO.Path.Combine(directory, expected.Name + ".dll")); check(false, "wrong public key rejected " + entry[0]); }
                catch (System.IO.FileLoadException) { check(true, "wrong public key rejected " + entry[0]); }
            }
            try { OpennessReleaseContract.For("21", new System.Reflection.AssemblyName("Siemens.Engineering, Version=21.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84")); check(false, "V21 monolithic old-token identity rejected"); }
            catch (System.IO.FileLoadException) { check(true, "V21 monolithic old-token identity rejected"); }
            check(Rejects(() => OpennessReleaseContract.For("21", selectedPublicApiDirectory: "unverified")), "directory alone cannot bind a contract");
            var fixture = new FileBoundaryFixture();
            foreach (var key in new[] { "14sp1", "15.1", "21" })
            {
                var contract = OpennessReleaseContract.For(key);
                var method = OpennessReleaseContract.RequireExactMethod(typeof(FileBoundaryFixture), "Export", contract.XmlFileArgumentType, typeof(bool));
                check((string?)OpennessReleaseContract.InvokeExact(method, fixture, new[] { contract.XmlFileArgument("test.xml"), (object)false })
                    == "modern-file", "exact adapter overload selection " + key);
            }
            try { OpennessReleaseContract.RequireExactMethod(typeof(FileBoundaryFixture), "Export", typeof(System.IO.DirectoryInfo), typeof(bool)); check(false, "no argument-count overload fallback"); }
            catch (MissingMethodException) { check(true, "no argument-count overload fallback"); }
            try { OpennessReleaseContract.RequireExactMethod(typeof(FileBoundaryFixture), "AssignableOnly", typeof(string)); check(false, "no assignable-type overload fallback"); }
            catch (MissingMethodException) { check(true, "no assignable-type overload fallback"); }
            try { OpennessReleaseContract.InvokeExact(typeof(FileBoundaryFixture).GetMethod("Fail")!, fixture, Array.Empty<object>()); check(false, "native exception unwrapped"); }
            catch (InvalidOperationException ex) { check(ex.Message == "native-sentinel", "native exception unwrapped"); }
            foreach (string version in new[] { "20", "21" })
                check(ToolVersionPolicy.CallProblem(version, "ImportLibraryTypeDocuments", key => key == "libraryName" ? "Global" : null).Length > 0, "library scope refusal before dispatch " + version);
            foreach (string option in new[] { "SkipInactiveCultures", "ActivateInactiveCultures" })
            {
                check(ToolVersionPolicy.CallProblem("20", "ImportLibraryTypeDocuments", key => key == "importOptions" ? option : null).Length > 0, "V20 library option refused before dispatch " + option);
                check(ToolVersionPolicy.CallProblem("21", "ImportLibraryTypeDocuments", key => key == "importOptions" ? option : null).Length == 0, "V21 library option retains native validation " + option);
            }
            TiaMcpServer.ModelContextProtocol.ToolBridgeProbes.VersionProbeCalls = 0;
            bool isV20 = EngineRouter.CompiledTiaMajorVersion == 20;
            var preflight = TiaMcpServer.ModelContextProtocol.McpServer.PreflightToolCall("ManageDcbLibraries", "{\"action\":\"import\"}");
            check((preflight.Meta?["ok"]?.GetValue<bool?>() == true) != isV20, "preflight agrees with compiled adapter action gate");
            var imported = TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ManageDcbLibraries", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"action\":\"import\"}")));
            check((imported.IsError != true) != isV20, "bridge agrees with compiled adapter action gate");
            check(TiaMcpServer.ModelContextProtocol.ToolBridgeProbes.VersionProbeCalls == (isV20 ? 0 : 1), "denial happens before invoking mutation body");
            var read = TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ManageDcbLibraries", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{}")));
            check(read.IsError != true, "mixed-action read remains callable in both builds");
            int callsBeforeUnknown = TiaMcpServer.ModelContextProtocol.ToolBridgeProbes.VersionProbeCalls;
            TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ManageDcbLibraries", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"action\":\"futureAction\"}")));
            check(TiaMcpServer.ModelContextProtocol.ToolBridgeProbes.VersionProbeCalls == callsBeforeUnknown, "unknown action cannot invoke body");
            TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ManageDcbLibraries", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{\"ACTION\":\"read\",\"action\":\"import\"}")));
            check(TiaMcpServer.ModelContextProtocol.ToolBridgeProbes.VersionProbeCalls == callsBeforeUnknown, "case-duplicate bridge selectors are rejected before dispatch");
            check(TiaMcpServer.ModelContextProtocol.McpServer.PreflightToolCall("ManageDcbLibraries", "{\"ACTION\":\"read\",\"action\":\"import\"}").Meta?["ok"]?.GetValue<bool?>() == false,
                "preflight refuses case-duplicate selectors");
            var safety = TiaMcpServer.ModelContextProtocol.McpServer.CallTool("ListSafetyActivationTests", new ToolArguments(JsonSerializer.Deserialize<JsonElement>("{}")));
            check((safety.IsError != true) != isV20, "whole-tool gate also applies to bridge");
            foreach (var key in new[] { "14", "14sp1", "15", "15.1", "16", "17", "18", "19", "22", "" })
                check(ToolVersionPolicy.ToolProblem(key, "Connect").Length > 0, key + " tool routing has no implicit adapter");
            foreach (var tool in ToolVersionPolicy.V21Only.Keys)
            {
                check(ToolVersionPolicy.ToolProblem("20", tool).Length > 0, tool + " hidden/denied on V20");
                check(ToolVersionPolicy.ToolProblem("21", tool).Length == 0, tool + " existing V21 route retained, not native-certified");
            }
            foreach (var tool in new[] { "ManageDcbLibraries", "ManageDriveHardwareModule" })
            {
                check(ToolVersionPolicy.CallProblem("20", tool, _ => null).Length == 0, tool + " missing action retains read default");
                check(ToolVersionPolicy.CallProblem("20", tool, _ => "read").Length == 0, tool + " mixed read retained on V20");
                check(ToolVersionPolicy.CallProblem("21", tool, _ => "futureAction").Length > 0, tool + " unknown action denied even on V21");
            }
            check(ToolVersionPolicy.CallProblem("20", "managedcblibraries", _ => "IMPORT").Length > 0, "case normalization cannot bypass V20 import guard");
            check(ToolVersionPolicy.CallProblem("21", "ManageDcbLibraries", _ => "import").Length == 0, "V21 DCB import retains native guard");
            foreach (var action in new[] { "changeType", "setPositionNumber" })
                check(ToolVersionPolicy.CallProblem("20", "ManageDriveHardwareModule", _ => action).Length > 0, "V20 hardware module mutation denied: " + action);
            foreach (var release in TiaVersionCatalog.All)
            {
                var contract = OpennessReleaseContract.For(release.Key);
                check(contract.XmlFileArgumentType == typeof(System.IO.FileInfo), "XML signature boundary " + release.Key);
                check(contract.XmlFileArgument(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "a.xml")).GetType() == contract.XmlFileArgumentType, "XML argument exact type " + release.Key);
            }
            check(OpennessReleaseContract.For("14sp1").SoftwareContainerType.EndsWith("HW.Features.SoftwareContainer"), "SP1 concrete service boundary");
            check(OpennessReleaseContract.For("14sp1").TagConstantCollections.SequenceEqual(new[] { "UserConstants", "SystemConstants" }), "SP1 constants split");
            check(OpennessReleaseContract.For("16").UnifiedCollection("Tags") == "HmiTags" && OpennessReleaseContract.For("17").UnifiedCollection("Tags") == "Tags", "Unified V16-to-V17 rename");
            foreach (var logical in new[] { "Tags", "TagTables", "Connections", "SystemTags" })
            {
                check(OpennessReleaseContract.For("16").ResolveUnifiedCollection(logical, new[] { "Hmi" + logical }) == "Hmi" + logical, "manual V16 alias " + logical);
                check(OpennessReleaseContract.For("16").ResolveUnifiedCollection(logical, new[] { logical }) == logical, "V19-hosted V16 compatibility alias " + logical);
                check(OpennessReleaseContract.For("17").ResolveUnifiedCollection(logical, new[] { logical }) == logical, "V17 renamed alias " + logical);
            }
            try { OpennessReleaseContract.For("16").ResolveUnifiedCollection("Tags", new[] { "Tags", "HmiTags" }); check(false, "ambiguous Unified aliases refused"); }
            catch (System.Reflection.AmbiguousMatchException) { check(true, "ambiguous Unified aliases refused"); }
            try { OpennessReleaseContract.For("16").ResolveUnifiedCollection("Tags", Array.Empty<string>()); check(false, "missing Unified properties not interpreted as empty collections"); }
            catch (MissingMemberException) { check(true, "missing Unified properties not interpreted as empty collections"); }
            check(OpennessReleaseContract.PublicApiKey(@"C:\Portal V15\PublicAPI\V14 SP1\Siemens.Engineering.dll") == "14sp1", "API identity differs from host install");
            check(OpennessReleaseContract.PublicApiKey("/PublicAPI/V15.1/Siemens.Engineering.dll") == "15.1", "minor API identity retained");
            check(OpennessReleaseContract.PublicApiKey("/PublicAPI/V21/net48/Siemens.Engineering.dll") == "21", "V21 net48 API directory");
            foreach (var path in new[] { "/Portal V14/Siemens.Engineering.dll", "/PublicAPI/V15.10/file", "/PublicAPI/V14 SP10/file", "/PublicAPI/V22/file" })
                check(OpennessReleaseContract.PublicApiKey(path) == null, "reject ambiguous API path " + path);
            foreach (var entry in new[] {
                new[] { "ManagePlcProtection", "action", "protectAllConfiguration" },
                new[] { "ManagePlcProtection", "action", "unprotectAllConfiguration" },
                new[] { "ManagePlcExternalSources", "action", "renameGroup" },
                new[] { "ManagePlcSafety", "action", "generateBaseId" },
                new[] { "ManagePlcDocuments", "action", "createFromMasterCopy" },
                new[] { "ManagePlcDocuments", "action", "createFromLibraryType" },
                new[] { "ManageSivarcBlockDefinition", "kind", "tagMemberSettings" },
                new[] { "ManageSivarcBlockDefinition", "kind", "commonParameters" },
                new[] { "ManageSivarcBlockDefinition", "kind", "blockParameter" },
                new[] { "ManageDeviceServiceObjects", "family", "webApplications" },
                new[] { "ManageDeviceServiceObjects", "family", "telecontrolDataPoints" } })
            {
                check(ToolVersionPolicy.CallProblem("20", entry[0], key => key == entry[1] ? entry[2] : null).Length != 0, "V20 scoped guard " + string.Join("/", entry));
                check(ToolVersionPolicy.CallProblem("21", entry[0], key => key == entry[1] ? entry[2] : null).Length == 0, "V21 existing scoped route " + string.Join("/", entry));
            }
            foreach (string action in new[] { "export", "import" })
                check(ToolVersionPolicy.CallProblem("20", "ManageDeviceServiceObjects", key => key == "family" ? "telecontrolDataPoints" : key == "action" ? action : null).Length == 0, "V20 telecontrol file route retained " + action);
            check(ToolVersionPolicy.CallProblem("20", "ManagePlcDocuments", key => key == "objectKind" ? "type" : key == "action" ? "createFromMasterCopy" : null).Length == 0, "V20 PLC type master-copy route retained");
            foreach (string kind in new[] { "tagDefinition", "textDefinition" })
                check(ToolVersionPolicy.CallProblem("20", "ManageSivarcBlockDefinition", key => key == "kind" ? kind : null).Length == 0, "V20 SiVArc definition retained " + kind);
            check(!isV20 || (string?)TiaMcpServer.ModelContextProtocol.McpServer.ResultBody(safety)?["error"]?["code"] == "UNSUPPORTED_CAPABILITY",
                "known unavailable tool is distinguished from unknown tool");
            try { OpennessReleaseContract.For("14sp1").RequireAssembly(typeof(TiaVersionCatalogTests).Assembly); check(false, "unbound legacy adapter cannot dispatch"); }
            catch (NotSupportedException) { check(true, "unbound legacy adapter cannot dispatch"); }
            foreach (var verb in new[] { "help", "--help", "-h", "version", "schema", "HELP" })
                check(CliOptions.IsInformationalCommand(new[] { verb }), verb + " bypasses native startup");
            foreach (var verb in new[] { "config", "compile", "doctor", "--tia-version", "" })
                check(!CliOptions.IsInformationalCommand(new[] { verb }), verb + " is not a static information command");
            check(!CliOptions.IsInformationalCommand(new string[0]), "MCP startup still validates versions");
            string[] keys = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
            check(TiaVersionCatalog.All.Select(v => v.Key).SequenceEqual(keys), "version catalog preserves all exact release identities");
            check(TiaVersionCatalog.Runnable.Select(v => v.Key).SequenceEqual(keys.AsEnumerable().Reverse()), "all eight releases are selectable, V21 remains default");
            check(TiaVersionCatalog.Get("14sp1").Key == "14sp1", "V14 SP1 remains a target");
            check(TiaVersionCatalog.Get("15.1").Key == "15.1", "V15.1 remains a target despite V15 exclusion");
            check(OpennessReleaseContract.For("14sp1").SclXmlBodyEvidence == "unverified", "SP1 SCL body remains unverified");
            foreach (var excluded in new[] { "14", "15" })
            {
                try { TiaVersionCatalog.Get(excluded); check(false, excluded + " excluded from catalog"); }
                catch (ArgumentException ex) { check(ex.Message.Contains("Unsupported TIA version key"), excluded + " rejected as unsupported, not planned"); }
                check(Rejects(() => TiaVersionCatalog.RequireRunnable(excluded)), excluded + " has no executable fallback");
                check(Rejects(() => OpennessReleaseContract.For(excluded)), excluded + " cannot instantiate an adapter");
                foreach (var flag in new[] { "--tia-version", "--tia-major-version", "-tia-major-version" })
                    check(Rejects(() => CliOptions.ParseArgs(new[] { flag, excluded })), excluded + " rejected by " + flag);
                check(OpennessReleaseContract.PublicApiKey("/PublicAPI/V" + excluded + "/Siemens.Engineering.dll") == null, excluded + " API directory is out of scope");
            }
            foreach (var key in keys.Take(6))
            {
                var entry = TiaVersionCatalog.Get(key);
                check(entry.IsRunnable && !entry.IsFullEngine && entry.SupportState == "plc-foundation" && entry.RuntimeDirectory == "v" + key && entry.EngineOutputDirectory == null,
                    key + " uses an exact PLC foundation runtime");
                check(TiaVersionCatalog.RequireRunnable(key).Key == key, key + " can select its independent runtime");
                check(Rejects(() => CliOptions.ParseArgs(new[] { "--tia-version", key })), key + " CLI fails closed");
            }
            foreach (var key in new[] { "", "0", "22", "15.0", "14.1", "V20", "020", " 20", "14SP1", "../20" })
                check(Rejects(() => TiaVersionCatalog.Get(key)), "unknown or ambiguous key rejected: " + key);
            check(Rejects(() => TiaVersionCatalog.Get(null!)), "null key rejected");
            foreach (int major in new[] { 20, 21 })
            {
                var entry = TiaVersionCatalog.RequireRunnable(major);
                check(entry.MajorVersion == major && entry.RuntimeDirectory == "v" + major, "runtime path preserves V" + major);
                check(entry.EngineOutputDirectory == (major == 20 ? "bin-v20" : "bin"), "source path preserves V" + major);
                TiaVersionCatalog.RequireMatchingEngine(major, major);
                foreach (var flag in new[] { "--tia-version", "--tia-major-version", "-tia-major-version" })
                    check(CliOptions.ParseArgs(new[] { flag, major.ToString() }).TiaMajorVersion == major, flag + " preserves V" + major);
            }
            check(Rejects(() => TiaVersionCatalog.RequireMatchingEngine(20, 21)), "V21 engine cannot execute V20 request after failed redirect");
            check(Rejects(() => TiaVersionCatalog.RequireMatchingEngine(21, 20)), "V20 engine cannot execute V21 request after failed redirect");
            TiaVersionCatalog.RequireMatchingEngine("15.1", "15.1");
            check(Rejects(() => TiaVersionCatalog.RequireMatchingEngine("15.1", "14sp1")), "minor/SP release identity is enforced");
            check(Rejects(() => CliOptions.ParseArgs(new[] { "--tia-version" })), "missing precise version rejected");
            check(Rejects(() => CliOptions.ParseArgs(new[] { "--tia-major-version", "nonsense" })), "malformed legacy version no longer silently auto-detects");
            check(Rejects(() => CliOptions.ParseArgs(new[] { "--tia-version", "20", "--tia-major-version", "21" })), "conflicting aliases rejected");
            check(CliOptions.ParseArgs(new[] { "--tia-version", "20", "--tia-major-version", "20" }).TiaMajorVersion == 20, "consistent repeated aliases accepted");
            check(!CliOptions.ParseArgs(new string[0]).TiaMajorVersion.HasValue, "unspecified version remains auto-detected");
            foreach (int major in new[] { -1, 0, 14, 15, 16, 17, 18, 19, 22 })
                foreach (TiaFeature feature in Enum.GetValues(typeof(TiaFeature)))
                    check(!Capability.IsSupported(feature, major), "unknown/planned/future feature gate denied: " + major + "/" + feature);
            check(!Capability.IsSupported(TiaFeature.HardwareHmiConnection, 20), "V20 hardware HMI gate unchanged");
            check(Capability.IsSupported(TiaFeature.HardwareHmiConnection, 21), "V21 hardware HMI gate unchanged");
            check(Capability.IsSupported(TiaFeature.DocumentExport, 20) && Capability.IsSupported(TiaFeature.DocumentExport, 21), "existing document export gates unchanged");
            check(!Capability.IsSupported((TiaFeature)999, 21), "unregistered feature fails closed");
            foreach (int detected in new[] { 14, 15, 16, 17, 18, 19, 22 })
            {
                int lookups = 0;
                var diagnostic = TiaMcpServer.Runtime.EnvironmentDoctor.EngineVersionMatch(21, detected, _ => { lookups++; return "fake.exe"; });
                check(!diagnostic.Ok && diagnostic.Gating && lookups == 0, "doctor denies unsupported version without searching sibling: " + detected);
                check(diagnostic.FixEn != null && !diagnostic.FixEn.Contains("runtime"), "doctor does not recommend nonexistent engine: " + detected);
            }
            check(TiaMcpServer.Runtime.EnvironmentDoctor.EngineVersionMatch(20, 20, _ => throw new Exception("must not look up matching engine")).Ok, "doctor accepts matching existing engine");
            check(!TiaMcpServer.Runtime.EnvironmentDoctor.EngineVersionMatch(21, 20, _ => null).Ok, "doctor reports missing V20 sibling");
            check(TiaMcpServer.Runtime.EnvironmentDoctor.EngineVersionMatch(21, 20, _ => "sibling.exe").Ok, "doctor preserves existing V20 sibling discovery");
            TiaMcpServer.Runtime.OpennessReadiness.MarkUnavailable(
                "TIA Portal V20 or its Openness API files were not found.",
                "Install TIA Portal V20 with the Openness option, or set TiaPortalLocation to its installation folder; run `tia doctor` for details.",
                "Install TIA Portal V20 with the Openness option, or set TiaPortalLocation to its installation folder; run `tia doctor` for details.");
            check(TiaMcpServer.Runtime.OpennessReadiness.FixZh?.Contains("请安装") == true
                && TiaMcpServer.Runtime.OpennessReadiness.FixZh?.Contains("Install TIA Portal") == false,
                "startup readiness normalizes English duplicate repair text to Chinese");
            TiaMcpServer.Runtime.OpennessReadiness.MarkReady(false);
            check(TiaMcpServer.Runtime.OpennessReadiness.FixZh?.Contains("请") == true
                && TiaMcpServer.Runtime.OpennessReadiness.FixZh != TiaMcpServer.Runtime.OpennessReadiness.FixEn,
                "V20/V21 Openness readiness keeps Chinese group repair guidance");
            check(TiaMcpServer.Runtime.OpennessReadiness.Guidance(true).Contains(TiaMcpServer.Runtime.OpennessReadiness.FixZh!),
                "Chinese readiness guidance uses the Chinese repair text");
            TiaMcpServer.Runtime.OpennessReadiness.MarkReady(true);
        }
    }
}
