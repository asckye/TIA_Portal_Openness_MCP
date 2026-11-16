using TiaMcp.Adapters.Native.Engineering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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
        private static List<object?> HardwareLineArray(IEnumerable<string> lines) => lines.Cast<object?>().ToList();
        public Func<Exception, string>? FormatEngineeringException { get; set; }
        public Func<string, object?>? ResolveEngineeringSoftwareContainer { get; set; }
        private string HardwareExceptionDetail(Exception error) => FormatEngineeringException != null ? FormatEngineeringException(error) : error.ToString();
        public Dictionary<string, object?> HardwareGetProjectTopology()
        {
            if (HardwareProjectMissing()) return new Dictionary<string, object?> { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." };
            var devices = new List<object?>();
            foreach (var located in ProjectDeviceWalk.Walk(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup))
            {
                devices.Add(new Dictionary<string, object?>
                {
                    ["device"] = located.Device.Name ?? string.Empty,
                    ["groupPath"] = located.GroupPath,
                    ["nodes"] = HardwareBuildDeviceNodesJson(located.Device)
                });
            }
            return new Dictionary<string, object?>
            {
                ["projectName"] = project!.Name ?? string.Empty,
                ["deviceCount"] = devices.Count,
                ["devices"] = devices,
                ["note"] = "Root devices, nested device groups and the ungrouped devices group are enumerated (groupPath names the group)."
            };
        }

        private Subnet? HardwareFindSubnet(string subnetName)
        {
            if (project == null || string.IsNullOrWhiteSpace(subnetName)) return null;
            return HardwareGroupOperations.Find(project.Subnets, HardwareNetworkPolicy.RequireExactName(subnetName, "subnetName")) as Subnet;
        }

        private List<object?> HardwareBuildSubnetReadbackJson(string subnetName)
        {
            var arr = new List<object?>();
            foreach (var line in HardwareBuildSubnetReadbackLines(subnetName))
                arr.Add(line);
            return arr;
        }

        private List<string> HardwareBuildSubnetReadbackLines(string subnetName)
        {
            var lines = new List<string>();
            if (project == null) return lines;
            foreach (var device in EnumerateAllDevices())
                foreach (var root in device.DeviceItems)
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(HardwareFormatNodeInfo(node));
                    }
            return lines;
        }

        private List<object?> HardwareBuildDeviceNodesJson(Device device)
        {
            var arr = new List<object?>();
            foreach (var root in device.DeviceItems)
            {
                foreach (var n in FindNetworkNodes(root))
                {
                    var subnet = TryGetPropertyValue(n.Node, "ConnectedSubnet");
                    arr.Add(new Dictionary<string, object?>
                    {
                        ["nodeName"] = TryGetName(n.Node) ?? "<unnamed>",
                        ["address"] = ReadNodeAddress(n.Node) ?? string.Empty,
                        ["nodeType"] = TryGetPropertyValue(n.Node, "NodeType")?.ToString() ?? string.Empty,
                        ["connectedSubnet"] = TryGetName(subnet) ?? subnet?.ToString() ?? string.Empty,
                        ["interfacePath"] = n.Path,
                        ["isIndustrialEthernet"] = IsIndustrialEthernetNode(n.Node)
                    });
                }
            }
            return arr;
        }

        // The HMI software container of the second root, its parent item or its device's items (Unified PC/panel head item).
        private static SoftwareContainer? HardwareSoftwareContainerUnder(DeviceItem root)
        {
            var pending = new Stack<DeviceItem>();
            pending.Push(root);
            if (root.Parent is DeviceItem parent) pending.Push(parent);
            if (HardwareOwningDevice(root) is Device device) foreach (DeviceItem item in device.DeviceItems) pending.Push(item);
            var seen = new HashSet<DeviceItem>();
            int budget = 0;
            while (pending.Count > 0 && ++budget < 4096)
            {
                var item = pending.Pop();
                if (!seen.Add(item)) continue;
                var container = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
                if (container?.Software != null && (container.Software.GetType().FullName ?? "").IndexOf("Hmi", StringComparison.Ordinal) >= 0)
                    return container;
                foreach (DeviceItem child in item.DeviceItems) pending.Push(child);
            }
            return null;
        }

        private static Device? HardwareOwningDevice(DeviceItem item)
        {
            object? current = item;
            for (int hop = 0; hop < 16 && current != null; hop++)
            {
                if (current is Device device) return device;
                current = (current as IEngineeringInstance)?.Parent;
            }
            return null;
        }

        private IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget,
                object PartnerNode)> HardwareBuildHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            var seen = new HashSet<object>(HardwareReferenceEqualityComparer.Instance);

            foreach (var c in Expand("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;

            if (project != null)
            {
                foreach (var c in Add("Project", project, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;
                foreach (var c in Add("Project devices", project.Devices, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;

                foreach (var d in project.Devices)
                {
                    foreach (var c in Add("Device " + d.Name, d, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                    foreach (var c in Add("DeviceItems " + d.Name, d.DeviceItems, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Expand(string label, object? start,
                    object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                var current = start;
                for (var depth = 0; current != null && depth < 8; depth++)
                {
                    foreach (var c in Add(depth == 0 ? label : label + " ancestor[" + depth + "]", current, localNode, partnerTarget, partnerNode))
                        yield return c;

                    var next = TryGetPropertyValue(current, "Parent", "OwnedBy");
                    if (next == null || ReferenceEquals(next, current)) break;
                    current = next;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Add(string label, object? target,
                    object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                if (target == null) yield break;
                if (!seen.Add(target)) yield break;
                yield return (label, target, localNode, partnerTarget, partnerNode);
            }
        }

        private static IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget,
                object PartnerNode)> HardwareBuildDirectHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            yield return ("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node);
        }

        private static string HardwareFormatNodeInfo(NetworkNodeInfo info)
        {
            return $"path={info.Path}; item={info.Item.Name}; node={TryGetName(info.Node) ?? "<unnamed>"}; type={TryGetPropertyValue(info.Node, "NodeType")}; connectedSubnet={TryGetName(TryGetPropertyValue(info.Node, "ConnectedSubnet")) ?? TryGetPropertyValue(info.Node, "ConnectedSubnet")?.ToString() ?? "<none>"}";
        }
        private sealed class HardwareReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly HardwareReferenceEqualityComparer Instance = new HardwareReferenceEqualityComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
        private static object? TryGetService(object owner, Type serviceType)
        {
            var method = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(serviceType);
            return method.Invoke(owner, null);
        }
    }
}
