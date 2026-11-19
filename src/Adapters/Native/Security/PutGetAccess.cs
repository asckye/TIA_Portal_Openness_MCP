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
    internal Device? GetDevice(string path) => engine.ResolveEngineeringDevice != null ? engine.ResolveEngineeringDevice(path) as Device : engine
        .SecurityLegacyDevice(path);
    // Exact names only. Enumeration failures make uniqueness unknowable and must refuse.
    public static (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device, string[]? itemPath = null)
    {
        if (SecurityAttributePolicy.PutGetNames.Count == 0) throw new NotSupportedException(SecurityAttributePolicy.Unverified);
        IEnumerable<DeviceItem> owners;
        if (itemPath != null && itemPath.Length > 0)
        {
            DeviceItem? owner = null;
            foreach (string segment in itemPath)
            {
                var children = owner == null ? device.DeviceItems : owner.DeviceItems;
                owner = children.SingleOrDefault(item => item.Name == segment);
                if (owner == null) throw AdapterPreconditionException.NotFound("PUT/GET owner item was not found.", "itemPath");
            }
            owners = new[] { owner! };
        }
        else owners = device.DeviceItems.SelectMany(root => PlcFoundationEngine.HardwareTraverseDeviceItems(root, root.Name).Select(row => row.Item1));

        var candidates = owners.Distinct().SelectMany(item => item.GetAttributeInfos()
            .Where(info => SecurityAttributePolicy.PutGetNames.Contains(info.Name, StringComparer.Ordinal))
            .Select(info => (Item: item, Name: info.Name))).Distinct().ToArray();
        if (candidates.Length > 1)
        {
            var cpus = candidates.Where(row => row.Item.Classification == DeviceItemClassifications.CPU).ToArray();
            if (cpus.Length != 0) candidates = cpus;
        }
        int selected = SecurityAttributePolicy.SelectExact(candidates.Select(row => row.Name), SecurityAttributePolicy.PutGetNames);
        return candidates[selected];
    }

    internal static void RequireWritable(DeviceItem item, string attribute, object? before)
    {
        var mode = item.GetAttributeInfos().Where(info => info.Name == attribute)
            .Select(info => info.AccessMode.ToString()).Distinct(StringComparer.Ordinal).Single();
        SecurityAttributePolicy.RequireBooleanWrite(mode, before);
    }

        internal static bool AttrValueIsEnabled(object? v)
            => v is bool b ? b : (v?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false);

        // Read whether a CPU permits remote PUT/GET access (precondition for ReadPlcLiveValuesS7 on DB areas).
        internal HmiObject GetPutGetAccess(string devicePath, string[]? itemPath = null)
        {
            if (engine.PlcB5Session.IsProjectNull()) return new HmiObject {
                ["found"] = false, ["failureCode"] = "PROJECT_NOT_BOUND", ["mayHaveChanged"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new HmiObject {
                ["found"] = false, ["failureCode"] = "NOT_FOUND", ["mayHaveChanged"] = false,
                ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            DeviceItem? item;
            string? attrName;
            try { (item, attrName) = FindPutGetAttribute(device, itemPath); }
            catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
            {
                string message = error.Message + (error is NotSupportedException
                    ? " Check or set it in TIA: CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication'." : "");
                return new HmiObject { ["found"] = false, ["failureCode"] = error is AdapterPreconditionException
                        ? MutationEvidence.FailureCode(error) : "UNSUPPORTED_CAPABILITY", ["mayHaveChanged"] = false,
                    ["device"] = device.Name, ["message"] = message, ["nativeMessage"] = message };
            }
            if (item == null || attrName == null)
                return new HmiObject
                {
                    ["found"] = false,
                    ["failureCode"] = "UNSUPPORTED_CAPABILITY",
                    ["mayHaveChanged"] = false,
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
