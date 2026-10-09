using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class HardwareFamilyWire
{
    private static bool Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);
    private static bool Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out _);
    private static bool Description(JsonNode? node) => node == null || node is JsonObject row
        && Text(row["Name"]) && row["Attributes"] is JsonArray attributes
        && attributes.All(a => a is JsonObject && Text(a["Name"]));

    internal static void Validate(string operation, JsonNode? reply)
    {
        string name = operation[(operation.LastIndexOf('.') + 1)..];
        bool valid = name switch {
            "HardwareDescribeDeviceAt" or "HardwareDescribeItemAt" => Description(reply),
            "HardwareDescribeDevices" => reply is JsonArray devices && devices.All(d => d != null && Description(d)),
            "HardwareReadItemTree" or "HardwareCreateDevice" or "HardwareProbeConnectDeviceNodesToSubnet" => Text(reply),
            "HardwareReadPlcNames" => reply is JsonArray names && names.All(Text),
            "HardwareLegacySearchInstalledGsdDevices" or "HardwareLegacySearchHardwareCatalog" => reply is JsonArray catalog
                && catalog.All(c => c is JsonObject && Text(c["TypeIdentifier"])),
            "HardwareCreateGsdDevice" or "HardwareCreateCatalogDevice" or "HardwareCreateDeviceFallback" => reply is JsonObject created
                && Description(created["Device"]) && created["Attempts"] is JsonArray attempts && attempts.All(Text),
            "HardwareLegacyDumpDeviceAttributes" => reply is JsonObject attributes && Flag(attributes["found"]),
            "HardwareReadPlugLocations" => reply == null || reply is JsonObject slots && slots["free"] is JsonArray
                && slots["occupied"] is JsonArray,
            "HardwarePlugModule" => reply is JsonObject module && Flag(module["Ok"]) && Text(module["Message"])
                && Flag(module["MayHaveChanged"]) && Flag(module["RequiresSessionReset"])
                && (!(bool)module["RequiresSessionReset"]! || (bool)module["MayHaveChanged"]!),
            "HardwareGetDeviceItemNetworkInfo" => reply == null || reply is JsonArray network
                && network.All(a => a is JsonObject && Text(a["Name"])),
            "HardwareGetProjectTopology" => reply is JsonObject topology && (Text(topology["failureCode"])
                || Number(topology["deviceCount"]) && topology["devices"] is JsonArray),
            "ReadHardwareOwnerCandidates" or "ReadHardwareWhitelistedServices" => reply is JsonObject probe
                && probe["Lines"] is JsonArray lines && lines.All(Text),
            "ExportHardwareAml" => reply is JsonObject export && Flag(export["Success"]) && Text(export["State"])
                && Text(export["FilePath"]) && Number(export["ErrorCount"]) && Number(export["WarningCount"]),
            _ => reply is JsonObject step && Text(step["Message"]) && Flag(step["RequiresSessionReset"])
                && step["Meta"] is JsonObject meta && Flag(meta["success"])
        };
        if (!valid) throw new IOException("Malformed hardware worker reply; session stopped: " + operation);
    }
}
