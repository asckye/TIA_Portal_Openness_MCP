using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class HardwareAddressingWire
{
    internal static void Validate(string operation, JsonNode? reply)
    {
        bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);
        bool Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
        bool Address(JsonNode? node) => node is JsonObject row && Text(row["IoType"])
            && row["StartAddress"] is JsonValue start && start.TryGetValue<int>(out var offset) && offset >= 0
            && row["Length"] is JsonValue length && length.TryGetValue<int>(out var size) && size >= 0;
        bool valid = operation switch {
            "hardware-addressing.ReadHardwareIoAddresses" => reply == null || reply is JsonArray rows && rows.All(Address),
            "hardware-addressing.DescribeHardwareIoChildren" => reply is JsonArray children && children.All(Text),
            "hardware-addressing.ReadHardwareIpAddress" => reply is JsonObject ip && Flag(ip["Found"]) && ip["Nodes"] is JsonArray nodes
                && nodes.All(n => n is JsonObject && Text(n["NodeName"]) && Text(n["Address"]) && Flag(n["IsIndustrialEthernet"])),
            "hardware-addressing.SetHardwareIoAddress" => reply is JsonObject io && Flag(io["Ok"]) && Text(io["Message"])
                && Flag(io["RequiresSessionReset"]) && (io["Before"] == null || Address(io["Before"])) && (io["After"] == null || Address(io["After"]))
                && (!(bool)io["Ok"]! || io["Before"] != null && io["After"] != null)
                && ((bool)io["RequiresSessionReset"]! == (io["Before"] != null && io["After"] == null)),
            "hardware-addressing.ReadHardwareAddressing" or "hardware-addressing.UpdateHardwareAddress" => reply is JsonObject step
                && Text(step["Message"]) && Flag(step["RequiresSessionReset"]) && step["Meta"] is JsonObject meta
                && Text(meta["tool"]) && (string?)meta["tool"] == (operation.EndsWith("ReadHardwareAddressing", StringComparison.Ordinal) ? "GetDeviceAddressing" : "SetDeviceAddress")
                && Flag(meta["success"]),
            _ => false
        };
        if (!valid) throw new IOException("Malformed hardware-addressing worker reply; session stopped.");
    }
}
