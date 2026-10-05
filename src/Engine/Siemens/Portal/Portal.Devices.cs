using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region devices

#if TIA_V20
        internal const int PortalMajorVersion = 20;
#else
        internal const int PortalMajorVersion = 21;
#endif

        public string GetProjectTree()
        {
            _logger?.LogInformation("Getting project tree...");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            StringBuilder sb = new();

            sb.AppendLine($"{_project?.Name}");

            var ancestorStates = new List<bool>();
            var sections = new List<Action>();
            
            if (_project?.Devices != null && _project.Devices.Count > 0)
            {
                sections.Add(() => GetProjectTreeDevices(sb, _project.Devices, ancestorStates));
            }
            
            if (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0)
            {
                sections.Add(() => GetProjectTreeGroups(sb, _project.DeviceGroups, ancestorStates));
            }
            
            if (_project?.UngroupedDevicesGroup != null)
            {
                sections.Add(() => GetProjectTreeUngroupedDeviceGroup(sb, _project.UngroupedDevicesGroup, ancestorStates));
            }
            
            for (int i = 0; i < sections.Count; i++)
            {
                var isLastSection = i == sections.Count - 1;
                if (i == 0)
                {
                    sections[i]();
                }
                else
                {
                    sections[i]();
                }
            }

            return sb.ToString();
        }


        public List<Device> GetDevices(string regexName = "")
        {
            _logger?.LogInformation("Getting devices...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<Device>();

            if (_project?.Devices != null)
            {
                foreach (Device device in _project.Devices)
                {
                    list.Add(device);
                }

                foreach (var group in _project.DeviceGroups)
                {
                    GetDevicesRecursive(group, list, regexName);
                }

            }

            return list;
        }

        public Device? GetDevice(string devicePath)
        {
            _logger?.LogInformation($"Getting device by path: {devicePath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceByPath(devicePath);
        }


        private static IEnumerable<object> FindHardwareCatalogEntries(object catalog, string filter)
        {
            var find = catalog.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (find == null) yield break;

            var value = find.Invoke(catalog, new object[] { filter });
            if (value is not IEnumerable enumerable) yield break;

            foreach (var item in enumerable)
            {
                if (item != null) yield return item;
            }
        }


        // In V21, the stable path is: generate/import standard block XML -> ImportBlock/ImportBlocksFromDirectory -> CompileSoftware.

        public DeviceItem? GetDeviceItem(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting device item by path: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceItemByPath(deviceItemPath);

        }

        public string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4)
        {
            _logger?.LogInformation($"Getting device item tree by path: {deviceItemPath}, depth={maxDepth}");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name} [DeviceItem]");
            BuildDeviceItemTree(sb, root, new List<bool>(), 0, Math.Max(0, maxDepth));
            return sb.ToString();
        }

        public List<ModelContextProtocol.NetworkAttribute>? GetDeviceItemNetworkInfo(string deviceItemPath)
        {
            if (IsProjectNull()) return null;
            var di = GetDeviceItemByPath(deviceItemPath);
            if (di == null) return null;

            // Heuristic: filter attribute names that likely contain network addressing / interface identity.
            var keys = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address"
            };

            var list = new List<ModelContextProtocol.NetworkAttribute>();
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

                    list.Add(new ModelContextProtocol.NetworkAttribute
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


        // ----- Network topology / IP read-out (Openness as the source of truth) -----
        // Replaces the old "probe S7 / hand-parse exported AML" workaround for finding a PLC's IP.

        private static string? ReadNodeAddress(object node)
            => TryGetPropertyValue(node, "Address")?.ToString()
               ?? TryGetPropertyValue(node, "IpAddress")?.ToString()
               ?? TryGetPropertyValue(node, "IPAddress")?.ToString()
               ?? TryGetEngineeringAttribute(node, "Address")?.ToString()
               ?? TryGetEngineeringAttribute(node, "IpAddress")?.ToString();

        private JsonArray BuildDeviceNodesJson(Device device)
        {
            var arr = new JsonArray();
            foreach (var root in device.DeviceItems)
            {
                foreach (var n in FindNetworkNodes(root))
                {
                    var subnet = TryGetPropertyValue(n.Node, "ConnectedSubnet");
                    arr.Add(new JsonObject
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

        // ----- PUT/GET access (dependency check for S7 reads) -----

        private static string NormalizeAttrName(string? n)
            => new string((n ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Locate the CPU DeviceItem + attribute controlling "Permit access with PUT/GET communication".
        // The exact Openness attribute name varies by CPU/firmware, so match any attribute whose
        // normalized (lowercase, alphanumeric-only) name contains "putget".
        // NOTE: S7-1200 (e.g. 1211C V4.6) does not expose this attribute via Openness at all — neither
        // enumerated by GetAttributeInfos() nor gettable by name — so this returns (null, null) there.
        // Use DumpDeviceAttributes to see exactly what a given CPU exposes.
        private (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device)
        {
            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in TraverseDeviceItems(root, root.Name))
                {
                    var it = tup.Item1;
                    string[] names;
                    try { names = it.GetAttributeInfos().Select(x => x.Name ?? string.Empty).ToArray(); }
                    catch { /* swallow(enumerate-optional): Skip device items whose attribute inventory is unavailable while locating PUT/GET support. */ continue; }
                    var match = names.FirstOrDefault(n => NormalizeAttrName(n).Contains("putget"));
                    if (!string.IsNullOrEmpty(match)) return (it, match);
                }
            }
            return (null, null);
        }

        private static bool AttrValueIsEnabled(object? v)
            => v is bool b ? b : (v?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false);

        // Read whether a CPU permits remote PUT/GET access (precondition for ReadPlcLiveValuesS7 on DB areas).
        public JsonObject GetPutGetAccess(string devicePath)
        {
            if (IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var (item, attrName) = FindPutGetAttribute(device);
            if (item == null || attrName == null)
                return new JsonObject
                {
                    ["found"] = false,
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access is not exposed as an Openness attribute on this CPU/firmware " +
                                  "(confirmed e.g. on S7-1200 1211C V4.6). Check/set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication'. " +
                                  "Note: M/I/Q S7 reads still work when the channel is permitted; a successful M-area read implies PUT/GET is enabled."
                };

            object? val = null;
            try { val = item.GetAttribute(attrName); } catch { /* swallow(probe-optional): An unavailable PUT/GET value leaves the existing null readback for this CPU. */ }
            return new JsonObject
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["enabled"] = AttrValueIsEnabled(val),
                ["rawValue"] = val?.ToString() ?? string.Empty
            };
        }

        public string ProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNodes = FindNetworkNodes(plcRoot).ToList();
            var hmiNodes = FindNetworkNodes(hmiRoot).ToList();
            sb.AppendLine("PLC item service scan:");
            foreach (var line in DescribeNetworkServiceScan(plcRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("HMI item service scan:");
            foreach (var line in DescribeNetworkServiceScan(hmiRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("PLC nodes:");
            foreach (var n in plcNodes) sb.AppendLine("  " + FormatNodeInfo(n));
            sb.AppendLine("HMI nodes:");
            foreach (var n in hmiNodes) sb.AppendLine("  " + FormatNodeInfo(n));

            var plcNode = plcNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = hmiNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
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
                sb.AppendLine("PLC subnet create/connect error: " + FormatExceptionDetail(ex));
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
                    sb.AppendLine("HMI ConnectToSubnet error: " + FormatExceptionDetail(ex));
                }
            }

            sb.AppendLine("Readback PLC node: " + FormatNodeInfo(plcNode));
            sb.AppendLine("Readback HMI node: " + FormatNodeInfo(hmiNode));

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
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
                sb.AppendLine("HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        public string ProbeDeviceNetworkExposure(string deviceItemPath)
        {
            var sb = new StringBuilder();
            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return $"DeviceItem not found: {deviceItemPath}";
            }

            sb.AppendLine($"Network exposure probe for: {deviceItemPath}");
            sb.AppendLine("DeviceItem tree:");
            sb.AppendLine(GetDeviceItemTree(deviceItemPath, 5));

            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                sb.AppendLine($"Item: path={item.Path}; kind={item.Kind}; owner={item.DeviceItem.Name}; type={item.Object.GetType().FullName}");

                try
                {
                    var attrs = TryReadInterestingAttributes(item.Object).ToList();
                    if (attrs.Count > 0)
                    {
                        sb.AppendLine("  Attributes:");
                        foreach (var attr in attrs)
                            sb.AppendLine("    " + attr);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Attributes error: " + FormatExceptionDetail(ex));
                }

                try
                {
                    var services = ProbeInterestingServices(item.Object).ToList();
                    if (services.Count > 0)
                    {
                        sb.AppendLine("  Services:");
                        foreach (var svc in services)
                            sb.AppendLine("    " + svc);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Services error: " + FormatExceptionDetail(ex));
                }
            }

            return sb.ToString();
        }

        public string ProbeCreateHardwareHmiConnection(string plcRootPath, string hmiRootPath, string connectionName, bool createConnection = false, bool deepScan = false)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            sb.AppendLine("CreateConnection: " + createConnection);
            sb.AppendLine("DeepScan: " + deepScan);
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

#if TIA_V20
            // V20 does not expose Siemens.Engineering.HW.CommunicationConnections. The hardware-level
            // HMI connection helper degrades to a no-op (caller still gets the diagnostic prefix).
            var connectionCompositionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var hmiConnectionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.HmiConnection, Siemens.Engineering");
            if (connectionCompositionType == null || hmiConnectionType == null)
            {
                sb.AppendLine(Capability.Describe(TiaFeature.HardwareHmiConnection) + " Skipping hardware HMI connection creation.");
                return sb.ToString();
            }
#else
            var connectionCompositionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition);
            var hmiConnectionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.HmiConnection);
#endif
            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            sb.AppendLine("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                sb.AppendLine("Candidate: " + c.Label + " targetType=" + c.Target.GetType().FullName);
                object? composition = null;
                try
                {
                    composition = TryGetService(c.Target, connectionCompositionType);
                    sb.AppendLine("  ConnectionComposition service: " + (composition == null ? "<none>" : composition.GetType().FullName));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  ConnectionComposition service error: " + FormatExceptionDetail(ex));
                }

                if (composition == null) continue;

                try
                {
                    sb.AppendLine("  Before count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                }
                catch /* swallow(probe-optional): the optional pre-action collection count must not prevent the action */ { }

                try
                {
                    var create = composition.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
                    if (create == null)
                    {
                        sb.AppendLine("  Create<T>(Node,DeviceItem,Node) not found.");
                        continue;
                    }

                    sb.AppendLine("  Create<T> signature: " + create);
                    if (!createConnection)
                    {
                        sb.AppendLine("  Create skipped (scan-only mode).");
                        continue;
                    }

                    var generic = create.MakeGenericMethod(hmiConnectionType);
                    var created = generic.Invoke(composition, new object[] { c.LocalNode, c.PartnerTarget, c.PartnerNode });
                    sb.AppendLine("  Create<HmiConnection>: " + (created == null ? "NULL" : "OK " + created.GetType().FullName));
                    if (created != null)
                    {
                        TrySetProperty(created, "LocalConnectionName", connectionName);
                        sb.AppendLine("  Created readback: " + SummarizeHmiObjectReadback(created, "LocalConnectionName", "LocalAddress", "PartnerAddress", "AccessPoint", "Online", "TimeSynchronizationMode"));
                    }

                    try
                    {
                        sb.AppendLine("  After count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                    }
                    catch /* swallow(probe-optional): the optional post-action collection count must not replace the action result */ { }

                    break;
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Create<HmiConnection> error: " + FormatExceptionDetail(ex));
                }
            }

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("Classic HMI Connections after HW probe:");
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
                sb.AppendLine("Classic HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        private IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

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

            if (_project != null)
            {
                foreach (var c in Add("Project", _project, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;
                foreach (var c in Add("Project devices", _project.Devices, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;

                foreach (var d in _project.Devices)
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

        private static IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildDirectHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            yield return ("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node);
        }

        internal readonly struct NetworkNodeInfo
        {
            public NetworkNodeInfo(string path, DeviceItem item, object networkInterface, object node)
            {
                Path = path;
                Item = item;
                NetworkInterface = networkInterface;
                Node = node;
            }

            public string Path { get; }
            public DeviceItem Item { get; }
            public object NetworkInterface { get; }
            public object Node { get; }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static IEnumerable<NetworkNodeInfo> FindNetworkNodes(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
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

        private static IEnumerable<(DeviceItem Item, string Path)> TraverseDeviceItems(DeviceItem root, string path)
        {
            yield return (root, path);
            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItems(child, path + "/" + child.Name))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<string> DescribeNetworkServiceScan(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
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

        private static IEnumerable<(object Object, DeviceItem DeviceItem, string Path, string Kind)> TraverseDeviceItemsAndHardware(DeviceItem root, string path)
            => TraverseDeviceItemsAndHardware(root, path, new HashSet<DeviceItem>());

        // hardware components that are DeviceItems of their own (Comfort panel: the head item lists MCP_TP700.IE_CP_1 only
        // in Items, and the PROFINET interface sits two levels below it) are walked as well, so the panel's Ethernet node is found
        // (real project: ConnectDeviceNodesToProfinetSubnet "Selected HMI node: <none>" on a TP700 Comfort V17).
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

        private static IEnumerable<string> TryReadInterestingAttributes(object target)
        {
            var result = new List<string>();
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getInfos == null || getAttr == null)
                return result;

            var infos = getInfos.Invoke(target, Array.Empty<object>()) as IEnumerable;
            if (infos == null)
                return result;

            var interesting = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address", "partner", "node"
            };

            foreach (var info in infos)
            {
                var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var lower = name.ToLowerInvariant();
                if (!interesting.Any(k => lower.Contains(k)))
                    continue;

                try
                {
                    var value = getAttr.Invoke(target, new object[] { name });
                    result.Add($"{name}={value ?? ""}");
                }
                catch /* swallow(probe-optional): unreadable attributes are omitted from the diagnostic summary */ { }
            }

            return result;
        }

        private JsonArray BuildDeviceItemNetworkReadbackJson(string deviceItemPath)
        {
            var arr = new JsonArray();
            var attrs = GetDeviceItemNetworkInfo(deviceItemPath) ?? new List<ModelContextProtocol.NetworkAttribute>();
            foreach (var attr in attrs)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = attr.Name ?? string.Empty,
                    ["value"] = attr.Value ?? string.Empty,
                    ["dataType"] = attr.DataType ?? string.Empty,
                    ["isWritable"] = attr.IsWritable
                });
            }
            return arr;
        }

        private static IEnumerable<string> ProbeInterestingServices(object target)
        {
            var serviceSuffixes = new[]
            {
                "NetworkInterface",
                "NetworkPort",
                "Node",
                "SubnetOwner",
                "TransferArea",
                "InterfaceOperatingMode",
                "CommunicationConnections",
                "CommunicationConnection",
                "AddressController",
                "HardwareObject"
            };

            foreach (var suffix in serviceSuffixes)
            {
                object? svc = null;
                try { svc = TryGetServiceByTypeSuffix(target, suffix); } catch /* swallow(probe-optional): unsupported optional services are omitted from device diagnostics */ { }
                if (svc == null) continue;

                var details = new List<string>
                {
                    $"service={svc.GetType().FullName}"
                };

                var nodes = TryGetPropertyValue(svc, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add($"{TryGetName(node) ?? "<unnamed>"}:{TryGetPropertyValue(node, "NodeType") ?? ""}:{TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"}");
                    }
                    details.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }

                foreach (var propName in new[] { "Name", "Type", "Mode", "Address", "Subnet", "ConnectedSubnet", "NodeType" })
                {
                    try
                    {
                        var value = TryGetPropertyValue(svc, propName);
                        if (value != null)
                            details.Add(propName + "=" + value);
                    }
                    catch /* swallow(probe-optional): unavailable service properties are omitted from device diagnostics */ { }
                }

                var memberSummaries = DescribeMembers(svc, 80)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(40)
                    .ToList();
                if (memberSummaries.Count > 0)
                    details.Add("Members=[" + string.Join(" | ", memberSummaries) + "]");

                yield return suffix + " => " + string.Join("; ", details);
            }
        }

        private static string FormatNodeInfo(NetworkNodeInfo info)
        {
            return $"path={info.Path}; item={info.Item.Name}; node={TryGetName(info.Node) ?? "<unnamed>"}; type={TryGetPropertyValue(info.Node, "NodeType")}; connectedSubnet={TryGetName(TryGetPropertyValue(info.Node, "ConnectedSubnet")) ?? TryGetPropertyValue(info.Node, "ConnectedSubnet")?.ToString() ?? "<none>"}";
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


        private static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            // Hardware components (Items)
            if (node.Items != null && node.Items.Count > 0)
            {
                var items = node.Items.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = (i == items.Count - 1) && (node.DeviceItems == null || node.DeviceItems.Count == 0);
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            // Sub device items
            if (node.DeviceItems != null && node.DeviceItems.Count > 0)
            {
                var children = node.DeviceItems.ToList();
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var isLast = i == children.Count - 1;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                    BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
                }
            }
        }

        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }


        public ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false
            };

            var problems = new JsonArray();
            var devices = new JsonArray();
            var software = new JsonArray();
            meta["problems"] = problems;
            meta["devices"] = devices;
            meta["software"] = software;

            if (IsProjectNull())
            {
                problems.Add("Project is null. Use ConnectPortal + AttachOpenProject/OpenProject first.");
                return new ResponseMessage { Message = "Automation context invalid", Meta = meta };
            }

            meta["project"] = _project?.Name ?? string.Empty;

            try
            {
                foreach (var d in _project!.Devices)
                {
                    var deviceInfo = new JsonObject
                    {
                        ["name"] = d.Name,
                        ["type"] = d.GetType().FullName ?? d.GetType().Name
                    };
                    devices.Add(deviceInfo);

                    foreach (var di in d.DeviceItems)
                    {
                        TryAddSoftwareInfo(di, software);
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Device/software scan failed: {FormatExceptionDetail(ex)}");
            }

            if (devices.Count == 0)
            {
                problems.Add("No devices found in current project.");
            }

            if (!string.IsNullOrWhiteSpace(expectedPlcSoftwarePath))
            {
                var plc = GetSoftwareContainer(expectedPlcSoftwarePath)?.Software as PlcSoftware;
                meta["expectedPlcSoftwarePath"] = expectedPlcSoftwarePath;
                meta["expectedPlcFound"] = plc != null;
                if (plc == null) problems.Add($"PLC software not found at '{expectedPlcSoftwarePath}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHmiSoftwarePath))
            {
                var hmi = GetSoftwareContainer(expectedHmiSoftwarePath)?.Software;
                meta["expectedHmiSoftwarePath"] = expectedHmiSoftwarePath;
                meta["expectedHmiFound"] = hmi != null && hmi.GetType().FullName?.Contains("Hmi", StringComparison.OrdinalIgnoreCase) == true;
                if (hmi == null) problems.Add($"HMI software not found at '{expectedHmiSoftwarePath}'.");
            }

            try
            {
                meta["projectTree"] = GetProjectTree();
            }
            catch (Exception ex)
            {
                problems.Add($"Project tree failed: {FormatExceptionDetail(ex)}");
            }

            meta["success"] = problems.Count == 0;
            return new ResponseMessage
            {
                Message = problems.Count == 0 ? "Automation context OK" : "Automation context has problems",
                Meta = meta
            };

            void TryAddSoftwareInfo(DeviceItem item, JsonArray target)
            {
                try
                {
                    var sc = item.GetService<SoftwareContainer>();
                    if (sc?.Software != null)
                    {
                        target.Add(new JsonObject
                        {
                            ["deviceItem"] = item.Name,
                            ["softwareName"] = TryGetName(sc.Software) ?? item.Name,
                            ["softwareType"] = sc.Software.GetType().FullName ?? sc.Software.GetType().Name
                        });
                    }
                }
                catch /* swallow(enumerate-optional): Unavailable software services or children are skipped while retaining the remaining preflight inventory. */ { }

                try
                {
                    foreach (var child in item.DeviceItems)
                    {
                        TryAddSoftwareInfo(child, target);
                    }
                }
                catch /* swallow(enumerate-optional): Unavailable software services or children are skipped while retaining the remaining preflight inventory. */ { }
            }
        }

        #endregion
    }
}
