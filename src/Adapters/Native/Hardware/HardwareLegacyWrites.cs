using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Engineering;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private string NetworkAdmissionFailure(Exception error, Dictionary<string, object?> meta)
        {
            bool lost = HmiReadPolicy.ConnectionUnavailable(error);
            if (lost) meta["connectionUnavailable"] = true;
            meta["mayHaveChanged"] = false;
            meta["failureCode"] = lost ? "CONNECTION_UNAVAILABLE" : "PRECONDITION_FAILED";
            meta["nativeMessage"] = HardwareExceptionDetail(error);
            return lost ? "Portal connection unavailable before the network operation started."
                : "Exclusive access refused before the network operation started.";
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
            bool nestedWrite = EngineeringWriteJournal.Active;
            using var journal = new EngineeringWriteJournal();
            IDisposable? editAccess = null;
            bool acquiringLease = false;

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }

                if (!SubnetTypePolicy.IsSupported(subnetType))
                {
                    meta["failureCode"] = "INVALID_ARGUMENT";
                    meta["status"] = "InvalidParams";
                    meta["parameter"] = "subnetType";
                    meta["nativeMessage"] = SubnetTypePolicy.Message;
                    meta["allowedValues"] = SubnetTypePolicy.AllowedValues;
                    meta["error"] = meta["nativeMessage"];
                    return new HardwareAddressingReply { Message = "Invalid subnetType", Meta = meta };
                }
                if (!nestedWrite)
                {
                    acquiringLease = true;
                    editAccess = EngineeringPlcSession.AcquireHmiEditAccess();
                    acquiringLease = false;
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
                    meta["refusalReason"] = "The anchor node is attached to subnet '" + anchorNode.ConnectedSubnet.Name + "', not to '" + subnetName
                        + "'; choose a free anchor node or disconnect it explicitly first. Nothing was created.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Anchor node is attached to another subnet; nothing was created", Meta = meta };
                }

                meta["mayHaveChanged"] = true;
                EngineeringWriteJournal.Creating(() => HardwareFindSubnet(subnetName), "Project.Subnets/" + subnetName);
                EngineeringWriteJournal
                    .ConnectedNode(anchorDeviceItemPath + "/Nodes/" + anchorNode.Name, () => anchorNode.ConnectedSubnet != null, () => {
                        anchorNode.DisconnectFromSubnet(); });
                var subnet = anchorNode.CreateAndConnectToSubnet(subnetName);
                meta["created"] = subnet != null;
                meta["createdSubnetName"] = subnet?.Name;
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = subnet != null && object.Equals(anchorNode.ConnectedSubnet, subnet) && object.Equals(HardwareFindSubnet(subnetName), subnet);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Subnet created on the anchor node and read back (Project.Subnets and Node.ConnectedSubnet)"
                        : "Subnet creation did not read back as the anchor node's subnet",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                EngineeringWriteJournal.ObserveFailure(ex);
                meta["error"] = HardwareExceptionDetail(ex);
                if (acquiringLease)
                    return new HardwareAddressingReply { Message = NetworkAdmissionFailure(ex, meta), Meta = meta };
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                return new HardwareAddressingReply { Message = "Failed ensuring subnet", Meta = meta };
            }
            finally
            {
                if (meta["success"] is true) journal.Commit();
                else journal.Rollback(meta);
                editAccess?.Dispose();
            }
        }

        public HardwareAddressingReply HardwareAttachDeviceNodeToSubnet(string deviceItemPath, int interfaceIndex, string subnetName,
                string anchorDeviceItemPath = "")
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
            bool nestedWrite = EngineeringWriteJournal.Active;
            using var journal = new EngineeringWriteJournal();
            IDisposable? editAccess = null;
            bool acquiringLease = false;

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }
                if (!nestedWrite)
                {
                    acquiringLease = true;
                    editAccess = EngineeringPlcSession.AcquireHmiEditAccess();
                    acquiringLease = false;
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
                    meta["refusalReason"] = "The node is attached to subnet '" + connected.Name + "', not to '" + subnetName
                        + "'; disconnect it explicitly first. Nothing was changed.";
                    meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                    return new HardwareAddressingReply { Message = "Node is attached to another subnet; nothing was changed", Meta = meta };
                }
                if (connected == null)
                {
                    meta["mayHaveChanged"] = true;
                    EngineeringWriteJournal
                        .ConnectedNode(deviceItemPath + "/Nodes/" + node.Name, () => node.ConnectedSubnet != null, () => { node.DisconnectFromSubnet(); });
                    node.ConnectToSubnet(subnet);
                }
                else meta["alreadyConnected"] = true;

                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                var ok = object.Equals(node.ConnectedSubnet, subnet);
                meta["success"] = ok;
                return new HardwareAddressingReply
                {
                    Message = ok ? "Device network node reads back on the subnet (Node.ConnectedSubnet)"
                        : "Device network node attach did not read back on the subnet",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                EngineeringWriteJournal.ObserveFailure(ex);
                meta["error"] = HardwareExceptionDetail(ex);
                if (acquiringLease)
                    return new HardwareAddressingReply { Message = NetworkAdmissionFailure(ex, meta), Meta = meta };
                meta["readback"] = HardwareBuildSubnetReadbackJson(subnetName);
                return new HardwareAddressingReply { Message = "Failed attaching device node to subnet", Meta = meta };
            }
            finally
            {
                if (meta["success"] is true) journal.Commit();
                else journal.Rollback(meta);
                editAccess?.Dispose();
            }
        }

    }
}
