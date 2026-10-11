using System;
using System.Linq;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters.Native.Security;
public sealed class PutGetAccess
{
    private readonly PlcFoundationEngine engine;
    internal PutGetAccess(PlcFoundationEngine engine) => this.engine = engine;
    internal Device? GetDevice(string path) => engine.ResolveEngineeringDevice != null ? engine.ResolveEngineeringDevice(path) as Device : engine.SecurityLegacyDevice(path);
        public static string NormalizeAttrName(string? n)
            => new string((n ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Locate the CPU DeviceItem + attribute controlling "Permit access with PUT/GET communication".
        // The exact Openness attribute name varies by CPU/firmware, so match any attribute whose
        // normalized (lowercase, alphanumeric-only) name contains "putget".
        // NOTE: S7-1200 (e.g. 1211C V4.6) does not expose this attribute via Openness at all — neither
        // enumerated by GetAttributeInfos() nor gettable by name — so this returns (null, null) there.
        // Use DumpDeviceAttributes to see exactly what a given CPU exposes.
        public static (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device)
        {
            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in PlcFoundationEngine.HardwareTraverseDeviceItems(root, root.Name))
                {
                    var it = tup.Item1;
                    string[] names;
                    try { names = it.GetAttributeInfos().Select(x => x.Name ?? string.Empty).ToArray(); }
                    catch { /* swallow(enumerate-optional): Skip device items whose attribute inventory is unavailable while locating PUT/GET support. */ continue; }
                    var match = names.FirstOrDefault(n => NormalizeAttrName(n).Contains("putget"));
                    if (!string.IsNullOrEmpty(match)) return (it, match);
                }
            }
            return (null, null);
        }

        internal static bool AttrValueIsEnabled(object? v)
            => v is bool b ? b : (v?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false);

        // Read whether a CPU permits remote PUT/GET access (precondition for ReadPlcLiveValuesS7 on DB areas).
        internal HmiObject GetPutGetAccess(string devicePath)
        {
            if (engine.PlcB5Session.IsProjectNull()) return new HmiObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new HmiObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var (item, attrName) = FindPutGetAttribute(device);
            if (item == null || attrName == null)
                return new HmiObject
                {
                    ["found"] = false,
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access is not exposed as an Openness attribute on this CPU/firmware " +
                                  "(confirmed e.g. on S7-1200 1211C V4.6). Check/set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication'. " +
                                  "Note: M/I/Q S7 reads still work when the channel is permitted; a successful M-area read implies PUT/GET is enabled."
                };

            object? val = null;
            try { val = item.GetAttribute(attrName); } catch { /* swallow(probe-optional): An unavailable PUT/GET value leaves the existing null readback for this CPU. */ }
            return new HmiObject
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["enabled"] = AttrValueIsEnabled(val),
                ["rawValue"] = val?.ToString() ?? string.Empty
            };
        }

}
