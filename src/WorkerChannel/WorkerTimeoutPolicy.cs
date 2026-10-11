using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TiaMcp.WorkerChannel
{
    public sealed class WorkerTimeoutPolicy
    {
        // Data, rather than separate timeout decisions in the two host pipelines.
        public static readonly IReadOnlyDictionary<string, TimeSpan> Defaults = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["default"] = TimeSpan.FromSeconds(120), ["firstAttach"] = TimeSpan.FromMinutes(5),
            ["compile"] = TimeSpan.FromMinutes(30), ["transfer"] = TimeSpan.FromMinutes(30),
            ["importExport"] = TimeSpan.FromMinutes(30), ["archive"] = TimeSpan.FromMinutes(30),
            ["libraryUpdate"] = TimeSpan.FromMinutes(30)
        };
        // Exact PLC dispatcher operations. Coverage tests require a category for every registration.
        public static readonly IReadOnlyDictionary<string, string> PlcFamilies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AddDeviceWithFallback"] = "default",
            ["Attach"] = "default",
            ["BindProject"] = "default",
            ["CloseProject"] = "default",
            ["CompileCandidate"] = "compile",
            ["CompileSoftware"] = "compile",
            ["CreateHardwareDeviceCandidate"] = "default",
            ["CreateProject"] = "default",
            ["CreateTag"] = "default",
            ["CreateTagTable"] = "default",
            ["CreateUserConstant"] = "default",
            ["DeletePlcExternalSource"] = "default",
            ["Disconnect"] = "default",
            ["ExportAsDocuments"] = "importExport",
            ["ExportBlock"] = "importExport",
            ["ExportBlocks"] = "importExport",
            ["ExportBlocksAsDocuments"] = "importExport",
            ["ExportPlcCandidate"] = "importExport",
            ["ExportPlcWatchTable"] = "importExport",
            ["ExportTagTable"] = "importExport",
            ["ExportTechnologyObject"] = "importExport",
            ["ExportType"] = "importExport",
            ["ExportTypes"] = "importExport",
            ["GenerateBlocksFromExternalSource"] = "compile",
            ["ImportBlocks"] = "importExport",
            ["ImportBlocksFromDirectory"] = "importExport",
            ["ImportBlocksFromDocuments"] = "importExport",
            ["ImportFromDocuments"] = "importExport",
            ["ImportPlcCandidate"] = "importExport",
            ["ImportPlcExternalSource"] = "importExport",
            ["ImportPlcProgramFromDirectory"] = "importExport",
            ["ImportTagTables"] = "importExport",
            ["ImportTypes"] = "importExport",
            ["ListProjects"] = "default",
            ["ListSystemConstants"] = "default",
            ["ListTags"] = "default",
            ["ListUserConstants"] = "default",
            ["OpenProject"] = "default",
            ["PlanPlcExternalSourceImport"] = "default",
            ["ReadBlockHierarchy"] = "default",
            ["ReadBlockInfo"] = "default",
            ["ReadBlocks"] = "default",
            ["ReadExternalSourceNames"] = "default",
            ["ReadPortalConnectReadiness"] = "default",
            ["ReadPortalProcessProjects"] = "default",
            ["ReadProjectTree"] = "default",
            ["ReadSoftwareInfo"] = "default",
            ["ReadSoftwareTree"] = "default",
            ["ReadState"] = "default",
            ["ReadTagTableNames"] = "default",
            ["ReadTechnologyObjects"] = "default",
            ["ReadTypeInfo"] = "default",
            ["ReadTypes"] = "default",
            ["ReadWatchTableNames"] = "default",
            ["SaveCloseCandidate"] = "default",
            ["SaveProject"] = "default",
            ["SearchHardwareCatalog"] = "default",
            ["SessionCandidate"] = "default",
            ["SourceCandidate"] = "compile",
            ["engineering-services.BeginEngineeringTransaction"] = "default",
            ["engineering-services.CommitEngineeringTransaction"] = "default",
            ["engineering-services.DisposeEngineeringTransaction"] = "default",
            ["engineering-services.ExecuteEngineeringServices"] = "default",
            ["engineering-services.ReadEngineeringTransaction"] = "default",
            ["hardware-addressing.DescribeHardwareIoChildren"] = "default",
            ["hardware-addressing.ReadHardwareAddressing"] = "default",
            ["hardware-addressing.ReadHardwareIoAddresses"] = "default",
            ["hardware-addressing.ReadHardwareIpAddress"] = "default",
            ["hardware-addressing.SetHardwareIoAddress"] = "default",
            ["hardware-addressing.UpdateHardwareAddress"] = "default",
            ["hardware-aml.ExportHardwareAml"] = "importExport",
            ["hardware-aml.ImportHardwareAml"] = "importExport",
            ["hardware-devices.HardwareCreateCatalogDevice"] = "default",
            ["hardware-devices.HardwareCreateDevice"] = "default",
            ["hardware-devices.HardwareCreateDeviceFallback"] = "default",
            ["hardware-devices.HardwareCreateGsdDevice"] = "default",
            ["hardware-devices.HardwareDescribeDeviceAt"] = "default",
            ["hardware-devices.HardwareDescribeDevices"] = "default",
            ["hardware-devices.HardwareDescribeItemAt"] = "default",
            ["hardware-devices.HardwareDeviceCandidate"] = "default",
            ["hardware-devices.HardwareExchangeSystemDiagnosticsSettings"] = "default",
            ["hardware-devices.HardwareLegacyDumpDeviceAttributes"] = "default",
            ["hardware-devices.HardwareLegacySearchHardwareCatalog"] = "default",
            ["hardware-devices.HardwareLegacySearchInstalledGsdDevices"] = "default",
            ["hardware-devices.HardwareLegacySetCpuCommonSettings"] = "default",
            ["hardware-devices.HardwareLegacySetDeviceItemAttribute"] = "default",
            ["hardware-devices.HardwareManageCommunicationConnection"] = "default",
            ["hardware-devices.HardwareManageDeviceServiceObjects"] = "default",
            ["hardware-devices.HardwareManageDeviceUserGroup"] = "default",
            ["hardware-devices.HardwareManageHardwareObject"] = "default",
            ["hardware-devices.HardwareManageHardwareUtilities"] = "default",
            ["hardware-devices.HardwarePlugModule"] = "default",
            ["hardware-devices.HardwareReadCommunicationConnections"] = "default",
            ["hardware-devices.HardwareReadHardwareFeatures"] = "default",
            ["hardware-devices.HardwareReadItemTree"] = "default",
            ["hardware-devices.HardwareReadPlcNames"] = "default",
            ["hardware-devices.HardwareReadPlugLocations"] = "default",
            ["hardware-network.HardwareAttachDeviceNodeToSubnet"] = "default",
            ["hardware-network.HardwareEnsureSubnet"] = "default",
            ["hardware-network.HardwareGetDeviceItemNetworkInfo"] = "default",
            ["hardware-network.HardwareGetProjectTopology"] = "default",
            ["hardware-network.HardwareManageCommunicationConnection"] = "default",
            ["hardware-network.HardwareManageIoSystem"] = "default",
            ["hardware-network.HardwareManageNetworkDomain"] = "default",
            ["hardware-network.HardwareManagePortInterconnection"] = "default",
            ["hardware-network.HardwareManageTransferArea"] = "default",
            ["hardware-network.HardwareProbeConnectDeviceNodesToSubnet"] = "default",
            ["hardware-network.HardwareReadCommunicationConnections"] = "default",
            ["hardware-network.HardwareReadDeviceItemChannels"] = "default",
            ["hardware-network.HardwareReadIoSystems"] = "default",
            ["hardware-network.HardwareReadNetworkDomains"] = "default",
            ["hardware-network.HardwareReadTransferAreas"] = "default",
            ["hardware-network.HardwareUpdateDeviceItemChannel"] = "default",
            ["hardware-network.ReadHardwareOwnerCandidates"] = "default",
            ["hardware-network.ReadHardwareWhitelistedServices"] = "default",
            ["hmi-classic.ExecuteHmi"] = "default",
            ["hmi-generic.ExecuteHmi"] = "default",
            ["libraries.ExecuteLibrary"] = "default",
            ["openness-reflection.ExecuteOpennessReflection"] = "default",
            ["plc-alarms.ExecutePlcB5"] = "default",
            ["plc-analysis.ExportBlockDocument"] = "default",
            ["plc-compile.ExecutePlcOrganisation"] = "compile",
            ["plc-documents.ExecutePlcB5"] = "default",
            ["plc-integrity.ExecutePlcOrganisation"] = "default",
            ["plc-opcua.ExecutePlcB5"] = "default",
            ["plc-organisation.ExecutePlcOrganisation"] = "default",
            ["plc-software.ExecutePlcOrganisation"] = "default",
            ["plc-software.SeedReferenceHmi"] = "importExport",
            ["plc-sources.ExecutePlcOrganisation"] = "default",
            ["plc-technology.ExecutePlcOrganisation"] = "default",
            ["plc-watch.ExecutePlcOrganisation"] = "default",
        };
        public TimeSpan ForPlc(string operation, System.Text.Json.Nodes.JsonObject arguments, bool firstAttach = false)
            => budgets[firstAttach ? "firstAttach" : PlcFamily(operation, arguments)];
        public static string PlcFamily(string operation, System.Text.Json.Nodes.JsonObject arguments)
        {
            string? mode = (string?)arguments["mode"];
            var request = arguments["request"] as System.Text.Json.Nodes.JsonObject;
            if ((bool?)arguments["dryRun"] == true || (bool?)request?["DryRun"] == true || mode == "preview"
                || mode == null && operation.EndsWith("Candidate", StringComparison.Ordinal)) return "default";
            if (operation == "SourceCandidate")
            {
                var items = arguments["candidate"]?["Check"]?["Request"]?["Items"] as System.Text.Json.Nodes.JsonArray;
                if (items?.Any(item => (string?)item?["Action"] == "generate") == true) return "compile";
                return items?.Any(item => (string?)item?["Action"] == "import") == true ? "importExport" : "default";
            }
            if (request != null)
            {
                string name = (string?)request["Operation"] ?? operation;
                string action = (string?)request["Action"] ?? "";
                if (name == "CheckLibraryUpdates") return "default";
                if (name == "SynchronizeLibrary" || name == "ManageLibraryType" && action is "updateLibrary" or "updateProject"
                    || name == "ManageLibraryTypeVersion" && action == "updateInstances") return "libraryUpdate";
                if (name is "SeedProjectFromReference" or "GeneratePlcSourceFromBlocks") return "importExport";
                if (name == "GenerateOpcUaModelledInterface") return "compile";
                if (name == "ManagePlcExternalSources" && action == "generateBlocks") return "compile";
                string family = Family(name);
                return family != "default" ? family : Family(action);
            }
            return PlcFamilies.TryGetValue(operation, out var category) ? category : Family(operation);
        }
        private readonly Dictionary<string, TimeSpan> budgets;
        public WorkerTimeoutPolicy(int defaultSeconds = 120, string? configJson = null, string? environmentJson = null)
        {
            if (defaultSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(defaultSeconds));
            budgets = Defaults.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            budgets["default"] = TimeSpan.FromSeconds(defaultSeconds);
            Apply(configJson); Apply(environmentJson);
        }
        private void Apply(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using var document = JsonDocument.Parse(json!);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(entry.Name) || !budgets.ContainsKey(entry.Name) || !entry.Value.TryGetDouble(out var seconds)
                    || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > 86400)
                    throw new ArgumentException("Worker timeout overrides require known family names and seconds in (0, 86400].");
                budgets[entry.Name] = TimeSpan.FromSeconds(seconds);
            }
        }
        public IReadOnlyDictionary<string, TimeSpan> Budgets => budgets;
        public TimeSpan For(string operation, bool firstAttach = false)
            => budgets[firstAttach ? "firstAttach" : Family(operation)];
        public static string Family(string operation)
        {
            if (operation.IndexOf("Compile", StringComparison.OrdinalIgnoreCase) >= 0) return "compile";
            if (operation.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0) return "transfer";
            if (operation.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Export", StringComparison.OrdinalIgnoreCase) >= 0) return "importExport";
            if (operation.IndexOf("Archive", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Retrieve", StringComparison.OrdinalIgnoreCase) >= 0) return "archive";
            if (operation.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 && operation.IndexOf("Librar", StringComparison.OrdinalIgnoreCase) >= 0) return "libraryUpdate";
            return "default";
        }
    }
}
