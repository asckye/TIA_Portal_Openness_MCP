using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.Upload;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // 设备传输：在线可达设备扫描、站上载、参数上载、下载到 Windows 文件夹（存储卡镜像 / PLCSIM Advanced）。
    // 与 DownloadToPlc 共用 PG/PC 路由遍历和 DownloadPromptPolicy 提示应答；上载提示对象形态相同，可直接复用。
    public partial class Portal
    {
        private sealed class PcInterfacePick
        {
            public object Interface = null!;
            public string ModeName = "";
            public string Name = "";
            public int Number;
            public List<string> Addresses = new List<string>();
        }

        private static List<PcInterfacePick> EnumeratePcInterfaces(object? connectionConfiguration)
        {
            var list = new List<PcInterfacePick>();
            if (connectionConfiguration == null) return list;
            foreach (var mode in EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                foreach (var pcInterface in EnumerateReflectedProperty(mode, "PcInterfaces"))
                {
                    if (pcInterface == null) continue;
                    list.Add(new PcInterfacePick
                    {
                        Interface = pcInterface, ModeName = ReadReflectedString(mode, "Name"), Name = ReadReflectedString(pcInterface, "Name"),
                        Number = ReadReflectedInt(pcInterface, "Number"), Addresses = ReadConfigurationAddresses(pcInterface)
                    });
                }
            return list;
        }

        private static JsonArray DescribePcInterfaces(IEnumerable<PcInterfacePick> picks)
            => new JsonArray(picks.Select(p => (JsonNode)new JsonObject { ["mode"] = p.ModeName, ["name"] = p.Name, ["number"] = p.Number, ["addresses"] = string.Join(", ", p.Addresses) }).ToArray());

        // 精确按名称或编号选择 PG/PC 接口；只有一个时允许省略。绝不做子串猜测。
        private static PcInterfacePick PickPcInterface(List<PcInterfacePick> picks, string pgPcInterface)
        {
            if (picks.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC interface available in this connection configuration.");
            if (string.IsNullOrWhiteSpace(pgPcInterface))
            {
                if (picks.Count == 1) return picks[0];
                throw new ArgumentException("pgPcInterface is required because several PG/PC interfaces exist: " + string.Join("; ", picks.Select(p => $"{p.Name} (#{p.Number})")));
            }
            var matches = picks.Where(p => string.Equals(p.Name, pgPcInterface, StringComparison.Ordinal) || (int.TryParse(pgPcInterface, out var n) && p.Number == n)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "PG/PC interface not found (exact name or number required): " + pgPcInterface);
            throw new ArgumentException("pgPcInterface is ambiguous; use the interface number: " + string.Join("; ", matches.Select(p => $"{p.Name} (#{p.Number})")));
        }

        // 在所选 PG/PC 接口的 TargetInterfaces.Addresses 中精确匹配目标地址。扫描后 TIA 才会填充可达目标。
        private static ConfigurationAddress? FindTargetAddress(PcInterfacePick pick, string targetIpAddress, List<string> seen)
        {
            foreach (var target in EnumerateReflectedProperty(pick.Interface, "TargetInterfaces"))
                foreach (var address in EnumerateReflectedProperty(target, "Addresses"))
                {
                    var value = ReadReflectedString(address, "Address");
                    if (!string.IsNullOrWhiteSpace(value)) seen.Add(value);
                    if (string.Equals(value, targetIpAddress, StringComparison.OrdinalIgnoreCase) && address is ConfigurationAddress typed) return typed;
                }
            // 2.7.49: subnet / gateway addresses of the PC interface (where TIA lists configured CPU addresses)
            var viaSubnet = FindSubnetOrGatewayAddress(pick.Interface, targetIpAddress, out _);
            if (viaSubnet != null) return viaSubnet;
            return null;
        }

        // 2.7.49: an address that ScanAccessibleDevices reported on the same PG/PC interface (IP or MAC of a station TIA has not
        // seen in the route tree) is created on the first target interface - or, without target interfaces (project-level
        // StationUploadProvider), on the first subnet - through the official ConfigurationAddressComposition.Create.
        private static ConfigurationAddress? CreateScannedAddress(PcInterfacePick pick, string targetAddress, out string note)
        {
            note = "";
            if (pick.Interface is not ConfigurationPcInterface typed) { note = "PC interface is not typed"; return null; }
            var errors = new List<string>();
            try
            {
                var accessible = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                if (!accessible.Any(d => string.Equals(d.Address, targetAddress, StringComparison.OrdinalIgnoreCase) || string.Equals(d.MACAddress, targetAddress, StringComparison.OrdinalIgnoreCase)))
                { note = "address not reported by the network scan on this PG/PC interface"; return null; }
            }
            catch (Exception ex) { note = "network scan failed: " + ex.Message; return null; }
            foreach (ConfigurationTargetInterface target in EngineeringGroupOperations.Items(typed.TargetInterfaces).Cast<ConfigurationTargetInterface>())
            {
                try { var created = target.Addresses.Find(targetAddress) ?? target.Addresses.Create(targetAddress); note = "created on target interface " + target.Name; return created; }
                catch (Exception ex) { errors.Add(target.Name + ": " + ex.Message); }
            }
            foreach (ConfigurationSubnet subnet in EngineeringGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
            {
                try { var created = subnet.Addresses.Find(targetAddress) ?? subnet.Addresses.Create(targetAddress); note = "created on subnet " + subnet.Name; return created; }
                catch (Exception ex) { errors.Add("subnet " + subnet.Name + ": " + ex.Message); }
            }
            // 2.7.50 (real machine): the project-level StationUploadProvider lists the PC interface with neither target interfaces nor
            // subnets, so the last composition left is the PC interface's own ConfigurationPcInterface.Addresses.
            try { var created = typed.Addresses.Find(targetAddress) ?? typed.Addresses.Create(targetAddress); note = "created on PC interface " + typed.Name; return created; }
            catch (Exception ex) { errors.Add("PC interface " + typed.Name + ": " + ex.Message); }
            note = errors.Count == 0 ? "no target interface, subnet or PC interface composition to create the address on" : string.Join("; ", errors);
            return null;
        }

        private ConnectionConfiguration ResolveScanConfiguration(string softwarePath, JsonObject meta)
        {
            if (!string.IsNullOrWhiteSpace(softwarePath))
            {
                var plc = ExactPlcForEngineering(softwarePath, false);
                var provider = ResolvePlcService<DownloadProvider>(softwarePath, plc) ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                meta["configurationSource"] = "DownloadProvider:" + softwarePath;
                return provider.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "PLC has no connection configuration.");
            }
            var upload = _project!.GetService<StationUploadProvider>() ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version; pass softwarePath to scan through an existing PLC instead.");
            meta["configurationSource"] = "StationUploadProvider";
            return upload.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "StationUploadProvider has no connection configuration.");
        }

        public ResponseMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ScanAccessibleDevices", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and 1 <= limit <= 500 required.");
                var configuration = ResolveScanConfiguration(softwarePath, meta);
                var picks = EnumeratePcInterfaces(configuration);
                meta["pgPcInterfaces"] = DescribePcInterfaces(picks);
                var pick = PickPcInterface(picks, pgPcInterface);
                meta["pgPcInterface"] = new JsonObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                if (pick.Interface is not ConfigurationPcInterface typed) throw new NotSupportedException("Selected PG/PC interface is not a ConfigurationPcInterface.");
                var devices = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                meta["apiCallSuccess"] = true;
                var rows = devices.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries }).ToList();
                meta["total"] = rows.Count; meta["offset"] = offset; meta["limit"] = limit;
                meta["devices"] = new JsonArray(rows.Skip(offset).Take(limit).ToArray());
                meta["dataComplete"] = offset + limit >= rows.Count;
                return rows.Count == 0 ? "Network scan returned no accessible devices on the selected PG/PC interface." : $"{rows.Count} accessible device(s) found by a live network scan; no project change.";
            });

        public ResponseMessage UploadStationFromPlc(string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
            => RunHmiStepTool("UploadStationFromPlc", meta => {
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required (exact address as listed by ScanAccessibleDevices).");
                var policy = BuildDownloadPromptPolicy(true, true, false, false, "keep", promptAnswersJson, string.IsNullOrEmpty(password) ? null : password, null, null);
                var provider = _project!.GetService<StationUploadProvider>() ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version.");
                var picks = EnumeratePcInterfaces(provider.Configuration);
                var pick = PickPcInterface(picks, pgPcInterface);
                var seen = new List<string>();
                var address = FindTargetAddress(pick, targetIpAddress, seen);
                meta["pgPcInterface"] = new JsonObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                meta["knownTargetAddresses"] = string.Join(", ", seen.Distinct());
                string addressSource = "route tree";
                if (address == null) { address = CreateScannedAddress(pick, targetIpAddress, out addressSource); meta["addressCreation"] = addressSource; }
                // 2.7.51 (real machine, PLCSIM Advanced MCP_SIM seen only by MAC): ConfigurationAddressComposition.Create takes IP addresses
                // only ("'02-C0-A8-00-C8-00' does not specify a valid address"); the official page creates "192.68.0.1". A MAC from the scan is
                // therefore never a valid target, and a virtual PLC that has not been downloaded to (IP 0.0.0.0) cannot be uploaded from.
                if (address == null)
                    throw new PortalException(PortalErrorCode.NotFound, "Target address not present on the selected PG/PC interface and not creatable from the network scan (" + addressSource + "). "
                        + (LooksLikeMacAddress(targetIpAddress)
                            ? "'" + targetIpAddress + "' is a MAC address: ConfigurationAddressComposition.Create accepts IP addresses only, so pass the device's IP as listed by ScanAccessibleDevices (a PLC that shows only a MAC, e.g. a PLCSIM Advanced instance before its first download, has no IP yet - assign one by downloading first)."
                            : "Run ScanAccessibleDevices on the same interface first; only the exact IP address listed there is accepted."));
                meta["targetAddressSource"] = addressSource;
                meta["targetAddress"] = address.Address; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["devicesBefore"] = _project.Devices.Count;
                if (dryRun) return "Station upload preview: provider, PG/PC interface and target address resolved (" + addressSource + "); " + (addressSource == "route tree" ? "no PLC contact" : "only a DCP network scan was sent") + ", no project change.";
                if (!confirmUpload) throw new InvalidOperationException("Station upload adds a new device to the project from the live PLC; set confirmUpload=true to execute.");
                using var access = AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.StationUpload(address, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["uploadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()); meta["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray());
                var station = result?.UploadedStation;
                meta["uploadedStation"] = station?.Name; meta["devicesAfter"] = _project.Devices.Count;
                bool ok = result != null && result.State != UploadResultState.Error && station != null && _project.Devices.Any(d => d.Name == station.Name);
                meta["operationSuccess"] = ok; meta["dataComplete"] = false;
                if (!ok) throw new InvalidOperationException("Station upload did not yield a verified device: " + (result?.State.ToString() ?? "no result") + policy.UnansweredSummary());
                return $"Station uploaded as device '{station!.Name}' and verified in the project; not saved, compiled or downloaded." + policy.UnansweredSummary();
            });

        private static bool LooksLikeMacAddress(string value)
            => System.Text.RegularExpressions.Regex.IsMatch((value ?? "").Trim(), "^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$");

        public ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
            => RunHmiStepTool("UploadDeviceParameters", meta => {
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required.");
                var policy = BuildDownloadPromptPolicy(true, true, false, false, "keep", promptAnswersJson, string.IsNullOrEmpty(password) ? null : password, null, null);
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                // ParameterUploadProvider 只在 V21 PublicAPI 中存在；按名解析并经泛型 GetService 反射取得，V20 明确 NotSupported。
                var providerType = typeof(StationUploadProvider).Assembly.GetType("Siemens.Engineering.Upload.ParameterUploadProvider")
                    ?? throw new NotSupportedException("ParameterUploadProvider is not part of this TIA version's PublicAPI (V21 only).");
                if (owner is not IEngineeringServiceProvider serviceProvider) throw new NotSupportedException("Hardware object is not a service provider.");
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(providerType);
                var provider = getService.Invoke(serviceProvider, null) ?? throw new NotSupportedException("ParameterUploadProvider service unavailable on this hardware object.");
                var uploadMethod = providerType.GetMethod("ParameterUpload", new[] { typeof(IConfiguration), typeof(ConfigurationAddress), typeof(UploadConfigurationDelegate) })
                    ?? throw new NotSupportedException("ParameterUpload(IConfiguration, ConfigurationAddress, delegate) unavailable.");
                var providerConfiguration = providerType.GetProperty("Configuration")?.GetValue(provider);
                var route = SelectDownloadRoute(providerConfiguration, string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface, targetIpAddress);
                if (route.Error != null) throw new PortalException(PortalErrorCode.NotFound, route.Error);
                if (route.Configuration == null) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC route to the target address; run ScanAccessibleDevices first.");
                ConfigurationAddress? address = route.Address;   // 2.7.49: target interface, subnet / gateway or created address
                foreach (var candidate in EnumerateReflectedProperty(route.Target ?? route.Configuration, "Addresses"))
                    if (address == null && string.Equals(ReadReflectedString(candidate, "Address"), targetIpAddress, StringComparison.OrdinalIgnoreCase) && candidate is ConfigurationAddress typed) address = typed;
                if (address == null) throw new PortalException(PortalErrorCode.NotFound, "Exact target address not found on the selected route: " + route.Description);
                meta["route"] = route.Description; meta["targetAddress"] = address.Address; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = EngineeringScalarProperties.Read(owner);
                if (dryRun) return "Parameter upload preview: provider, route and address resolved; no PLC contact, no project change.";
                if (!confirmUpload) throw new InvalidOperationException("Parameter upload overwrites offline hardware parameters from the live device; set confirmUpload=true to execute.");
                using var access = AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                if ((route.Target ?? route.Configuration) is not IConfiguration configuration) throw new NotSupportedException("Selected route is not an IConfiguration.");
                try { uploadMethod.Invoke(provider, new object[] { configuration, address, handler }); }
                catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                meta["after"] = EngineeringScalarProperties.Read(owner); meta["dataComplete"] = false;
                return "Parameter upload completed; compare before/after scalars for changes. Not saved, compiled or downloaded." + policy.UnansweredSummary();
            });

        public ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware = "CPU", bool overwriteOnMemoryCard = false,
            bool keepActualValues = true, string userManagementMode = "keep", string promptAnswersJson = "{}", bool confirmDownload = false, bool dryRun = true)
            => RunHmiStepTool("DownloadPlcToFolder", meta => {
                if (!new[] { "CPU", "PlcSimulationAdvanced" }.Contains(targetForSoftware)) throw new ArgumentException("targetForSoftware must be CPU or PlcSimulationAdvanced.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                var directory = new DirectoryInfo(destinationDirectory);
                if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) throw new IOException("destinationDirectory must be new or empty; merging into existing content is refused.");
                var policy = BuildDownloadPromptPolicy(true, keepActualValues, false, false, userManagementMode, promptAnswersJson, null, null, null);
                policy.Explicit["TargetForSoftware"] = targetForSoftware;
                policy.Explicit["OverwriteOnMemoryCard"] = overwriteOnMemoryCard ? "Load" : "NoAction";
                var plc = ExactPlcForEngineering(softwarePath, false);
                var provider = ResolvePlcService<DownloadProvider>(softwarePath, plc) ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                if (provider.GetType().GetMethod("Download", new[] { typeof(DirectoryInfo), typeof(DownloadConfigurationDelegate) }) == null) throw new NotSupportedException("Download(DirectoryInfo, delegate) unavailable on this version.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName; meta["targetForSoftware"] = targetForSoftware; meta["overwriteOnMemoryCard"] = overwriteOnMemoryCard;
                if (dryRun) return "Folder download preview: PLC, provider and destination validated; no files written.";
                if (!confirmDownload) throw new InvalidOperationException("Writing a memory-card image is an explicit action; set confirmDownload=true to execute.");
                if (!directory.Exists) directory.Create();
                meta["mayHaveWrittenFiles"] = true;
                DownloadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.Download(directory, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value?.DeepClone();
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["downloadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()); meta["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray());
                directory.Refresh();
                var files = directory.Exists ? directory.GetFiles("*", SearchOption.AllDirectories) : Array.Empty<FileInfo>();
                meta["filesWritten"] = files.Length; meta["bytesWritten"] = files.Sum(f => f.Length); meta["dataComplete"] = false;
                bool ok = result != null && result.State != DownloadResultState.Error && files.Length > 0;
                meta["operationSuccess"] = ok;
                if (!ok) throw new InvalidOperationException("Folder download did not produce a verified image: " + (result?.State.ToString() ?? "no result") + policy.UnansweredSummary());
                return $"Memory-card image written: {files.Length} file(s) in {directory.FullName}. Content semantics not verified; no PLC contacted." + policy.UnansweredSummary();
            });
    }
}
