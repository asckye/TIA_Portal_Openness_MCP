#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;

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
            internal Located(Device device, string groupPath, bool ungrouped) { Device = device; GroupPath = groupPath; Ungrouped = ungrouped; }
            internal Device Device { get; }
            /// <summary>Empty at the project root, slash-separated user group names, or the ungrouped group's name.</summary>
            internal string GroupPath { get; }
            internal bool Ungrouped { get; }
        }

        /// <summary>
        /// Enumerates the three device roots. With <paramref name="onError"/> an unreadable composition is reported
        /// and skipped (Studio browsing); without it the native exception propagates to the caller.
        /// </summary>
        internal static IEnumerable<Located> Walk(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped,
            Action<Exception>? onError = null)
        {
            foreach (var device in Each(root, onError)) if (device != null) yield return new Located(device, string.Empty, false);
            foreach (var group in Each(groups, onError))
                if (group != null)
                    foreach (var located in Group(group, Name(group, onError), 0, onError)) yield return located;
            if (ungrouped == null) yield break;
            string ungroupedName = Name(ungrouped, onError);
            foreach (var device in Each(Devices(ungrouped, onError), onError)) if (device != null) yield return new Located(device, ungroupedName, true);
        }

        internal static IEnumerable<Device> Devices(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped)
            => Walk(root, groups, ungrouped).Select(located => located.Device);

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

        private static IEnumerable<Located> Group(DeviceUserGroup group, string path, int depth, Action<Exception>? onError)
        {
            if (depth > MaximumGroupDepth) throw new InvalidOperationException("Device group nesting exceeds the traversal depth limit.");
            foreach (var device in Each(group.Devices, onError)) if (device != null) yield return new Located(device, path, false);
            foreach (var child in Each(group.Groups, onError))
                if (child != null)
                    foreach (var located in Group(child, path + "/" + Name(child, onError), depth + 1, onError)) yield return located;
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
        /// Resolves a device path. Device names legitimately contain '/', so an exact full-name match wins first, then
        /// the IDE-visible CPU name (a DeviceItem name) resolves to its owning device. Otherwise the path is a group path:
        /// "Group/SubGroup/Device", or "$ungrouped/Device" for the ungrouped system group.
        /// </summary>
        internal static Device? FindDevice(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped, string devicePath)
        {
            if (root == null || string.IsNullOrWhiteSpace(devicePath)) return null;
            devicePath = devicePath.Trim();
            var all = Devices(root, groups, ungrouped).ToList();
            var exact = all.FirstOrDefault(d => d.Name.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var byItem = all.FirstOrDefault(d => HasItemNamed(d.DeviceItems, devicePath));
            if (byItem != null) return byItem;

            var segments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;
            if (segments.Length == 1) return all.FirstOrDefault(d => d.Name.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
            if (segments[0] == UngroupedSegment)
            {
                var rest = string.Join("/", segments, 1, segments.Length - 1);
                return ungrouped == null ? null : ungrouped.Devices.FirstOrDefault(d => d.Name.Equals(rest, StringComparison.OrdinalIgnoreCase));
            }

            var group = groups?.FirstOrDefault(g => g.Name.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
            if (group == null) return null;
            for (int i = 1; i < segments.Length; i++)
            {
                var device = group.Devices.FirstOrDefault(d => d.Name.Equals(segments[i], StringComparison.OrdinalIgnoreCase));
                if (device != null) return device;
                group = group.Groups.FirstOrDefault(g => g.Name.Equals(segments[i], StringComparison.OrdinalIgnoreCase));
                if (group == null) break;
            }
            return null;
        }

        /// <summary>
        /// Resolves a slash-separated device item path. The first level searches the root and the ungrouped devices;
        /// a segment that is not a device of the current level may name a user group (or "$ungrouped" at the top level).
        /// The window slides only while a segment is not claimed by the current level: once a device or item claims a
        /// segment, a later missing segment fails instead of silently resolving to an ancestor.
        /// </summary>
        internal static DeviceItem? FindItem(IEnumerable<Device>? root, IEnumerable<DeviceUserGroup>? groups, DeviceGroup? ungrouped, string deviceItemPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(deviceItemPath)) return null;
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
                        deviceItem = MatchChild(deviceItem.DeviceItems, segments, nextIndex, out used);
                        if (used == 0) break;
                        nextIndex += used;
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
                    deviceItem = MatchChild(deviceItem.DeviceItems, segments, next, out var used);
                    if (used == 0) break;
                    next += used;
                }
            }
            return deviceItem;
        }
    }
}
