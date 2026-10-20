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

                // Project.Subnets is authoritative (accessing-subnets); a subnet without nodes is still found.
                var existing = HardwareFindSubnet(subnetName);
                if (existing != null)
                {
                    meta["created"] = false;
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    meta["success"] = true;
                    return new HardwareAddressingReply { Message = "Subnet already exists in Project.Subnets and was read back", Meta = meta };
                }

                var node = FindNetworkNodes(anchorRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
                if (node.Node is not Node anchorNode)
                {
                    meta["error"] = "No Industrial Ethernet/PROFINET network node found under anchor path.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "No suitable network node found", Meta = meta };
                }

                meta["selectedNode"] = HardwareFormatNodeInfo(node);
                if (anchorNode.ConnectedSubnet != null)
                {
                    // CreateAndConnectToSubnet on a connected node would move or fail; the anchor must be free.
                    meta["anchorConnectedSubnet"] = anchorNode.ConnectedSubnet.Name;
                    meta["refusalReason"] = "The anchor node is attached to subnet '" + anchorNode.ConnectedSubnet.Name + "', not to '" + subnetName + "'; choose a free anchor node or disconnect it explicitly first. Nothing was created.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Anchor node is attached to another subnet; nothing was created", Meta = meta };
                }

                meta["mayHaveChanged"] = true;
                var subnet = anchorNode.CreateAndConnectToSubnet(subnetName);
                meta["created"] = subnet != null;
                meta["createdSubnetName"] = subnet?.Name;
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = subnet != null && object.Equals(anchorNode.ConnectedSubnet, subnet) && object.Equals(HardwareFindSubnet(subnetName), subnet);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Subnet created on the anchor node and read back (Project.Subnets and Node.ConnectedSubnet)" : "Subnet creation did not read back as the anchor node's subnet",
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

                var subnet = HardwareFindSubnet(subnetName);
                if (subnet == null && !string.IsNullOrWhiteSpace(anchorDeviceItemPath))
                {
                    var ensure = HardwareEnsureSubnet(anchorDeviceItemPath, "PROFINET", subnetName);
                    meta["ensureSubnet"] = ensure.Meta;
                    subnet = HardwareFindSubnet(subnetName);
                }

                if (subnet == null)
                {
                    meta["error"] = "Subnet was not found in Project.Subnets. Call EnsureSubnet with a valid anchor first or pass anchorDeviceItemPath.";
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
                meta["selectedNodeBefore"] = HardwareFormatNodeInfo(selected);
                if (selected.Node is not Node node)
                {
                    meta["error"] = "The selected network node is not an Openness Node.";
                    return new HardwareAddressingReply { Message = "Selected node is not a Node", Meta = meta };
                }
                var connected = node.ConnectedSubnet;
                if (connected != null && !object.Equals(connected, subnet))
                {
                    // Node.ConnectToSubnet throws on a connected node (devices-and-hardware skill); never move it implicitly.
                    meta["nodeConnectedSubnet"] = connected.Name;
                    meta["refusalReason"] = "The node is attached to subnet '" + connected.Name + "', not to '" + subnetName + "'; disconnect it explicitly first. Nothing was changed.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Node is attached to another subnet; nothing was changed", Meta = meta };
                }
                if (connected == null)
                {
                    meta["mayHaveChanged"] = true;
                    node.ConnectToSubnet(subnet);
                }
                else meta["alreadyConnected"] = true;

                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = object.Equals(node.ConnectedSubnet, subnet);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Device network node reads back on the subnet (Node.ConnectedSubnet)" : "Device network node attach did not read back on the subnet",
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
                        DataType = HardwareAttributeTypes(info) is { Length: > 0 } types ? types : null,
                        IsWritable = HardwareAttributeWritable(info)
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

        // ConnectDeviceNodesToProfinetSubnet. The operation name is kept for the worker wire; this is a guarded write, not a probe:
        // the subnet comes from Project.Subnets (an existing subnet of that name is joined explicitly, never through a silent
        // CreateAndConnectToSubnet name collision), a node attached to another subnet is refused, and success is the typed
        // Node.ConnectedSubnet read-back of both nodes. Connections are read from the HMI software under the second root.
        public string HardwareProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var sb = new StringBuilder();
            if (HardwareProjectMissing()) return "Project is null";

            var plcRoot = HardwareResolveItem(plcRootPath);
            var hmiRoot = HardwareResolveItem(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.AppendLine("Result: NOT CONNECTED: a root path was not found; nothing was changed.").ToString();

            var plcNodes = FindNetworkNodes(plcRoot).ToList();
            var hmiNodes = FindNetworkNodes(hmiRoot).ToList();
            sb.AppendLine("PLC nodes:");
            foreach (var n in plcNodes) sb.AppendLine("  " + HardwareFormatNodeInfo(n));
            sb.AppendLine("HMI nodes:");
            foreach (var n in hmiNodes) sb.AppendLine("  " + HardwareFormatNodeInfo(n));

            var plcNode = plcNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            var hmiNode = hmiNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node)) ?? new NetworkNodeInfo();
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : HardwareFormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : HardwareFormatNodeInfo(hmiNode)));
            if (plcNode.Node is not Node plc || hmiNode.Node is not Node hmi)
                return sb.AppendLine("Result: NOT CONNECTED: no Industrial Ethernet/PROFINET node under both roots; nothing was changed.").ToString();

            Subnet? target;
            try { target = HardwareFindSubnet(subnetName); }
            catch (Exception ex) { return sb.AppendLine("Result: NOT CONNECTED: invalid subnetName: " + HardwareExceptionDetail(ex)).ToString(); }
            sb.AppendLine("Project.Subnets '" + subnetName + "': " + (target == null ? "<absent>" : "exists"));

            // Both nodes must be free or already on the target before anything is written.
            string? Conflict(string label, Node node)
                => node.ConnectedSubnet != null && (target == null || !object.Equals(node.ConnectedSubnet, target))
                    ? label + " node is attached to subnet '" + node.ConnectedSubnet.Name + "', not to '" + subnetName + "'" : null;
            var conflict = Conflict("PLC", plc) ?? Conflict("HMI", hmi);
            if (conflict != null) return sb.AppendLine("Result: NOT CONNECTED: " + conflict + "; disconnect it explicitly first. Nothing was changed.").ToString();

            try
            {
                if (target == null)
                {
                    target = plc.CreateAndConnectToSubnet(subnetName);
                    sb.AppendLine("PLC CreateAndConnectToSubnet: " + (target == null ? "NULL" : "created '" + target.Name + "'"));
                    if (target == null) return sb.AppendLine("Result: NOT CONNECTED: CreateAndConnectToSubnet returned null; inspect the project.").ToString();
                }
                else if (plc.ConnectedSubnet == null)
                {
                    plc.ConnectToSubnet(target);
                    sb.AppendLine("PLC ConnectToSubnet(existing '" + target.Name + "') called");
                }
                else sb.AppendLine("PLC node is on '" + target.Name + "' (no call)");

                if (hmi.ConnectedSubnet == null)
                {
                    hmi.ConnectToSubnet(target);
                    sb.AppendLine("HMI ConnectToSubnet('" + target.Name + "') called");
                }
                else sb.AppendLine("HMI node is on '" + target.Name + "' (no call)");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Subnet create/connect error: " + HardwareExceptionDetail(ex));
            }

            sb.AppendLine("Readback PLC node: " + HardwareFormatNodeInfo(plcNode));
            sb.AppendLine("Readback HMI node: " + HardwareFormatNodeInfo(hmiNode));
            bool verified = target != null && object.Equals(plc.ConnectedSubnet, target) && object.Equals(hmi.ConnectedSubnet, target)
                && object.Equals(HardwareFindSubnet(subnetName), target);

            try
            {
                var container = HardwareSoftwareContainerUnder(hmiRoot);
                var sw = container?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("HMI Connections after subnet (" + (sw == null ? "no HMI software under " + hmiRootPath : "software '" + (TryGetName(sw) ?? "<unnamed>") + "'") + "):");
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

            sb.AppendLine(verified
                ? "Result: ConnectToSubnet: OK (both nodes read back on subnet '" + subnetName + "')"
                : "Result: NOT CONNECTED: Node.ConnectedSubnet read-back does not show both nodes on '" + subnetName + "'; inspect the project.");
            return sb.ToString();
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
                if (container?.Software != null && (container.Software.GetType().FullName ?? "").IndexOf("Hmi", StringComparison.Ordinal) >= 0) return container;
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
