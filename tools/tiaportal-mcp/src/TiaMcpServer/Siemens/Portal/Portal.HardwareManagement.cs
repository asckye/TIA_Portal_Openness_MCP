using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // JSON segments preserve legal '/' in station names. Never use CPU-name aliases for deletion.
        private Device ExactEngineeringDevice(string pathJson)
        {
            var names = JsonNode.Parse(pathJson)?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
                ?? throw new ArgumentException("devicePathJson must be an array.");
            if (names.Length < 1 || names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Device path needs 1-64 exact nonempty names.");
            if (names.Length == 1)
                return (Device)(EngineeringGroupOperations.Find(EnumerateAllDevices().ToArray(), names[0])
                    ?? throw new InvalidOperationException("Exact unique device not found."));
            var group = EngineeringGroupOperations.Find(_project!.DeviceGroups, names[0]) as DeviceUserGroup
                ?? throw new InvalidOperationException("Device group not found: " + names[0]);
            foreach (var name in names.Skip(1).Take(names.Length - 2))
                group = (DeviceUserGroup)(EngineeringGroupOperations.Find(group.Groups, name) ?? throw new InvalidOperationException("Device group not found: " + name));
            return (Device)(EngineeringGroupOperations.Find(group.Devices, names.Last()) ?? throw new InvalidOperationException("Device not found."));
        }
        private HardwareObject ExactEngineeringHardware(string devicePathJson, string itemPathJson)
        {
            HardwareObject current = ExactEngineeringDevice(devicePathJson);
            var names = JsonNode.Parse(itemPathJson)?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
                ?? throw new ArgumentException("itemPathJson must be an array.");
            if (names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid item path.");
            foreach (var name in names)
            {
                // 2.7.46: fall back to the hardware-component association (Items) - a Comfort panel's head item reaches its
                // IE_CP_1 only that way, an S7-1500 rail reaches its plugged modules only that way (real project).
                var next = EngineeringGroupOperations.Find(current.DeviceItems, name)
                    ?? current.Items.Cast<object?>().FirstOrDefault(i => i is DeviceItem d && string.Equals(d.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Device item not found: " + name + " (children of " + current.Name + ": " + string.Join(", ", current.DeviceItems.Select(d => d.Name).Concat(current.Items.OfType<DeviceItem>().Select(d => d.Name)).Distinct().Take(20)) + ").");
                current = (DeviceItem)next;
            }
            return current;
        }
        public ResponseMessage ManageHardwareObject(string devicePathJson, string action, string itemPathJson = "[]",
            string destinationDevicePathJson = "[]", string destinationItemPathJson = "[]", int position = -1, bool dryRun = true)
            => RunHmiStepTool("ManageHardwareObject", meta => {
                if (!new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem" }.Contains(action)) throw new ArgumentException("Invalid hardware action.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var source = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["source"] = EngineeringScalarProperties.Read(source); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["dependencyImpact"] = "Hardware descendants and dependent software/configuration can be affected. No online action or download is performed.";
                if (action == "deleteDevice")
                {
                    if (source is not Device device) throw new ArgumentException("deleteDevice requires empty itemPathJson.");
                    if (!dryRun)
                    {
                        meta["mayHaveChanged"] = true; device.Delete();
                        try { ExactEngineeringDevice(devicePathJson); throw new InvalidOperationException("Device is still present after Delete."); }
                        catch (InvalidOperationException ex) when (ex.Message == "Exact unique device not found." || ex.Message == "Device not found.") { meta["verifiedAbsent"] = true; }
                    }
                }
                else
                {
                    if (source is not DeviceItem item) throw new ArgumentException("This operation requires a nonempty exact device item path.");
                    if (action == "deleteItem")
                    {
                        var parent = item.Parent as HardwareObject ?? throw new InvalidOperationException("Hardware parent unavailable.");
                        var name = item.Name;
                        if (!dryRun) { meta["mayHaveChanged"] = true; item.Delete(); if (EngineeringGroupOperations.Find(parent.DeviceItems, name) != null) throw new InvalidOperationException("Item remains after Delete."); meta["verifiedAbsent"] = true; }
                    }
                    else
                    {
                        if (position < 0) throw new ArgumentException("A nonnegative destination slot position is required.");
                        var destination = ExactEngineeringHardware(destinationDevicePathJson, destinationItemPathJson);
                        bool allowed = action == "moveItem" ? destination.CanPlugMove(item, position) : destination.CanPlugCopy(item, position);
                        meta["canPlug"] = allowed; meta["position"] = position;
                        if (!allowed) throw new InvalidOperationException("TIA CanPlugMove/CanPlugCopy refused the destination.");
                        if (!dryRun)
                        {
                            meta["mayHaveChanged"] = true;
                            var result = action == "moveItem" ? destination.PlugMove(item, position) : destination.PlugCopy(item, position);
                            meta["after"] = EngineeringScalarProperties.Read(result);
                        }
                    }
                }
                return dryRun ? "Hardware preview; no modification." : "Hardware operation completed; project not saved or downloaded.";
            });
    }
}
