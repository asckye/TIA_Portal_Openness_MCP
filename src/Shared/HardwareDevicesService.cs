using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareDevicesService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal HardwareDevicesService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        internal bool HasProject => hasProject();
        private JsonNode? Invoke(string operation, JsonObject arguments, bool write)
        {
            if (write) { arguments["dryRun"] = false; arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
            return call(operation, arguments);
        }
        private static ResponseMessage Step(JsonNode? reply)
        {
            if (reply is not JsonObject result || result["Meta"] is not JsonObject meta || result["Message"] is not JsonValue message)
                throw new InvalidOperationException("Missing hardware network step reply.");
            return new ResponseMessage { Message = message.GetValue<string>(), Meta = (JsonObject)meta.DeepClone() };
        }

        internal sealed class DeviceInfo
        { public string Name { get; set; } = ""; public string? Description { get; set; } public List<TiaMcpServer.ModelContextProtocol.Attribute> Attributes { get; set; } = new(); public override string ToString() => Description ?? ""; }
        internal sealed class GsdProbe
        { public DeviceInfo? Device { get; set; } public GsdDeviceCandidate? Candidate { get; set; } public List<GsdDeviceCandidate> Candidates { get; set; } = new(); public List<string> Attempts { get; set; } = new(); public string? Error { get; set; } }
        internal sealed class CatalogProbe
        { public DeviceInfo? Device { get; set; } public HardwareCatalogCandidate? Candidate { get; set; } public List<HardwareCatalogCandidate> Candidates { get; set; } = new(); public List<string> Attempts { get; set; } = new(); public string? Error { get; set; } }
        internal sealed class FallbackProbe
        { public DeviceInfo? Device { get; set; } public string? MlfbUsed { get; set; } public string? VersionUsed { get; set; } public List<string> Attempts { get; set; } = new(); public string? Error { get; set; } }
        private T? Read<T>(string operation, JsonObject arguments, bool write = false)
        {
            var reply = Invoke("hardware-devices." + operation, arguments, write);
            return reply == null ? default : JsonSerializer.Deserialize<T>(reply.ToJsonString());
        }
        public DeviceInfo? GetDevice(string devicePath) => Read<DeviceInfo>("HardwareDescribeDeviceAt", new() { ["path"] = devicePath });
        public DeviceInfo? GetDeviceItem(string deviceItemPath) => Read<DeviceInfo>("HardwareDescribeItemAt", new() { ["path"] = deviceItemPath });
        public List<DeviceInfo> GetDevices(string regexName = "") => Read<List<DeviceInfo>>("HardwareDescribeDevices", new() { ["regexName"] = regexName })!;
        public string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4) => Read<string>("HardwareReadItemTree", new() { ["deviceItemPath"] = deviceItemPath, ["maxDepth"] = maxDepth })!;
        public string[] GetPlcSoftwareNamesForDesktop() => Read<string[]>("HardwareReadPlcNames", new())!;
        public DeviceInfo AddDevice(string orderNumber, string version, string deviceName) => new() { Name = Read<string>("HardwareCreateDevice", new() { ["orderNumber"] = orderNumber, ["version"] = version, ["deviceName"] = deviceName }, true)! };
        public List<GsdDeviceCandidate> SearchInstalledGsdDevices(string keyword, int limit = 50) => Read<List<GsdDeviceCandidate>>("HardwareLegacySearchInstalledGsdDevices", new() { ["keyword"] = keyword, ["limit"] = limit })!;
        public List<HardwareCatalogCandidate> SearchHardwareCatalog(string keyword, int limit = 50) => Read<List<HardwareCatalogCandidate>>("HardwareLegacySearchHardwareCatalog", new() { ["keyword"] = keyword, ["limit"] = limit })!;
        public GsdProbe AddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "") => Read<GsdProbe>("HardwareCreateGsdDevice", new() { ["keyword"] = keyword, ["deviceName"] = deviceName, ["preferredDap"] = preferredDap }, true)!;
        public CatalogProbe AddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "") => Read<CatalogProbe>("HardwareCreateCatalogDevice", new() { ["keyword"] = keyword, ["deviceName"] = deviceName, ["preferredText"] = preferredText }, true)!;
        public FallbackProbe AddDeviceWithFallback(string preferredMlfb, string preferredVersion, string deviceName, string family)
            => Read<FallbackProbe>("HardwareCreateDeviceFallback", new() { ["preferredMlfb"] = preferredMlfb, ["preferredVersion"] = preferredVersion, ["deviceName"] = deviceName, ["family"] = family }, true)!;
        public ResponseMessage SetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
            => Step(Invoke("hardware-devices.HardwareLegacySetDeviceItemAttribute", new() { ["deviceItemPath"] = deviceItemPath, ["attributeName"] = attributeName, ["value"] = value }, true));
        public ResponseMessage SetCpuCommonSettings(string cpuPath, string settingsJson)
            => Step(Invoke("hardware-devices.HardwareLegacySetCpuCommonSettings", new() { ["cpuPath"] = cpuPath, ["settingsJson"] = JsonSerializer.SerializeToNode(HardwareAddressingService.Scalars(JsonNode.Parse(settingsJson)!["exactAttributes"]!.ToJsonString())) }, true));
        public JsonObject DumpDeviceAttributes(string devicePath, string? nameFilter = null, int maxItems = 500)
            => (JsonObject)Invoke("hardware-devices.HardwareLegacyDumpDeviceAttributes", new() { ["devicePath"] = devicePath, ["nameFilter"] = nameFilter, ["maxItems"] = maxItems }, false)!;
    }
}
