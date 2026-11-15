#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hardware
{
    /// <summary>
    /// The one project device enumeration shared by the adapter, the Studio navigator and the engine resolvers.
    /// Devices sit in exactly three places (devices-and-hardware skill, "Recursing Through Nested Device Groups"):
    /// Project.Devices, the user DeviceGroups with their nested Groups, and UngroupedDevicesGroup.Devices
    /// (Project in V14 SP1-V16, ProjectBase from V17). Order: root devices, user groups depth-first, ungrouped.
    /// </summary>
    internal static class ProjectDeviceWalk
    {
        /// <summary>Language-independent path segment for the ungrouped system group (its Name is localized).</summary>
        internal const string UngroupedSegment = "$ungrouped";
        private const int MaximumGroupDepth = 64;

        internal sealed class Located
        {
            internal Located(Device device, string[] groupNames, bool ungrouped)
            { Device = device; GroupNames = groupNames; Ungrouped = ungrouped; }
            internal Device Device { get; }
            /// <summary>Empty at the project root, slash-separated user group names, or the ungrouped group's name.</summary>
            internal string GroupPath => string.Join("/", GroupNames);
            internal string[] GroupNames { get; }
            internal bool Ungrouped { get; }
        }

        /// <summary>
        /// Enumerates the three device roots. With <paramref name="onError"/> an unreadable composition is reported
        /// and skipped (Studio browsing); without it the native exception propagates to the caller.
        /// </summary>
        internal static IEnumerable<Located> Walk(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped,
            Action<Exception>? onError = null)
        {
            foreach (var device in Each(root, onError)) if (device != null) yield return new Located(device, Array.Empty<string>(), false);
            foreach (var group in Each(groups, onError))
                if (group != null)
                    foreach (var located in Group(group, new[] { Name(group, onError) }, 0, onError)) yield return located;
            if (ungrouped == null) yield break;
            string ungroupedName = Name(ungrouped, onError);
            foreach (var device in Each(Devices(ungrouped, onError), onError))
                if (device != null) yield return new Located(device, new[] { ungroupedName }, true);
        }

        internal static IEnumerable<Device> Devices(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped)
            => Walk(root, groups, ungrouped).Select(located => located.Device);

        internal static string? DevicePathPrefix(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups,
            DeviceGroup? ungrouped, Device device)
        {
            if (root != null && root.Any(candidate => candidate.Equals(device))) return "devices/" + PathName(device.Name);
            var grouped = GroupDevicePath(groups, "device-groups/", device);
            if (grouped != null) return grouped;
            return ungrouped != null && ungrouped.Devices.Any(candidate => candidate.Equals(device))
                ? "ungrouped/" + PathName(device.Name) : null;
        }

        private static string? GroupDevicePath(IEnumerable<DeviceUserGroup>? groups, string prefix, Device device)
        {
            if (groups == null) return null;
            foreach (var group in groups)
            {
                string path = prefix + PathName(group.Name);
                if (group.Devices.Any(candidate => candidate.Equals(device))) return path + "/devices/" + PathName(device.Name);
                var nested = GroupDevicePath(group.Groups, path + "/groups/", device);
                if (nested != null) return nested;
            }
            return null;
        }

        private static string PathName(string name) => name.Replace("%", "%25").Replace("/", "%2F");

        /// <summary>Names of the devices and of their top-level device items (CPU/head module), for name-collision preflights.</summary>
        internal static HashSet<string> OccupiedNames(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in Devices(root, groups, ungrouped))
            {
                names.Add(device.Name);
                foreach (DeviceItem item in device.DeviceItems) if (item != null) names.Add(item.Name);
            }
            return names;
        }

        private static IEnumerable<Located> Group(DeviceUserGroup group, string[] names, int depth, Action<Exception>? onError)
        {
            if (depth > MaximumGroupDepth) throw new InvalidOperationException("Device group nesting exceeds the traversal depth limit.");
            foreach (var device in Each(group.Devices, onError)) if (device != null) yield return new Located(device, names, false);
            foreach (var child in Each(group.Groups, onError))
                if (child != null)
                    foreach (var located in Group(child, names.Concat(new[] { Name(child, onError) }).ToArray(), depth + 1, onError))
                        yield return located;
        }

        private static IEnumerable<Device>? Devices(DeviceGroup group, Action<Exception>? onError)
        {
            if (onError == null) return group.Devices;
            try { return group.Devices; }
            catch (Exception error) { onError(error); return null; }
        }

        private static string Name(object group, Action<Exception>? onError)
        {
            string? name = group is DeviceGroup g ? (onError == null ? g.Name : SafeName(g, onError)) : null;
            return name ?? string.Empty;
        }

        private static string? SafeName(DeviceGroup group, Action<Exception> onError)
        {
            try { return group.Name; }
            catch (Exception error) { onError(error); return null; }
        }

        private static IEnumerable<T> Each<T>(IEnumerable<T>? source, Action<Exception>? onError)
        {
            if (source == null) yield break;
            if (onError == null)
            {
                foreach (var value in source) yield return value;
                yield break;
            }
            IEnumerator<T> cursor;
            try { cursor = source.GetEnumerator(); }
            catch (Exception error) { onError(error); yield break; }
            using (cursor)
            {
                while (true)
                {
                    T current;
                    try
                    {
                        if (!cursor.MoveNext()) yield break;
                        current = cursor.Current;
                    }
                    catch (Exception error) { onError(error); yield break; }
                    yield return current;
                }
            }
        }

        // ---- path resolution shared by the adapter and engine resolvers -------------------------------------

        /// <summary>
        /// Resolves printed and joined name paths before considering the legacy CPU-name alias.
        /// </summary>
        internal static Device? FindDevice(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped, string devicePath)
            => string.IsNullOrWhiteSpace(devicePath) ? null
                : ResolveDevice(root, groups, ungrouped, new[] { devicePath.Trim() }, legacyText: true);

        internal static Device? ResolveDevice(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped,
            string[] path, bool legacyText)
        {
            if (path.Length == 0) return null;
            string target = string.Join("/", path);
            if (path.Length > 1)
            {
                IEnumerable<Device>? devices;
                bool loose = Same(path[0], UngroupedSegment);
                if (loose) devices = path.Length == 2 ? ungrouped?.Devices : null;
                else
                {
                    var group = groups?.FirstOrDefault(g => Same(g.Name, path[0]));
                    for (int i = 1; group != null && i < path.Length - 1; i++)
                        group = group.Groups.FirstOrDefault(g => Same(g.Name, path[i]));
                    devices = group?.Devices;
                }
                return Unique((devices ?? Enumerable.Empty<Device>()).Where(d => Same(d.Name, path[path.Length - 1]))
                    .Select(d => new Located(d, path.Take(path.Length - 1).ToArray(), loose)), target);
            }
            var all = Walk(root, groups, ungrouped).ToArray();
            var exact = all.Where(d => Same(d.Device.Name, target) || Same(DevicePath(d), target) || Same(CanonicalDevicePath(d), target)).ToArray();
            if (exact.Length > 0) return Unique(exact, target);
            return legacyText ? Unique(all.Where(d => HasItemNamed(d.Device.DeviceItems, target)), target) : null;
        }

        private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static string DevicePath(Located located)
            => string.Join("/", (located.Ungrouped ? new[] { UngroupedSegment } : located.GroupNames).Concat(new[] { located.Device.Name }));

        private static string CanonicalDevicePath(Located located)
            => located.Ungrouped ? "ungrouped/" + PathName(located.Device.Name)
                : located.GroupNames.Length == 0 ? "devices/" + PathName(located.Device.Name)
                : "device-groups/" + string.Join("/groups/", located.GroupNames.Select(PathName)) + "/devices/" + PathName(located.Device.Name);

        private static Device? Unique(IEnumerable<Located> matches, string target)
        {
            var distinct = matches.GroupBy(d => d.Device).Select(g => g.First()).ToArray();
            if (distinct.Length < 2) return distinct.FirstOrDefault()?.Device;
            var candidates = distinct.Take(20).Select(DevicePath).ToArray();
            var error = new PlcSoftwareException("Ambiguous",
                "Device path " + target + " matches more than one device: " + string.Join("; ", candidates) + ".", candidates);
            error.Data["target"] = target;
            error.Data["candidates"] = candidates;
            throw error;
        }

        internal static HardwareAddressingException MissingDevice(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups,
            DeviceGroup? ungrouped, string[] path)
        {
            string target = string.Join("/", path);
            var candidates = Walk(root, groups, ungrouped).Take(20).Select(DevicePath).ToArray();
            var error = new HardwareAddressingException("NotFound",
                "Device not found: " + target + ". Candidates: " + (candidates.Length == 0 ? "none" : string.Join("; ", candidates)) + ".");
            error.Data["missingDeviceTarget"] = target;
            error.Data["target"] = target;
            error.Data["candidates"] = candidates;
            return error;
        }

        /// <summary>
        /// Resolves a project-tree path, an exact device/head-item name across all groups, or a slash-separated legacy path.
        /// The first level of a legacy path searches the root and the ungrouped devices;
        /// a segment that is not a device of the current level may name a user group (or "$ungrouped" at the top level).
        /// The window slides only while a segment is not claimed by the current level: once a device or item claims a
        /// segment, a later missing segment fails instead of silently resolving to an ancestor.
        /// </summary>
        internal static DeviceItem? FindItem(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped, string deviceItemPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(deviceItemPath)) return null;
            if (deviceItemPath.StartsWith("device-groups/", StringComparison.Ordinal)
                || deviceItemPath.StartsWith("devices/", StringComparison.Ordinal)
                || deviceItemPath.StartsWith("ungrouped/", StringComparison.Ordinal))
                return CanonicalItem(root, groups, ungrouped, deviceItemPath.Split('/'));
            var named = ItemFromDevices(new[] { deviceItemPath }, Devices(root, groups, ungrouped).ToList(), 0, out var namedAnchor);
            if (namedAnchor) return named;
            var segments = deviceItemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var ungroupedDevices = ungrouped == null ? new List<Device>() : ungrouped.Devices.ToList();
            IReadOnlyList<Device> devices = root.Concat(ungroupedDevices).ToList();
            IEnumerable<DeviceUserGroup>? levelGroups = groups;
            bool topLevel = true;
            DeviceItem? deviceItem = null;

            for (int index = 0; index < segments.Length; index++)
            {
                deviceItem = ItemFromDevices(segments, devices, index, out var anchorMatched);
                if (anchorMatched || deviceItem != null) return deviceItem;

                IReadOnlyList<Device>? groupDevices = null;
                IEnumerable<DeviceUserGroup>? childGroups = null;
                if (topLevel && segments[index] == UngroupedSegment)
                {
                    groupDevices = ungroupedDevices;
                    childGroups = Array.Empty<DeviceUserGroup>();
                }
                else
                {
                    var group = levelGroups?.FirstOrDefault(g => g.Name.Equals(segments[index], StringComparison.OrdinalIgnoreCase));
                    if (group != null) { groupDevices = group.Devices.ToList(); childGroups = group.Groups; }
                }
                if (groupDevices == null) continue;

                // Groups nest ("GroupA/GroupB/Device/Item"); a next segment that is not a device of this group is normal,
                // so the anchor flag is discarded here and the search continues in the child groups.
                if (index + 1 < segments.Length)
                {
                    deviceItem = ItemFromDevices(segments, groupDevices, index + 1, out _);
                    if (deviceItem != null) return deviceItem;
                }
                levelGroups = childGroups;
                devices = groupDevices;
                topLevel = false;
            }
            return deviceItem;
        }

        private static DeviceItem? CanonicalItem(IEnumerable<Device> root, IEnumerable<DeviceUserGroup>? groups,
            DeviceGroup? ungrouped, string[] segments)
        {
            IEnumerable<Device> devices = root;
            int index = 1;
            if (segments[0] == "ungrouped") devices = ungrouped?.Devices ?? Enumerable.Empty<Device>();
            else if (segments[0] == "device-groups")
            {
                while (index < segments.Length)
                {
                    var group = groups?.FirstOrDefault(g => g.Name.Equals(Uri.UnescapeDataString(segments[index]), StringComparison.OrdinalIgnoreCase));
                    if (group == null || ++index >= segments.Length) return null;
                    string kind = segments[index++];
                    if (kind == "devices") { devices = group.Devices; break; }
                    if (kind != "groups") return null;
                    groups = group.Groups;
                }
            }
            if (index >= segments.Length) return null;
            var path = segments.Skip(index).Select(Uri.UnescapeDataString).ToArray();
            return ItemFromDevices(path, devices.ToList(), 0, out _);
        }

        private static bool HasItemNamed(IEnumerable<DeviceItem>? items, string name)
        {
            if (items == null) return false;
            foreach (DeviceItem item in items)
            {
                if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                if (HasItemNamed(item.DeviceItems, name)) return true;
            }
            return false;
        }

        /// <summary>
        /// Matches a child by name while allowing '/' inside module names ("DQ 16x24VDC/0.5A ST_1", "AI 4xI 2-/4-wire ST_1"):
        /// the longest join of the remaining segments that names a known child wins, and <paramref name="consumed"/> reports it.
        /// </summary>
        private static DeviceItem? MatchChild(IEnumerable<DeviceItem>? children, string[] segments, int index, out int consumed)
        {
            consumed = 0;
            if (children == null || index >= segments.Length) return null;
            var list = children as IList<DeviceItem> ?? children.ToList();
            for (var span = segments.Length - index; span >= 1; span--)
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

        private static DeviceItem? ChildOf(DeviceItem parent, string[] segments, int index, out int consumed)
            => MatchChild(parent.DeviceItems, segments, index, out consumed)
                ?? MatchChild(parent.Items.OfType<DeviceItem>(), segments, index, out consumed);

        /// <param name="anchorMatched">
        /// True when this level claims <paramref name="index"/> (a device or device item of that name). A claimed anchor
        /// with a null result means a later segment does not exist; the caller must fail rather than slide the window.
        /// </param>
        private static DeviceItem? ItemFromDevices(string[] segments, IReadOnlyList<Device> devices, int index, out bool anchorMatched)
        {
            string segment = segments[index];
            string nextSegment = index + 1 < segments.Length ? segments[index + 1] : string.Empty;
            anchorMatched = false;
            DeviceItem? deviceItem = null;

            // A PC-based PLC is addressed by its visible Device.Name (for example 'PC-System_1').
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
                    deviceItem = MatchChild(device.DeviceItems, segments, index + 1, out var used);
                    var nextIndex = index + 1 + used;
                    while (deviceItem != null && nextIndex < segments.Length)
                    {
                        deviceItem = ChildOf(deviceItem, segments, nextIndex, out used);
                        if (used == 0) break;
                        nextIndex += used;
                    }
                    if (deviceItem == null)
                    {
                        deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                        nextIndex = index + 1;
                        while (deviceItem != null && nextIndex < segments.Length)
                        {
                            deviceItem = ChildOf(deviceItem, segments, nextIndex, out used);
                            if (used == 0) break;
                            nextIndex += used;
                        }
                    }
                }
                return deviceItem;
            }

            // A hardware PLC station name ('S7-1500/ET200MP station_1') is not visible in the IDE; its CPU item name is.
            deviceItem = devices.SelectMany(d => d.DeviceItems).FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (deviceItem != null)
            {
                // A claimed segment must also resolve every remaining segment.
                anchorMatched = true;
                var next = index + 1;
                while (deviceItem != null && next < segments.Length)
                {
                    deviceItem = ChildOf(deviceItem, segments, next, out var used);
                    if (used == 0) break;
                    next += used;
                }
            }
            return deviceItem;
        }
    }
}
