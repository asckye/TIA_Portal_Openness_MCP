using System;
using System.Linq;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters.Native.Security;
public sealed class PutGetAccess
{
    private readonly PlcFoundationEngine engine;
    internal PutGetAccess(PlcFoundationEngine engine) => this.engine = engine;
    internal Device? GetDevice(string path) => engine.ResolveEngineeringDevice != null ? engine.ResolveEngineeringDevice(path) as Device : engine.SecurityLegacyDevice(path);
    // Exact names only. Enumeration failures make uniqueness unknowable and must refuse.
    public static (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device)
    {
        if (SecurityAttributePolicy.PutGetNames.Count == 0) throw new NotSupportedException(SecurityAttributePolicy.Unverified);
        var candidates = new List<(DeviceItem Item, string Name)>();
        foreach (var root in device.DeviceItems)
            foreach (var row in PlcFoundationEngine.HardwareTraverseDeviceItems(root, root.Name))
                foreach (var info in row.Item1.GetAttributeInfos()) candidates.Add((row.Item1, info.Name));
        int selected = SecurityAttributePolicy.SelectExact(candidates.Select(row => row.Name), SecurityAttributePolicy.PutGetNames);
        return candidates[selected];
    }

        internal static bool AttrValueIsEnabled(object? v)
            => v is bool b ? b : (v?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false);

        // Read whether a CPU permits remote PUT/GET access (precondition for ReadPlcLiveValuesS7 on DB areas).
        internal HmiObject GetPutGetAccess(string devicePath)
        {
            if (engine.PlcB5Session.IsProjectNull()) return new HmiObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new HmiObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            DeviceItem? item;
            string? attrName;
            try { (item, attrName) = FindPutGetAttribute(device); }
            catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
            {
                return new HmiObject { ["found"] = false, ["device"] = device.Name, ["message"] = error.Message };
            }
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
