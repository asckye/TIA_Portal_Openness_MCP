using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hardware;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // Path resolution and enumeration share one walk over the three device roots (ProjectDeviceWalk).
        private Device? HardwareLegacyDevice(string devicePath)
            => project?.Devices == null ? null : ProjectDeviceWalk.FindDevice(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, devicePath);

        // Every Device in the project: root devices, nested user groups and the ungrouped system group.
        private IEnumerable<Device> EnumerateAllDevices()
            => project?.Devices == null ? Enumerable.Empty<Device>() : ProjectDeviceWalk.Devices(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup);

        private DeviceItem? HardwareLegacyItem(string deviceItemPath)
            => project?.Devices == null ? null : ProjectDeviceWalk.FindItem(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, deviceItemPath);

        private static IEnumerable<NetworkNodeInfo> FindNetworkNodes(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name, new HashSet<DeviceItem>()))
            {
                object? networkInterface = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch /* swallow(probe-optional): device items without a network service are skipped */ { }
                if (networkInterface == null) continue;

                var nodes = TryGetPropertyValue(networkInterface, "Nodes");
                if (nodes is not IEnumerable enumerable || nodes is string) continue;
                foreach (var node in enumerable)
                {
                    if (node != null) yield return new NetworkNodeInfo(item.Path, item.DeviceItem, networkInterface, node);
                }
            }
        }
        private static IEnumerable<(object Object, DeviceItem DeviceItem, string Path, string Kind)> TraverseDeviceItemsAndHardware(DeviceItem root, string path, HashSet<DeviceItem> visited)
        {
            if (!visited.Add(root)) yield break;
            yield return (root, root, path, "DeviceItem");

            var hardwareItems = new List<DeviceItem>();
            foreach (var hardware in root.Items)
            {
                if (hardware == null) continue;
                yield return (hardware, root, path + "#" + (TryGetName(hardware) ?? hardware.GetType().Name), "HardwareComponent");
                if (hardware is DeviceItem hardwareItem) hardwareItems.Add(hardwareItem);
            }

            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItemsAndHardware(child, path + "/" + child.Name, visited))
                {
                    yield return nested;
                }
            }

            foreach (var hardwareItem in hardwareItems)
            {
                foreach (var nested in TraverseDeviceItemsAndHardware(hardwareItem, path + "/" + hardwareItem.Name, visited))
                {
                    yield return nested;
                }
            }
        }

        private static object? TryGetNetworkInterfaceService(object target)
        {
            try
            {
                var mi = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;
                return mi.MakeGenericMethod(typeof(NetworkInterface)).Invoke(target, null);
            }
            catch /* swallow(probe-optional): unsupported NetworkInterface service lookup returns no service */
            {
                return null;
            }
        }

        private static object? TryGetNetworkInterfaceFromNetworkPort(object target)
        {
            try
            {
                var port = TryGetServiceByTypeSuffix(target, "NetworkPort");
                if (port == null) return null;
                return TryGetPropertyValue(port, "Interface");
            }
            catch /* swallow(probe-optional): an unavailable port interface returns no network service */
            {
                return null;
            }
        }
        private static bool IsIndustrialEthernetNode(object? node)
        {
            if (node == null) return false;
            var type = TryGetPropertyValue(node, "NodeType")?.ToString() ?? "";
            var name = TryGetName(node) ?? "";
            return type.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
                   || type.IndexOf("Profinet", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("PROFINET", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class NetworkNodeInfo
        {
            internal NetworkNodeInfo() { Path = ""; Item = null!; NetworkInterface = null!; Node = null!; }
            internal string Path { get; }
            internal DeviceItem Item { get; }
            internal object NetworkInterface { get; }
            internal object Node { get; }
            internal NetworkNodeInfo(string path, DeviceItem item, object networkInterface, object node)
            { Path = path; Item = item; NetworkInterface = networkInterface; Node = node; }
        }
        private static object? TryGetPropertyValue(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v != null) return v;
                }
                catch /* swallow(probe-optional): an unavailable property lets the next candidate property be tried */ { }
            }
            return null;
        }
        private static string? TryGetName(object? o)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(o);
                return v?.ToString();
            }
            catch /* swallow(probe-optional): objects without a readable Name yield no name hint */
            {
                return null;
            }
        }
        private static object? TryGetEngineeringAttribute(object target, string attributeName)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                return get?.Invoke(target, new object[] { attributeName });
            }
            catch /* swallow(probe-optional): An unavailable engineering attribute is represented by null for the existing fallback. */
            {
                return null;
            }
        }
        private static object? TryGetServiceByTypeSuffix(object target, string serviceTypeNameSuffix)
        {
            try
            {
                var getService = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService == null) return null;

                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { /* swallow(probe-optional): An unavailable reflection service or assembly is skipped while probing the optional cross-reference API. */ return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) ||
                                         t.FullName?.EndsWith("." + serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) == true);
                if (serviceType == null) return null;

                return getService.MakeGenericMethod(serviceType).Invoke(target, Array.Empty<object>());
            }
            catch
            { /* swallow(probe-optional): An unavailable reflection service or assembly is skipped while probing the optional cross-reference API. */
                return null;
            }
        }

        private static string? ReadNodeAddress(object node)
            => TryGetPropertyValue(node, "Address")?.ToString()
                ?? TryGetPropertyValue(node, "IpAddress")?.ToString()
                ?? TryGetPropertyValue(node, "IPAddress")?.ToString()
                ?? TryGetEngineeringAttribute(node, "Address")?.ToString()
                ?? TryGetEngineeringAttribute(node, "IpAddress")?.ToString();
    }
}
