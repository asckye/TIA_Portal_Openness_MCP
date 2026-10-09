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

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private Device? HardwareLegacyDevice(string devicePath)
        {
            if (project?.Devices == null || string.IsNullOrWhiteSpace(devicePath))
                return null;

            devicePath = devicePath.Trim();

            // Siemens hardware device names legitimately contain '/', e.g. "S7-1500/ET200MP station_1".
            // An exact full-name match must therefore win BEFORE we treat '/' as a path separator,
            // otherwise such devices are impossible to address (split -> bogus group lookup -> null).
            // Also accept the IDE-visible CPU name (a DeviceItem name, e.g. "安全PLC"/"5T车"), which differs
            // from the parent Device name (e.g. "S7-1200 station_3"); resolve it back to its owning Device.
            var allDevices = EnumerateAllDevices().ToList();
            var exact = allDevices.FirstOrDefault(d => d.Name.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var byItem = allDevices.FirstOrDefault(d => DeviceHasItemNamed(d.DeviceItems, devicePath));
            if (byItem != null) return byItem;

            var pathSegments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Try top-level device first
            if (pathSegments.Length == 1)
            {
                return project.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));
            }

            // Traverse device groups
            DeviceUserGroupComposition? groups = project.DeviceGroups;
            DeviceUserGroup? group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));

            if (group == null)
            {
                return null;
            }

            for (int i = 1; i < pathSegments.Length; i++)
            {
                // Try to find device in current group
                var device = group.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    return device;
                }

                // Try to find subgroup
                group = group.Groups.FirstOrDefault(g => g.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    break;
                }
            }

            return null;
        }

        // Enumerate every Device in the project, including those nested inside device groups.
        private IEnumerable<Device> EnumerateAllDevices()
        {
            if (project?.Devices != null)
                foreach (Device d in project.Devices) yield return d;
            foreach (var d in EnumerateGroupDevices(project?.DeviceGroups)) yield return d;
        }

        private static IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroupComposition? groups)
        {
            if (groups == null) yield break;
            foreach (var g in groups)
            {
                foreach (Device d in g.Devices) yield return d;
                foreach (var d in EnumerateGroupDevices(g.Groups)) yield return d;
            }
        }

        // True if the device contains a DeviceItem (CPU/module, at any nesting depth) with the given name.
        private static bool DeviceHasItemNamed(DeviceItemComposition? items, string name)
        {
            if (items == null) return false;
            foreach (DeviceItem it in items)
            {
                if (it.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                if (DeviceHasItemNamed(it.DeviceItems, name)) return true;
            }
            return false;
        }

        private DeviceItem? HardwareLegacyItem(string deviceItemPath)
        {
            if (project == null || project.Devices == null)
            {
                return null;
            }

            // Split the device path by '/' to get each device name  
            var pathSegments = deviceItemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            DeviceItem? deviceItem = null;

            // initial devices and groups
            var devices = project.Devices;
            var groups = project.DeviceGroups;

            for (int index = 0; index < pathSegments.Length; index++)
            {
                deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index, out var anchorMatched);

                // 这个循环是有意宽松的：它允许调用方省掉前缀（"PLC_1/AI 2_1" 而不是
                // "S7-1200 station_1/PLC_1/AI 2_1"），做法是匹配不上就把窗口往后滑一格再试。
                // 但窗口滑动必须只用于「这一段根本不是这层的东西」，**不能用于
                // 「这一段对上了、后面某一段对不上」** —— 那种情况下往后滑，最终会被
                // SelectMany 扫到路径中间的某一段并原样返回，于是「路径打错一段」
                // 静默变成「拿到它的父级设备项」：读地址读到父级的（空）清单、
                // 改地址改到父级头上。调用方看不出任何异常。
                if (anchorMatched)
                {
                    return deviceItem;
                }

                if (deviceItem == null)
                {
                    // search in groups
                    var group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[index], StringComparison.OrdinalIgnoreCase));
                    if (group != null)
                    {
                        devices = group.Devices;
                        if (devices != null)
                        {
                            // 这里丢弃 anchorMatched 是有意的：设备组可以嵌套
                            // （"GroupA/GroupB/Device/Item"），下一段对不上组内设备属于正常，
                            // 要继续往 group.Groups 里找，此处必须允许窗口滑动。
                            deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index + 1, out _);
                        }

                        if (deviceItem != null)
                        {
                            return deviceItem;
                        }

                        // not found, but on the path
                        groups = group.Groups;
                        devices = group.Devices;
                    }
                }
                else
                {
                    return deviceItem;
                }
            }

            return deviceItem;
        }

        /// <param name="anchorMatched">
        /// 本层是否**认领**了 pathSegments[index]（匹配到同名 Device 或 DeviceItem）。
        /// 认领了却返回 null，意思是「锚点对上了，但后面某一段不存在」——
        /// 调用方必须就此判定失败，不能再把窗口往后滑（滑动会让错误路径解析成祖先节点）。
        /// </param>

        /// <summary>
        /// 在一组子设备项里按名字找一个，**允许名字本身含 '/'**。
        ///
        /// 为什么要这样：deviceItemPath 用 '/' 分段，而西门子的模块名常常自带 '/'——
        /// `DQ 16x24VDC/0.5A ST_1`、`AI 4xI 2-/4-wire ST_1`、`AQ 4xU/I ST_1`、`DI 6/DQ 4_1`。
        /// 逐段比对的话，这些模块**从原理上就寻不到址**：不是找不到，是根本表达不出来
        /// （用户在 issue #33 里点名了这一条）。
        ///
        /// 做法是贪心：把 segments[index..] 里的前 k 段拼回 "a/b/c" 再和子项名比，
        /// **k 从大到小试**，先匹配更长（更具体）的那个，命中就把 consumed 设成 k。
        /// 子项名是已知的有限集合，所以这不是猜——是拿候选名去对，不会误吃别人的段。
        /// </summary>
        private static DeviceItem? MatchChildByName(
            IEnumerable<DeviceItem>? children, string[] segments, int index, out int consumed)
        {
            consumed = 0;
            if (children == null || index >= segments.Length) return null;

            var list = children as IList<DeviceItem> ?? children.ToList();
            var maxSpan = segments.Length - index;
            for (var span = maxSpan; span >= 1; span--)
            {
                var candidate = string.Join("/", segments, index, span);
                foreach (var child in list)
                {
                    if (child != null && child.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        consumed = span;
                        return child;
                    }
                }
            }
            return null;
        }

        private static DeviceItem? GetDeviceItemFromDevice(string[] pathSegments, DeviceComposition? devices, int index, out bool anchorMatched)
        {
            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            anchorMatched = false;
            DeviceItem? deviceItem = null;

            // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
            // use segment to find device
            var device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (device != null)
            {
                anchorMatched = true;
                if (string.IsNullOrWhiteSpace(nextSegment))
                {
                    deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                        ?? device.DeviceItems.FirstOrDefault();
                }
                else
                {
                    // 名字里可能含 '/'，所以每一层都走贪心匹配，吃掉几段由匹配结果决定。
                    deviceItem = MatchChildByName(device.DeviceItems, pathSegments, index + 1, out var used);
                    var nextIndex = index + 1 + used;
                    while (deviceItem != null && nextIndex < pathSegments.Length)
                    {
                        deviceItem = MatchChildByName(deviceItem.DeviceItems, pathSegments, nextIndex, out used);
                        if (used == 0) break;
                        nextIndex += used;
                    }
                }

            }

            // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
            if (device == null)
            {
                deviceItem = devices
                .SelectMany(d => d.DeviceItems)
                .FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));

                // 这一段被认领了就得把**剩下的段也走完**。原来这里找到就直接返回，
                // 后面的段整段被忽略：'安全PLC/不存在的模块' 会返回 '安全PLC' 本身。
                if (deviceItem != null)
                {
                    anchorMatched = true;
                    var next = index + 1;
                    while (deviceItem != null && next < pathSegments.Length)
                    {
                        deviceItem = MatchChildByName(deviceItem.DeviceItems, pathSegments, next, out var used);
                        if (used == 0) break;
                        next += used;
                    }
                }
            }

            return deviceItem;
        }


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
            internal string Path { get; }
            internal object Node { get; }
            internal NetworkNodeInfo(string path, DeviceItem item, object networkInterface, object node)
            { Path = path; Node = node; }
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
