using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private PlcSoftware ExactPlcForEngineering(string softwarePath, bool writing)
        {
            var plc = ResolveSoftwareContainerUncached(softwarePath)?.Software as PlcSoftware
                ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath);
            if (writing && ResolvePlcService<OnlineProvider>(softwarePath, plc)?.State.ToString() != "Offline")
                throw new PortalException(PortalErrorCode.InvalidState, "Confirmed Offline state is required.");
            return plc;
        }

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

        private object ExactOpenEngineeringLibrary(string libraryName)
            => string.IsNullOrEmpty(libraryName) ? _project!.ProjectLibrary
                : EngineeringGroupOperations.Find(_portal!.GlobalLibraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found. Open it in TIA first; no implicit open or close.");

        private object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath)
        {
            var sc = GetSoftwareContainer(hmiSoftwarePath);
            var software = sc?.Software;
            if (software == null)
            {
                throw new InvalidOperationException($"HMI software not found at '{hmiSoftwarePath}'.");
            }

            return software;
        }
    }
}
