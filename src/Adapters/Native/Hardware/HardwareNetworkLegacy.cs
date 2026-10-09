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

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static List<object?> HardwareLineArray(IEnumerable<string> lines) => lines.Cast<object?>().ToList();
        public Func<Exception, string>? FormatEngineeringException { get; set; }
        public Func<string, object?>? ResolveEngineeringSoftwareContainer { get; set; }
        private string HardwareExceptionDetail(Exception error) => FormatEngineeringException != null ? FormatEngineeringException(error) : error.ToString();
        private SoftwareContainer? HardwareSoftwareContainer(string name)
        {
            if (ResolveEngineeringSoftwareContainer != null) return ResolveEngineeringSoftwareContainer(name) as SoftwareContainer;
            var item = HardwareResolveItem(name);
            return item == null ? null : ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>();
        }
        private static bool? IsAttributeWritable(object attributeInfo)
        {
            var names = new[] { "AccessMode", "Access", "Mode" };
            foreach (var name in names)
            {
                var value = HardwareTryProperty(attributeInfo, name)?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value!.IndexOf("ReadWrite", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (value.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0 && value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            return null;
        }
        public Dictionary<string, object?> HardwareGetProjectTopology()
        {
            if (HardwareProjectMissing()) return new Dictionary<string, object?> { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." };
            var devices = new List<object?>();
            foreach (Device device in project!.Devices)
            {
                devices.Add(new Dictionary<string, object?>
                {
                    ["device"] = device.Name ?? string.Empty,
                    ["nodes"] = HardwareBuildDeviceNodesJson(device)
                });
            }
            return new Dictionary<string, object?>
            {
                ["projectName"] = project!.Name ?? string.Empty,
                ["deviceCount"] = devices.Count,
                ["devices"] = devices,
                ["note"] = "Devices placed inside device groups are not enumerated here (mirrors Project.Devices)."
            };
        }

        public HardwareAddressingReply HardwareEnsureSubnet(string anchorDeviceItemPath, string subnetType, string subnetName)
        {
            // envelope: legacy-multiple-dynamic-fields
            var meta = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["mayHaveChanged"] = false,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath,
                ["subnetType"] = subnetType,
                ["subnetName"] = subnetName
            };

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }

                if (!HardwareIsSupportedProfinetSubnetType(subnetType))
                {
                    meta["failureCode"] = "UNSUPPORTED_CAPABILITY";
                    meta["error"] = "Only IndustrialEthernet/PROFINET subnet types are supported by this safe primitive.";
                    return new HardwareAddressingReply { Message = "Unsupported subnet type", Meta = meta };
                }

                var anchorRoot = HardwareResolveItem(anchorDeviceItemPath);
                if (anchorRoot == null)
                {
                    meta["failureCode"] = "NOT_FOUND";
                    meta["error"] = "Anchor device item not found";
                    return new HardwareAddressingReply { Message = "Anchor device item not found", Meta = meta };
                }

                var existing = HardwareFindConnectedSubnetByName(subnetName);
                if (existing != null)
                {
                    // envelope: legacy-single-verdict
                    meta["success"] = true;
                    meta["created"] = false;
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Subnet already exists and was read back", Meta = meta };
                }

                var node = FindNetworkNodes(anchorRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
                if (node.Node == null)
                {
                    meta["error"] = "No Industrial Ethernet/PROFINET network node found under anchor path.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "No suitable network node found", Meta = meta };
                }

                object? subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = node.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    meta["mayHaveChanged"] = create != null;
                    subnet = create?.Invoke(node.Node, new object[] { subnetName });
                    meta["created"] = subnet != null;
                }
                else
                {
                    meta["created"] = false;
                    meta["reusedExistingNodeSubnet"] = TryGetName(subnet) ?? subnet.ToString();
                }

                meta["selectedNode"] = HardwareFormatNodeInfo(node);
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = subnet != null && HardwareSubnetReadbackContains(subnetName);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Subnet ensured and read back" : "Subnet ensure did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = HardwareExceptionDetail(ex);
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                return new HardwareAddressingReply { Message = "Failed ensuring subnet", Meta = meta };
            }
        }

        public HardwareAddressingReply HardwareAttachDeviceNodeToSubnet(string deviceItemPath, int interfaceIndex, string subnetName, string anchorDeviceItemPath = "")
        {
            var meta = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["mayHaveChanged"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["interfaceIndex"] = interfaceIndex,
                ["subnetName"] = subnetName,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath
            };

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }

                var targetRoot = HardwareResolveItem(deviceItemPath);
                if (targetRoot == null)
                {
                    meta["failureCode"] = "NOT_FOUND";
                    meta["error"] = "Device item not found";
                    return new HardwareAddressingReply { Message = "Device item not found", Meta = meta };
                }

                object? subnet = HardwareFindConnectedSubnetByName(subnetName);
                if (subnet == null && !string.IsNullOrWhiteSpace(anchorDeviceItemPath))
                {
                    var ensure = HardwareEnsureSubnet(anchorDeviceItemPath, "PROFINET", subnetName);
                    meta["ensureSubnet"] = ensure.Meta;
                    subnet = HardwareFindConnectedSubnetByName(subnetName);
                }

                if (subnet == null)
                {
                    meta["error"] = "Subnet was not found. Call EnsureSubnet with a valid anchor first or pass anchorDeviceItemPath.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Subnet not found", Meta = meta };
                }

                var nodes = FindNetworkNodes(targetRoot)
                    .Where(n => IsIndustrialEthernetNode(n.Node))
                    .ToList();
                meta["candidateNodes"] = HardwareLineArray(nodes.Select(HardwareFormatNodeInfo));
                if (interfaceIndex < 0 || interfaceIndex >= nodes.Count)
                {
                    meta["error"] = $"interfaceIndex is out of range. Available Industrial Ethernet/PROFINET nodes: {nodes.Count}.";
                    return new HardwareAddressingReply { Message = "Interface index out of range", Meta = meta };
                }

                var selected = nodes[interfaceIndex];
                var connected = TryGetPropertyValue(selected.Node, "ConnectedSubnet");
                var connectedName = TryGetName(connected);
                if (!string.Equals(connectedName, subnetName, StringComparison.OrdinalIgnoreCase))
                {
                    var connect = selected.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    meta["mayHaveChanged"] = connect != null;
                    connect?.Invoke(selected.Node, new object[] { subnet });
                }

                meta["selectedNodeBefore"] = HardwareFormatNodeInfo(selected);
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = HardwareSubnetReadbackContains(subnetName) && HardwareBuildSubnetReadbackLines(subnetName).Any(x => x.IndexOf(deviceItemPath, StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf(selected.Item.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Device network node attached to subnet and read back" : "Device network node attach did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = HardwareExceptionDetail(ex);
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                return new HardwareAddressingReply { Message = "Failed attaching device node to subnet", Meta = meta };
            }
        }

        public HardwareProbeLines HardwareProbeHardwareHmiConnectionOwnerCandidates(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new HardwareProbeLines { FailureCode = "PROJECT_NOT_BOUND" };
            if (HardwareProjectMissing())
            {
                lines.Add("Project is null");
                return lines;
            }

            lines.FailureCode = "NOT_FOUND";
            var plcRoot = HardwareResolveItem(plcRootPath);
            var hmiRoot = HardwareResolveItem(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            lines.FailureCode = "UNSUPPORTED_CAPABILITY";
            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : HardwareFormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : HardwareFormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            lines.FailureCode = null;
            var candidates = deepScan
                ? HardwareBuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : HardwareBuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                lines.Add(c.Label
                    + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name)
                    + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
            }

            return lines;
        }

        public HardwareProbeLines HardwareProbeHardwareHmiConnectionWhitelistedServices(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new HardwareProbeLines { FailureCode = "PROJECT_NOT_BOUND" };
            if (HardwareProjectMissing())
            {
                lines.Add("Project is null");
                return lines;
            }

            lines.FailureCode = "NOT_FOUND";
            var plcRoot = HardwareResolveItem(plcRootPath);
            var hmiRoot = HardwareResolveItem(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            lines.FailureCode = "UNSUPPORTED_CAPABILITY";
            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : HardwareFormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : HardwareFormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            lines.FailureCode = null;
            var candidates = deepScan
                ? HardwareBuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : HardwareBuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();

#if !PLC_HARDWARE_LINKED_TAGS
            var commConnT = typeof(HardwareObject).Assembly.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition");
            var serviceTypes = commConnT != null
                ? new[] { commConnT, typeof(NetworkInterface), typeof(NetworkPort) }
                : new[] { typeof(NetworkInterface), typeof(NetworkPort) };
#else
            var serviceTypes = new[]
            {
                typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition),
                typeof(NetworkInterface),
                typeof(NetworkPort)
            };
#endif

            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);
            lines.Add("Service whitelist: " + string.Join(", ", serviceTypes.Select(t => t.FullName)));

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                if (!HardwareIsSafeHardwareHmiServiceProbeTarget(c.Target))
                {
                    lines.Add("Candidate: " + c.Label + " | type=" + c.Target.GetType().FullName + " | SKIP unsafe/high-level target");
                    continue;
                }

                lines.Add("Candidate: " + c.Label + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name) + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
                foreach (var serviceType in serviceTypes)
                {
                    object? service = null;
                    try
                    {
                        service = TryGetService(c.Target, serviceType);
                    }
                    catch (Exception ex)
                    {
                        lines.Add("  " + serviceType.Name + ": ERROR " + HardwareExceptionDetail(ex));
                        continue;
                    }

                    if (service == null)
                    {
                        lines.Add("  " + serviceType.Name + ": <none>");
                        continue;
                    }

                    lines.Add("  " + serviceType.Name + ": " + service.GetType().FullName + " | " + HardwareSummarizeWhitelistedService(service));
                }
            }

            return lines;
        }

        private static bool HardwareIsSafeHardwareHmiServiceProbeTarget(object target)
        {
            var typeName = target.GetType().FullName ?? target.GetType().Name;
            if ((typeName.IndexOf("Project", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if ((typeName.IndexOf("Composition", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if ((typeName.IndexOf("DeviceComposition", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            return (typeName.IndexOf("DeviceItem", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (typeName.IndexOf("Node", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (typeName.IndexOf("NetworkInterface", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (typeName.IndexOf("NetworkPort", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (typeName.IndexOf("HardwareComponent", StringComparison.OrdinalIgnoreCase) >= 0)
                   || (typeName.IndexOf("Device", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string HardwareSummarizeWhitelistedService(object service)
        {
            var parts = new List<string>();
            try
            {
                var count = TryGetPropertyValue(service, "Count");
                if (count != null) parts.Add("Count=" + count);
            }
            catch { /* swallow(probe-optional): An unavailable optional service summary field must not discard the other probe evidence. */ }

            try
            {
                var nodes = TryGetPropertyValue(service, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add((TryGetName(node) ?? "<unnamed>") + ":" + (TryGetPropertyValue(node, "NodeType")?.ToString() ?? "") + ":" + (TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"));
                    }
                    parts.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }
            }
            catch { /* swallow(probe-optional): An unavailable optional service summary field must not discard the other probe evidence. */ }

            try
            {
                var createMethods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (createMethods.Count > 0) parts.Add("CreateMethods=[" + string.Join(" | ", createMethods) + "]");
            }
            catch { /* swallow(probe-optional): An unavailable optional service summary field must not discard the other probe evidence. */ }

            try
            {
                var methods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m =>
                    {
                        var n = m.Name ?? "";
                        return n.IndexOf("Connect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Disconnect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Subnet", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (methods.Count > 0) parts.Add("NetworkMethods=[" + string.Join(" | ", methods) + "]");
            }
            catch { /* swallow(probe-optional): An unavailable optional service summary field must not discard the other probe evidence. */ }

            return parts.Count == 0 ? "<no summary>" : string.Join("; ", parts);
        }

        private static bool HardwareIsSupportedProfinetSubnetType(string subnetType)
        {
            var value = (subnetType ?? string.Empty).Trim();
            return value.Length == 0
                   || value.Equals("PROFINET", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("IndustrialEthernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Industrial Ethernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN/IE", StringComparison.OrdinalIgnoreCase);
        }

        private object? HardwareFindConnectedSubnetByName(string subnetName)
        {
            if (project == null || string.IsNullOrWhiteSpace(subnetName))
                return null;

            foreach (var device in project.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            return subnet;
                    }
                }
            }

            foreach (var group in project.DeviceGroups)
            {
                foreach (var subnet in HardwareFindConnectedSubnetByNameInGroup(group, subnetName))
                    return subnet;
            }

            return null;
        }

        private IEnumerable<object> HardwareFindConnectedSubnetByNameInGroup(DeviceUserGroup group, string subnetName)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            yield return subnet;
                    }
                }
            }

            foreach (var child in group.Groups)
            {
                foreach (var subnet in HardwareFindConnectedSubnetByNameInGroup(child, subnetName))
                    yield return subnet;
            }
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

            void AddFromDevice(Device device)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(HardwareFormatNodeInfo(node));
                    }
                }
            }

            foreach (var device in project.Devices)
                AddFromDevice(device);
            foreach (var group in project.DeviceGroups)
                HardwareAddSubnetReadbackLinesFromGroup(group, subnetName, lines);

            return lines;
        }

        private void HardwareAddSubnetReadbackLinesFromGroup(DeviceUserGroup group, string subnetName, List<string> lines)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(HardwareFormatNodeInfo(node));
                    }
                }
            }

            foreach (var child in group.Groups)
                HardwareAddSubnetReadbackLinesFromGroup(child, subnetName, lines);
        }

        private bool HardwareSubnetReadbackContains(string subnetName)
        {
            return HardwareBuildSubnetReadbackLines(subnetName)
                .Any(x => x.IndexOf("connectedSubnet=" + subnetName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public List<HardwareNetworkAttribute>? HardwareGetDeviceItemNetworkInfo(string deviceItemPath)
        {
            if (HardwareProjectMissing()) return null;
            var di = HardwareResolveItem(deviceItemPath);
            if (di == null) return null;

            // Heuristic: filter attribute names that likely contain network addressing / interface identity.
            var keys = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address"
            };

            var list = new List<HardwareNetworkAttribute>();
            try
            {
                foreach (var info in di.GetAttributeInfos())
                {
                    var n = info.Name ?? "";
                    var lower = n.ToLowerInvariant();
                    if (!keys.Any(k => lower.Contains(k))) continue;

                    object vObj;
                    try { vObj = di.GetAttribute(info.Name); }
                    catch /* swallow(probe-optional): an unreadable device attribute is omitted from address diagnostics */ { continue; }

                    var v = vObj?.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    list.Add(new HardwareNetworkAttribute
                    {
                        Name = info.Name,
                        Value = v,
                        DataType = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                        IsWritable = IsAttributeWritable(info)
                    });
                }
            }
            catch /* swallow(enumerate-optional): unavailable attribute metadata leaves the collected address diagnostics intact */
            {
                // best-effort
            }

            return list;
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

        public string HardwareProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var sb = new StringBuilder();
            if (HardwareProjectMissing()) return "Project is null";

            var plcRoot = HardwareResolveItem(plcRootPath);
            var hmiRoot = HardwareResolveItem(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNodes = FindNetworkNodes(plcRoot).ToList();
            var hmiNodes = FindNetworkNodes(hmiRoot).ToList();
            sb.AppendLine("PLC item service scan:");
            foreach (var line in HardwareDescribeNetworkServiceScan(plcRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("HMI item service scan:");
            foreach (var line in HardwareDescribeNetworkServiceScan(hmiRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("PLC nodes:");
            foreach (var n in plcNodes) sb.AppendLine("  " + HardwareFormatNodeInfo(n));
            sb.AppendLine("HMI nodes:");
            foreach (var n in hmiNodes) sb.AppendLine("  " + HardwareFormatNodeInfo(n));

            var plcNode = plcNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            var hmiNode = hmiNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : HardwareFormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : HardwareFormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

            object? subnet = null;
            try
            {
                subnet = TryGetPropertyValue(plcNode.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = plcNode.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    subnet = create?.Invoke(plcNode.Node, new object[] { subnetName });
                    sb.AppendLine("PLC CreateAndConnectToSubnet: " + (subnet == null ? "NULL" : "OK " + TryGetName(subnet)));
                }
                else
                {
                    sb.AppendLine("PLC already connected to subnet: " + (TryGetName(subnet) ?? subnet.ToString()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("PLC subnet create/connect error: " + HardwareExceptionDetail(ex));
            }

            if (subnet != null)
            {
                try
                {
                    var connect = hmiNode.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    connect?.Invoke(hmiNode.Node, new object[] { subnet });
                    sb.AppendLine("HMI ConnectToSubnet: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("HMI ConnectToSubnet error: " + HardwareExceptionDetail(ex));
                }
            }

            sb.AppendLine("Readback PLC node: " + HardwareFormatNodeInfo(plcNode));
            sb.AppendLine("Readback HMI node: " + HardwareFormatNodeInfo(hmiNode));

            try
            {
                var sw = HardwareSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("HMI Connections after subnet:");
                if (connections is IEnumerable en)
                {
                    var any = false;
                    foreach (var c in en)
                    {
                        any = true;
                        sb.AppendLine("  " + (TryGetName(c) ?? c?.ToString() ?? "<null>"));
                    }
                    if (!any) sb.AppendLine("  <empty>");
                }
                else
                {
                    sb.AppendLine("  <not found>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("HMI connection readback error: " + HardwareExceptionDetail(ex));
            }

            return sb.ToString();
        }

        private IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> HardwareBuildHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
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

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Expand(string label, object? start, object localNode, DeviceItem partnerTarget, object partnerNode)
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

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Add(string label, object? target, object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                if (target == null) yield break;
                if (!seen.Add(target)) yield break;
                yield return (label, target, localNode, partnerTarget, partnerNode);
            }
        }

        private static IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> HardwareBuildDirectHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            yield return ("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node);
        }

        private static IEnumerable<string> HardwareDescribeNetworkServiceScan(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name, new HashSet<DeviceItem>()))
            {
                object? networkInterface = null;
                Exception? networkEx = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch (Exception ex) { networkEx = ex; }

                var line = $"path={item.Path}; item={TryGetName(item.Object) ?? item.DeviceItem.Name}; ownerDeviceItem={item.DeviceItem.Name}; objectKind={item.Kind}; type={item.Object.GetType().FullName}";
                if (networkInterface != null)
                {
                    var nodes = TryGetPropertyValue(networkInterface, "Nodes") as IEnumerable;
                    var nodeCount = 0;
                    if (nodes != null)
                    {
                        foreach (var _ in nodes) nodeCount++;
                    }
                    line += $"; NetworkInterface=YES; nodes={nodeCount}";
                }
                else
                {
                    line += "; NetworkInterface=NO";
                    if (networkEx != null) line += "; err=" + networkEx.Message;
                }

                yield return line;
            }
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
        public HardwareProbeReply ReadHardwareOwnerCandidates(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = HardwareProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
            return new HardwareProbeReply { Lines = lines.ToArray(), FailureCode = lines.FailureCode };
        }
        public HardwareProbeReply ReadHardwareWhitelistedServices(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = HardwareProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);
            return new HardwareProbeReply { Lines = lines.ToArray(), FailureCode = lines.FailureCode };
        }
    }
}
