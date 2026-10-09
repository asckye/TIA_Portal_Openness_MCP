using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using static TiaMcp.Adapters.Hardware.HardwareAddressPrimitives;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private Subnet HardwareExactSubnet(string subnetName)
            => HardwareGroupOperations.Find(project!.Subnets, HardwareNetworkPolicy.RequireExactName(subnetName, "subnetName")) as Subnet
               ?? throw new HardwareAddressingException("NotFound", "Subnet not found: " + subnetName);

        private NetworkInterface HardwareRequireNetworkInterface(HardwareObject item, string parameter)
            => HardwareServiceProvider(item).GetService<NetworkInterface>() ?? throw new InvalidOperationException(parameter + " must identify a DeviceItem that exposes NetworkInterface.");

        private Dictionary<string, object?> HardwareIoSystemRow(IoSystem system) => new Dictionary<string, object?>
        {
            ["name"] = system.Name, ["number"] = system.Number, ["subnetName"] = system.Subnet?.Name,
            ["connectedIoDevices"] = new List<object?>(HardwareGroupOperations.Items(system.ConnectedIoDevices).Select(c => (object)HardwareOwnerPath(c)).ToArray()),
            ["hwIdentifiers"] = new List<object?>(HardwareGroupOperations.Items(system.HwIdentifiers).Cast<HwIdentifier>().Select(h => (object)h.Identifier).ToArray()),
            ["attributes"] = DynamicAttributes(system, HardwareNetworkPolicy.IoSystemAttributes)
        };

        private Dictionary<string, object?> HardwareIoControllerRow(IoController controller) => new Dictionary<string, object?>
        {
            ["ownerPath"] = HardwareOwnerPath(controller), ["ioSystemName"] = controller.IoSystem?.Name,
            ["addresses"] = new List<object?>(HardwareGroupOperations.Items(controller.Addresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
            ["attributes"] = DynamicAttributes(controller, HardwareNetworkPolicy.IoControllerAttributes)
        };

        private Dictionary<string, object?> HardwareIoConnectorRow(IoConnector connector)
        {
            IoController? remote = null; try { remote = connector.GetIoController(); } catch { /* swallow(probe-optional): A connector without a readable remote controller still reports its local attributes and a null controller path. */ }
            return new Dictionary<string, object?>
            {
                ["ownerPath"] = HardwareOwnerPath(connector), ["connectedToIoSystem"] = connector.ConnectedToIoSystem?.Name,
                ["ioControllerOwnerPath"] = remote == null ? null : HardwareOwnerPath(remote),
                ["attributes"] = DynamicAttributes(connector, HardwareNetworkPolicy.IoConnectorAttributes)
            };
        }

#if PLC_HARDWARE_DOMAINS
        private Dictionary<string, object?> HardwareSyncDomainRow(SyncDomain domain) => new Dictionary<string, object?>
        {
            ["name"] = domain.Name, ["convertedName"] = domain.ConvertedName, ["isDefault"] = domain.IsDefault,
            ["participants"] = new List<object?>(HardwareGroupOperations.Items(domain.DomainParticipants).Select(p => (object)HardwareOwnerPath(p)).ToArray()),
            ["attributes"] = DynamicAttributes(domain, HardwareNetworkPolicy.SyncDomainAttributes)
        };
#endif

#if PLC_HARDWARE_DOMAINS
        private Dictionary<string, object?> HardwareMrpDomainRow(MrpDomain domain) => new Dictionary<string, object?>
        {
            ["name"] = domain.Name,
            ["participants"] = new List<object?>(HardwareGroupOperations.Items(domain.DomainParticipants).Select(p => (object)HardwareOwnerPath(p)).ToArray()),
            ["attributes"] = DynamicAttributes(domain, HardwareNetworkPolicy.MrpDomainAttributes)
        };
#endif

#if PLC_HARDWARE_MRP_INSTANCES
        private Dictionary<string, object?> HardwareMrpInstanceRow(MrpInstance instance) => new Dictionary<string, object?>
        {
            ["name"] = instance.Name, ["connectedMrpDomain"] = instance.ConnectedMrpDomain?.Name, ["interfaceOwnerPath"] = HardwareOwnerPath(instance.Interface),
            ["ringPort1"] = instance.RingPort1 == null ? null : HardwareOwnerPath(instance.RingPort1), ["ringPort2"] = instance.RingPort2 == null ? null : HardwareOwnerPath(instance.RingPort2)
        };
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        private Dictionary<string, object?> HardwareMappingRuleRow(TransferAreaMappingRule rule, int index) => new Dictionary<string, object?>
        {
            ["index"] = index, ["positionNumber"] = rule.PositionNumber, ["begin"] = rule.Begin, ["end"] = rule.End, ["offset"] = rule.Offset,
            ["ioType"] = rule.IoType.ToString(), ["targetPath"] = rule.Target == null ? null : HardwareOwnerPath(rule.Target)
        };
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        private Dictionary<string, object?> HardwareTransferAreaRow(TransferArea area) => new Dictionary<string, object?>
        {
            ["kind"] = "standard", ["name"] = area.Name, ["type"] = area.Type.ToString(), ["direction"] = area.Direction.ToString(),
            ["positionNumber"] = area.PositionNumber,
#if PLC_HARDWARE_DOMAINS
            ["extendedPositionNumber"] = area.ExtendedPositionNumber,
#else
            ["extendedPositionNumber"] = null,
#endif
            ["localToPartnerLength"] = area.LocalToPartnerLength, ["partnerToLocalLength"] = area.PartnerToLocalLength,
            ["localAddresses"] = new List<object?>(HardwareGroupOperations.Items(area.LocalAddresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
            ["partnerAddresses"] = new List<object?>(HardwareGroupOperations.Items(area.PartnerAddresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
            ["mappingRules"] = new List<object?>(HardwareGroupOperations.Items(area.TransferAreaMappingRules).Cast<TransferAreaMappingRule>().Select((r, i) => (object)HardwareMappingRuleRow(r, i)).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkPolicy.TransferAreaAttributes)
        };
#endif

#if PLC_HARDWARE_TRANSFER_MULTICAST
        private Dictionary<string, object?> HardwareMulticastRow(MulticastableTransferArea area) => new Dictionary<string, object?>
        {
            ["kind"] = "multicast", ["name"] = area.Name, ["type"] = area.Type.ToString(), ["direction"] = area.Direction.ToString(), ["comment"] = area.Comment,
#if PLC_HARDWARE_MRP_INSTANCES
            ["dataLength"] = area.DataLength,
            ["addresses"] = new List<object?>(HardwareGroupOperations.Items(area.Addresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
#else
            ["dataLength"] = null, ["addresses"] = Array.Empty<object>(),
#endif
            ["partnerTransferAreas"] = new List<object?>(HardwareGroupOperations.Items(area.PartnerTransferAreas).Cast<MulticastableTransferArea>()
                .Select(p => (object)new Dictionary<string, object?> { ["name"] = p.Name, ["ownerPath"] = HardwareOwnerPath(p) }).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkPolicy.MulticastTransferAreaAttributes)
        };
#endif

        private static Dictionary<string, string> HardwareChannelAttributeModes(Channel channel)
        {
            var modes = new Dictionary<string, string>(StringComparer.Ordinal);
            try { foreach (var info in channel.GetAttributeInfos()) modes[info.Name] = info.AccessMode.ToString(); } catch { /* swallow(enumerate-optional): Retain any reported access modes; unavailable entries remain unlisted and native writes retain their own validation. */ }
            return modes;
        }

        private Dictionary<string, object?> HardwareChannelRow(Channel channel, string[] extraAttributes)
        {
            var modes = HardwareChannelAttributeModes(channel);
            return new Dictionary<string, object?>
            {
                ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(),
                ["attributeNames"] = new List<object?>(modes.Select(m => (object)new Dictionary<string, object?> { ["name"] = m.Key, ["accessMode"] = m.Value }).ToArray()),
                ["attributes"] = DynamicAttributes(channel, HardwareNetworkPolicy.ChannelAttributes.Concat(extraAttributes).Distinct(StringComparer.Ordinal).ToArray())
            };
        }

        private Dictionary<string, object?> HardwarePortRow(NetworkPort port) => new Dictionary<string, object?>
        {
            ["ownerPath"] = HardwareOwnerPath(port), ["interfaceOwnerPath"] = port.Interface == null ? null : HardwareOwnerPath(port.Interface),
            ["connectedPorts"] = new List<object?>(HardwareGroupOperations.Items(port.ConnectedPorts).Select(p => (object)HardwareOwnerPath(p)).ToArray())
        };

#if PLC_HARDWARE_TRANSFER_AREAS
        private static TransferAreaType HardwareParseTransferAreaType(string type)
        {
            if (!Enum.IsDefined(typeof(TransferAreaType), type)) throw new NotSupportedException("TransferAreaType." + type + " is not defined by the connected TIA Portal Openness version.");
            return (TransferAreaType)Enum.Parse(typeof(TransferAreaType), type);
        }
#endif

        public HardwareAddressingReply HardwareReadIoSystems(string subnetName = "", string[] devicePathJson = null!, string[] itemPathJson = null!, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("ListIoSystems", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit);
                bool bySubnet = !string.IsNullOrEmpty(subnetName), byInterface = devicePathJson != null && devicePathJson.Length > 0;
                if (bySubnet == byInterface) throw new ArgumentException("Give exactly one scope: subnetName, or devicePathJson/itemPathJson of an interface device item.");
                var rows = new List<object>();
                if (bySubnet)
                {
                    var subnet = HardwareExactSubnet(subnetName); meta["subnet"] = HardwareScalarEvidence.Read(subnet);
                    rows.AddRange(HardwareGroupOperations.Items(subnet.IoSystems).Cast<IoSystem>().Select(s => (object)HardwareIoSystemRow(s)));
                }
                else
                {
                    var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                    meta["interface"] = new Dictionary<string, object?> { ["ownerPath"] = HardwareOwnerPath(network), ["interfaceType"] = network.InterfaceType.ToString(), ["interfaceOperatingMode"] = network.InterfaceOperatingMode.ToString() };
                    var controllers = HardwareGroupOperations.Items(network.IoControllers).Cast<IoController>().ToArray();
                    var connectors = HardwareGroupOperations.Items(network.IoConnectors).Cast<IoConnector>().ToArray();
                    meta["ioControllers"] = new List<object?>(controllers.Select(c => (object)HardwareIoControllerRow(c)).ToArray());
                    meta["ioConnectors"] = new List<object?>(connectors.Select(c => (object)HardwareIoConnectorRow(c)).ToArray());
                    rows.AddRange(controllers.Where(c => c.IoSystem != null).Select(c => (object)HardwareIoSystemRow(c.IoSystem)));
                    rows.AddRange(connectors.Where(c => c.ConnectedToIoSystem != null).Select(c => (object)HardwareIoSystemRow(c.ConnectedToIoSystem)));
                }
                HardwarePage(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "IoSystem Name/Number/Subnet, ConnectedIoDevices owner paths, HwIdentifiers and the official dynamic attributes (per-attribute failures listed); IoController/IoConnector rows when scoped by interface.";
                return "IO systems read; no modification.";
            });

        public HardwareAddressingReply HardwareManageIoSystem(string[] devicePathJson, string[] itemPathJson, string action, string name = "", string subnetName = "", string ioSystemName = "",
            Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!, bool confirmDelete = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageIoSystem", meta => {
                HardwareNetworkPolicy.ValidateIoSystemRequest(action, name, subnetName, ioSystemName);
                if (action == "delete") HardwareServicesPolicy.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                if (action == "connect" || action == "disconnect")
                {
                    var connector = HardwareGroupOperations.Items(network.IoConnectors).Cast<IoConnector>().FirstOrDefault() ?? throw new InvalidOperationException("Interface has no IoConnector (not an IO device / DP slave interface).");
                    meta["before"] = HardwareIoConnectorRow(connector);
                    if (action == "connect")
                    {
                        if (connector.ConnectedToIoSystem != null) throw new InvalidOperationException("IoConnector is already connected to '" + connector.ConnectedToIoSystem.Name + "'; disconnect first.");
                        var target = HardwareGroupOperations.Items(HardwareExactSubnet(subnetName).IoSystems).Cast<IoSystem>().FirstOrDefault(s => string.Equals(s.Name, ioSystemName, StringComparison.Ordinal))
                            ?? throw new HardwareAddressingException("NotFound", "IO system not found on subnet: " + ioSystemName);
                        meta["target"] = HardwareIoSystemRow(target);
                        if (!dryRun) { meta["mayHaveChanged"] = true; connector.ConnectToIoSystem(target); }
                    }
                    else
                    {
                        if (connector.ConnectedToIoSystem == null) throw new InvalidOperationException("IoConnector is not connected to any IO system.");
                        if (!dryRun) { meta["mayHaveChanged"] = true; connector.DisconnectFromIoSystem(); }
                    }
                    if (dryRun) return "IO connector " + action + " preview; nothing changed.";
                    meta["after"] = HardwareIoConnectorRow(connector);
                    bool connected = connector.ConnectedToIoSystem != null;
                    if (connected != (action == "connect")) throw new InvalidOperationException("Native call returned but ConnectedToIoSystem readback does not reflect the request.");
                    return "IO connector " + action + " completed and read back. No save/compile/download.";
                }
                var controller = HardwareGroupOperations.Items(network.IoControllers).Cast<IoController>().FirstOrDefault() ?? throw new InvalidOperationException("Interface has no IoController (interface operating mode " + network.InterfaceOperatingMode + ").");
                var system = controller.IoSystem;
                if (action == "create")
                {
                    if (system != null) throw new InvalidOperationException("IoController already owns IO system '" + system.Name + "'.");
                    if (!HardwareGroupOperations.Items(network.Nodes).Cast<Node>().Any(n => n.ConnectedSubnet != null)) throw new InvalidOperationException("Interface must be connected to a subnet before an IO system can be created.");
                    meta["requestedName"] = name;
                    if (dryRun) return "IO system create preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    system = controller.CreateIoSystem(string.IsNullOrEmpty(name) ? string.Empty : name);
                    meta["after"] = HardwareIoSystemRow(system);
                    if (controller.IoSystem == null) throw new InvalidOperationException("CreateIoSystem returned but IoController.IoSystem is still null.");
                    return "IO system created and read back (empty name = TIA default). No save/compile/download.";
                }
                if (system == null) throw new InvalidOperationException("IoController has no IO system.");
                meta["before"] = HardwareIoSystemRow(system);
                if (action == "delete")
                {
                    if (dryRun) return "IO system delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; system.Delete();
                    if (controller.IoSystem != null) throw new InvalidOperationException("Delete returned but IoController.IoSystem is still set; do not blindly retry.");
                    meta["verifiedAbsent"] = true;
                    return "IO system deleted (IoController.IoSystem now null). No save/compile/download.";
                }
                ApplyScalarsAndAttributes(system, propertiesJson, attributesJson, meta, !dryRun);
                if (dryRun) return "IO system update preview; Number values outside the UI range fail only at compile time.";
                meta["after"] = HardwareIoSystemRow(system);
                return "IO system properties/attributes written and read back. No save/compile/download.";
            });

#if PLC_HARDWARE_DOMAINS
        public HardwareAddressingReply HardwareReadNetworkDomains(string subnetName, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("ListNetworkDomains", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit);
                var subnet = HardwareExactSubnet(subnetName); meta["subnet"] = HardwareScalarEvidence.Read(subnet);
                var syncOwner = subnet.GetService<SyncDomainOwner>(); var mrpOwner = subnet.GetService<MrpDomainOwner>();
                meta["syncDomainOwnerAvailable"] = syncOwner != null; meta["mrpDomainOwnerAvailable"] = mrpOwner != null;
                var rows = new List<object>();
                if (syncOwner != null) rows.AddRange(HardwareGroupOperations.Items(syncOwner.SyncDomains).Cast<SyncDomain>().Select(d => { var r = HardwareSyncDomainRow(d); r["kind"] = "sync"; return (object)r; }));
                if (mrpOwner != null) rows.AddRange(HardwareGroupOperations.Items(mrpOwner.MrpDomains).Cast<MrpDomain>().Select(d => { var r = HardwareMrpDomainRow(d); r["kind"] = "mrp"; return (object)r; }));
                // MRP instances live on the interface device items of the subnet's nodes (MrpInstancesOwner service).
                var instances = new List<object?>(); var seen = new HashSet<DeviceItem>();
#if PLC_HARDWARE_MRP_INSTANCES
                foreach (var node in HardwareGroupOperations.Items(subnet.Nodes).Cast<Node>())
                {
                    object? current = node; DeviceItem? item = null;
                    for (int hop = 0; hop < 8 && current != null && item == null; hop++) { item = current as DeviceItem; current = (current as IEngineeringInstance)?.Parent; }
                    if (item == null || !seen.Add(item)) continue;
                    var owner = HardwareServiceProvider(item).GetService<MrpInstancesOwner>(); if (owner == null) continue;
                    foreach (var instance in HardwareGroupOperations.Items(owner.MrpInstances).Cast<MrpInstance>()) instances.Add(HardwareMrpInstanceRow(instance));
                }
                meta["mrpInstances"] = instances;
#endif
                HardwarePage(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "SyncDomain/MrpDomain scalars, participant owner paths and official dynamic attributes; MrpInstances of the subnet's interface items.";
                return "Domain settings of the subnet read; no modification.";
            });
#else
        public HardwareAddressingReply HardwareReadNetworkDomains(string subnetName, int offset = 0, int limit = 100) => throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

#if PLC_HARDWARE_DOMAINS
        public HardwareAddressingReply HardwareManageNetworkDomain(string subnetName, string kind, string action, string name, Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!,
            string[] participantDevicePathJson = null!, string[] participantItemPathJson = null!, bool confirmDelete = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageNetworkDomain", meta => {
                HardwareNetworkPolicy.ValidateDomainRequest(kind, action, name);
                if (action == "delete") HardwareServicesPolicy.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var subnet = HardwareExactSubnet(subnetName);
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["name"] = name;
                // Every navigation starts from the subnet service again: compositions cached across a Create/Delete are stale.
                Func<object> composition = kind == "sync"
                    ? () => (subnet.GetService<SyncDomainOwner>() ?? throw new NotSupportedException("Subnet exposes no SyncDomainOwner (not a PROFINET subnet?).")).SyncDomains
                    : () => (object)(subnet.GetService<MrpDomainOwner>() ?? throw new NotSupportedException("Subnet exposes no MrpDomainOwner (not a PROFINET subnet?).")).MrpDomains;
                var target = HardwareGroupOperations.Find(composition(), name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Domain exists: " + name : "Domain not found: " + name);
                int before = HardwareGroupOperations.Items(composition()).Count(); meta["countBefore"] = before;
                Dictionary<string, object?> Row(object d) => d is SyncDomain s ? HardwareSyncDomainRow(s) : HardwareMrpDomainRow((MrpDomain)d);
                if (target != null) meta["before"] = Row(target);
                if (action == "create")
                {
                    if (dryRun) return "Domain create preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    target = HardwareGroupOperations.Call(composition(), "Create", new[] { typeof(string) }, name);
                    // The returned proxy is the authority (TIA may normalize the name); the refreshed composition is the cross-check.
                    var createdName = HardwareGroupOperations.Get(target, "Name").ToString()!; meta["createdName"] = createdName;
                    meta["after"] = Row(target);
                    var found = HardwareFindOnFresh(composition, createdName, meta, "postCreate"); meta["foundByNameAfterCreate"] = found != null;
                    var countAfter = HardwareCountOnFresh(composition, meta, "countAfter");
                    if (found == null && countAfter != before + 1) throw new InvalidOperationException("Create returned a proxy named '" + createdName + "' but the refreshed composition neither lists it nor grew by one.");
                    return "Domain created; readback from the returned proxy" + (found == null ? " (not yet visible by name on the refreshed composition, count grew by one)" : " and by name on the refreshed composition") + ". No save/compile/download.";
                }
                if (action == "delete")
                {
                    if (dryRun) return "Domain delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; HardwareGroupOperations.Call(target!, "Delete", Type.EmptyTypes);
                    var still = HardwareFindOnFresh(composition, name, meta, "postDelete");
                    if (still != null) throw new InvalidOperationException("Delete returned but the domain is still present on the refreshed composition; do not blindly retry.");
                    meta["verifiedAbsent"] = true; meta["absenceEvidence"] = meta["postDeleteEnumeration"] != null ? "refreshed composition raised a disposed-proxy error for the deleted domain" : "not found by name on the refreshed composition";
                    HardwareCountOnFresh(composition, meta, "countAfter");
                    return "Domain deleted and verified absent. No save/compile/download.";
                }
                if (action == "addParticipant")
                {
                    var participant = HardwareRequireNetworkInterface(ExactHardware(participantDevicePathJson, participantItemPathJson), "participantItemPathJson");
                    meta["participantOwnerPath"] = HardwareOwnerPath(participant);
                    var participants = HardwareGroupOperations.Get(target!, "DomainParticipants");
                    int count = HardwareGroupOperations.Items(participants).Count(); meta["participantCountBefore"] = count;
                    if (dryRun) return "Participant add preview; nothing changed (associations only support Add, removal is not exposed by the API).";
                    meta["mayHaveChanged"] = true;
                    if (target is SyncDomain sync) sync.DomainParticipants.Add(participant); else ((MrpDomain)target!).DomainParticipants.Add(participant);
                    var fresh = HardwareFindOnFresh(composition, name, meta, "postAdd") ?? target!;
                    int after = HardwareGroupOperations.Items(HardwareGroupOperations.Get(fresh, "DomainParticipants")).Count(); meta["participantCountAfter"] = after;
                    if (after != count + 1) throw new InvalidOperationException("Add returned but the participant count did not grow by one.");
                    meta["after"] = Row(fresh);
                    return "Participant added and count verified. No save/compile/download.";
                }
                ApplyScalarsAndAttributes(target!, propertiesJson, attributesJson, meta, !dryRun);
                if (dryRun) return "Domain update preview; nothing changed.";
                meta["after"] = Row(target!);
                return "Domain properties/attributes written and read back. No save/compile/download.";
            });
#else
        public HardwareAddressingReply HardwareManageNetworkDomain(string subnetName, string kind, string action, string name, Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!,
            string[] participantDevicePathJson = null!, string[] participantItemPathJson = null!, bool confirmDelete = false, bool dryRun = true) => throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        public HardwareAddressingReply HardwareReadTransferAreas(string[] devicePathJson, string[] itemPathJson, int positionNumber = -1, int extendedPositionNumber = -1, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("ListTransferAreas", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit); HardwareNetworkPolicy.RequirePosition(positionNumber, extendedPositionNumber);
                var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                var rows = new List<object>();
                if (positionNumber >= 0)
                {
                    #if PLC_HARDWARE_DOMAINS
                    var found = extendedPositionNumber >= 0 ? network.TransferAreas.Find(positionNumber, extendedPositionNumber) : network.TransferAreas.Find(positionNumber);
#else
                    var found = network.TransferAreas.Find(positionNumber);
#endif
                    if (found == null) throw new HardwareAddressingException("NotFound", "No transfer area at the requested position.");
                    rows.Add(HardwareTransferAreaRow(found));
                }
                else
                {
                    rows.AddRange(HardwareGroupOperations.Items(network.TransferAreas).Cast<TransferArea>().Select(a => (object)HardwareTransferAreaRow(a)));
#if PLC_HARDWARE_TRANSFER_MULTICAST
                    rows.AddRange(HardwareGroupOperations.Items(network.MulticastableTransferAreas).Cast<MulticastableTransferArea>().Select(a => (object)HardwareMulticastRow(a)));
#endif
                }
                HardwarePage(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "TransferArea and MulticastableTransferArea scalars, address rows, mapping rules and official dynamic attributes (per-attribute failures listed).";
                return "Transfer areas of the interface read; no modification.";
            });
#else
        public HardwareAddressingReply HardwareReadTransferAreas(string[] devicePathJson, string[] itemPathJson, int positionNumber = -1, int extendedPositionNumber = -1, int offset = 0, int limit = 100) => throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        public HardwareAddressingReply HardwareManageTransferArea(string[] devicePathJson, string[] itemPathJson, string action, string kind = "standard", string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!, string senderName = "", int length = -1,
            Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!, int ruleIndex = -1, string[] targetDevicePathJson = null!, string[] targetItemPathJson = null!, bool confirmDelete = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageTransferArea", meta => {
                HardwareNetworkPolicy.ValidateTransferAreaRequest(kind, action, name, type, positionNumber, extendedPositionNumber, length, ruleIndex);
                if (action == "delete" || action == "deleteMappingRule") HardwareServicesPolicy.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                if (kind == "multicast")
                {
#if PLC_HARDWARE_MRP_INSTANCES
                    Func<object> fresh = () => network.MulticastableTransferAreas;
                    var composition = network.MulticastableTransferAreas; var all = HardwareGroupOperations.Items(composition).Cast<MulticastableTransferArea>().ToArray();
                    meta["countBefore"] = all.Length;
                    var areaType = HardwareParseTransferAreaType(string.IsNullOrEmpty(type) ? "None" : type);
                    if (action == "create" || action == "createReceiver")
                    {
                        MulticastableTransferArea created;
                        if (action == "create")
                        {
                            var partner = HardwareRequireNetworkInterface(ExactHardware(partnerDevicePathJson, partnerItemPathJson), "partnerItemPathJson");
                            meta["partnerOwnerPath"] = HardwareOwnerPath(partner); meta["requestedName"] = name; meta["requestedLength"] = length;
                            if (dryRun) return "Multicast (CCDX) transfer area create preview; nothing changed.";
                            meta["mayHaveChanged"] = true;
                            created = length >= 0 ? composition.Create(partner, areaType, HardwareNetworkPolicy.RequireExactName(name, "name"), length)
                                : !string.IsNullOrEmpty(name) ? composition.Create(partner, areaType, name) : composition.Create(partner, areaType);
                        }
                        else
                        {
                            var sender = all.FirstOrDefault(a => string.Equals(a.Name, HardwareNetworkPolicy.RequireExactName(senderName, "senderName"), StringComparison.Ordinal))
                                ?? throw new HardwareAddressingException("NotFound", "Sender transfer area not found on this interface: " + senderName);
                            meta["sender"] = HardwareMulticastRow(sender);
                            if (dryRun) return "Receiver transfer area create preview; nothing changed.";
                            meta["mayHaveChanged"] = true; created = composition.Create(sender, areaType);
                        }
                        meta["after"] = HardwareMulticastRow(created); meta["createdName"] = created.Name; HardwareCountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area created and read back from the returned proxy. No save/compile/download.";
                    }
                    var target = all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal)) ?? throw new HardwareAddressingException("NotFound", "Multicast transfer area not found: " + name);
                    meta["before"] = HardwareMulticastRow(target);
                    if (action == "delete")
                    {
                        meta["partnerCount"] = HardwareGroupOperations.Items(target.PartnerTransferAreas).Count();
                        meta["deleteSemantics"] = "Deleting a sender deletes every receiver; deleting the last receiver also deletes the sender.";
                        if (dryRun) return "Multicast transfer area delete preview; nothing changed.";
                        meta["mayHaveChanged"] = true; target.Delete();
                        if (HardwareFindOnFresh(fresh, name, meta, "postDelete") != null) throw new InvalidOperationException("Delete returned but the transfer area is still present on the refreshed composition; do not blindly retry.");
                        meta["verifiedAbsent"] = true; HardwareCountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area deleted and verified absent. No save/compile/download.";
                    }
                    ApplyScalarsAndAttributes(target, propertiesJson, attributesJson, meta, !dryRun);
                    if (dryRun) return "Multicast transfer area update preview; nothing changed.";
                    meta["after"] = HardwareMulticastRow(target);
                    return "Multicast transfer area written and read back. No save/compile/download.";
#else
                    throw new NotSupportedException("Multicast transfer area mutations require V20.");
#endif
                }
                Func<object> freshAreas = () => network.TransferAreas;
                var areas = network.TransferAreas; var existing = HardwareGroupOperations.Items(areas).Cast<TransferArea>().ToArray();
                meta["countBefore"] = existing.Length;
                if (action == "create")
                {
                    if (existing.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal))) throw new InvalidOperationException("Transfer area name already used on this interface: " + name);
                    meta["requestedName"] = name; meta["requestedType"] = type; meta["requestedPositionNumber"] = positionNumber;
                    if (dryRun) return "Transfer area create preview; nothing changed (type cannot be changed afterwards).";
                    meta["mayHaveChanged"] = true;
                    var areaType = HardwareParseTransferAreaType(type);
                    #if PLC_HARDWARE_DOMAINS
                    var created = positionNumber >= 0 ? areas.Create(name, areaType, positionNumber) : areas.Create(name, areaType);
#else
                    var created = areas.Create(name, areaType);
#endif
                    meta["after"] = HardwareTransferAreaRow(created); meta["createdName"] = created.Name; HardwareCountOnFresh(freshAreas, meta, "countAfter");
                    return "Transfer area created and read back from the returned proxy. No save/compile/download.";
                }
                TransferArea? area = positionNumber >= 0
                    #if PLC_HARDWARE_DOMAINS
                    ? (extendedPositionNumber >= 0 ? areas.Find(positionNumber, extendedPositionNumber) : areas.Find(positionNumber))
#else
                    ? areas.Find(positionNumber)
#endif
                    : existing.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
                if (area == null) throw new HardwareAddressingException("NotFound", "Transfer area not found.");
                meta["before"] = HardwareTransferAreaRow(area);
                if (action == "delete")
                {
                    if (dryRun) return "Transfer area delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; area.Delete();
                    var countAfter = HardwareCountOnFresh(freshAreas, meta, "countAfter");
                    if (countAfter != null && countAfter != existing.Length - 1) throw new InvalidOperationException("Delete returned but the refreshed transfer area count did not drop by one; do not blindly retry.");
                    meta["verifiedAbsent"] = true;
                    return "Transfer area deleted and count verified. No save/compile/download.";
                }
                if (action == "update")
                {
                    ApplyScalarsAndAttributes(area, propertiesJson, attributesJson, meta, !dryRun);
                    if (dryRun) return "Transfer area update preview; nothing changed.";
                    meta["after"] = HardwareTransferAreaRow(area);
                    return "Transfer area written and read back. No save/compile/download.";
                }
                // Mapping rules (IO routing): Create() then Begin/End/IoType/Offset/Target; Target is a module of the IO device.
                var rules = area.TransferAreaMappingRules; var ruleList = HardwareGroupOperations.Items(rules).Cast<TransferAreaMappingRule>().ToArray();
                meta["ruleCountBefore"] = ruleList.Length;
                DeviceItem? ruleTarget = targetDevicePathJson != null && targetDevicePathJson.Length > 0 ? HardwareRequireDeviceItem(ExactHardware(targetDevicePathJson, targetItemPathJson), "targetItemPathJson") : null;
                if (ruleTarget != null) meta["ruleTargetPath"] = HardwareOwnerPath(ruleTarget);
                var ruleProperties = HardwareNetworkPolicy.ParseObject(propertiesJson, "propertiesJson"); meta["requestedProperties"] = HardwareScalarEvidence.Requested(ruleProperties);
                var prepared = HardwareAddressingPolicy.Prepare(typeof(TransferAreaMappingRule), ruleProperties);
                if (action == "createMappingRule")
                {
                    if (dryRun) return "Mapping rule create preview; nothing changed.";
                    meta["mayHaveChanged"] = true; var rule = rules.Create();
                    if (prepared.Count > 0) HardwareScalarEvidence.Apply(rule, prepared, meta);
                    if (ruleTarget != null) { rule.Target = ruleTarget; if (!ReferenceEquals(rule.Target, ruleTarget) && rule.Target?.Name != ruleTarget.Name) throw new InvalidOperationException("Target readback differs."); }
                    meta["after"] = HardwareMappingRuleRow(rule, ruleList.Length); meta["ruleCountAfter"] = HardwareGroupOperations.Items(rules).Count();
                    return "Mapping rule created and read back. No save/compile/download.";
                }
                if (ruleIndex >= ruleList.Length) throw new HardwareAddressingException("NotFound", "ruleIndex out of range (" + ruleList.Length + " rules).");
                var existingRule = ruleList[ruleIndex]; meta["ruleBefore"] = HardwareMappingRuleRow(existingRule, ruleIndex);
                if (action == "deleteMappingRule")
                {
                    if (dryRun) return "Mapping rule delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; existingRule.Delete();
                    var ruleCountAfter = HardwareCountOnFresh(() => area.TransferAreaMappingRules, meta, "ruleCountAfter");
                    if (ruleCountAfter != null && ruleCountAfter != ruleList.Length - 1) throw new InvalidOperationException("Delete returned but the refreshed rule count did not drop by one.");
                    meta["verifiedAbsent"] = true;
                    return "Mapping rule deleted and count verified. No save/compile/download.";
                }
                if (dryRun) return "Mapping rule update preview; nothing changed.";
                if (prepared.Count > 0) HardwareScalarEvidence.Apply(existingRule, prepared, meta);
                if (ruleTarget != null) { meta["mayHaveChanged"] = true; existingRule.Target = ruleTarget; }
                meta["ruleAfter"] = HardwareMappingRuleRow(existingRule, ruleIndex);
                return "Mapping rule written and read back. No save/compile/download.";
            });
#else
        public HardwareAddressingReply HardwareManageTransferArea(string[] devicePathJson, string[] itemPathJson, string action, string kind = "standard", string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!, string senderName = "", int length = -1,
            Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!, int ruleIndex = -1, string[] targetDevicePathJson = null!, string[] targetItemPathJson = null!, bool confirmDelete = false, bool dryRun = true) => throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

        public HardwareAddressingReply HardwareReadDeviceItemChannels(string[] devicePathJson, string[] itemPathJson, string channelType = "", string channelIoType = "", int channelNumber = -1, string[] attributeNamesJson = null!, int offset = 0, int limit = 100, bool includeLinkedTags = false)
            => RunHardwareAddressStep("ListDeviceItemChannels", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit);
                bool exact = HardwareNetworkPolicy.ChannelIdentityGiven(channelType, channelIoType, channelNumber);
                var extra = HardwareNetworkPolicy.ParseNames(attributeNamesJson, "attributeNamesJson");
                var item = HardwareRequireDeviceItem(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["ownerPath"] = HardwareOwnerPath(item);
                var channels = exact ? new[] { HardwareExactChannel(item, channelType, channelIoType, channelNumber) } : HardwareGroupOperations.Items(item.Channels).Cast<Channel>().ToArray();
                HardwarePage(channels.Select(c => { var row = HardwareChannelRow(c, extra); if (includeLinkedTags) row["linkedTags"] = HardwareLinkedTagRows(c, row); return (object)row; }).ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Channel Number/Type/IoType, attribute names from GetAttributeInfos, values of ChannelAddress/ChannelWidth plus requested names (failures listed)" + (includeLinkedTags ? "; linkedTags = PLC tags linked to the channel via PlcTagProvider.GetLinkedTags (V21)." : ".");
                return "Channels of the device item read; no modification.";
            });

        public HardwareAddressingReply HardwareUpdateDeviceItemChannel(string[] devicePathJson, string[] itemPathJson, string channelType, string channelIoType, int channelNumber, Dictionary<string, HardwareScalar> attributesJson, bool dryRun = true)
            => RunHardwareAddressStep("SetDeviceItemChannel", meta => {
                if (!HardwareNetworkPolicy.ChannelIdentityGiven(channelType, channelIoType, channelNumber)) throw new ArgumentException("channelType, channelIoType and channelNumber are required.");
                var attributes = HardwareNetworkPolicy.ParseObject(attributesJson, "attributesJson");
                if (attributes.Count == 0) throw new ArgumentException("attributesJson must name at least one attribute.");
                using var access = dryRun ? null : HardwareEditAccess();
                var item = HardwareRequireDeviceItem(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                var channel = HardwareExactChannel(item, channelType, channelIoType, channelNumber);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(item);
                var names = attributes.Select(p => p.Key).ToArray();
                meta["before"] = HardwareChannelRow(channel, names); meta["requestedAttributes"] = HardwareScalarEvidence.Requested(attributes);
                var modes = HardwareChannelAttributeModes(channel);
                var readOnly = names.Where(n => modes.TryGetValue(n, out var mode) && mode != "Write" && mode != "ReadWrite").ToArray();
                var unknown = names.Where(n => !modes.ContainsKey(n)).ToArray();
                meta["readOnlyAttributes"] = new List<object?>(readOnly.Select(n => (object)n).ToArray()); meta["unlistedAttributes"] = new List<object?>(unknown.Select(n => (object)n).ToArray());
                if (readOnly.Length > 0) throw new NotSupportedException("Attribute(s) are not writable on this channel per GetAttributeInfos: " + string.Join(", ", readOnly) + ". Nothing was written.");
                if (dryRun) return "Channel attribute write preview; nothing changed" + (unknown.Length > 0 ? " (attributes not listed by GetAttributeInfos are attempted as-is: " + string.Join(", ", unknown) + ")" : "") + ".";
                SetDynamicAttributes(channel, attributes, meta);
                meta["after"] = HardwareChannelRow(channel, names);
                return "Channel attributes written and read back. No save/compile/download.";
            });

        public HardwareAddressingReply HardwareManageDeviceUserGroup(string groupPath = "", string action = "read", string newName = "", bool dryRun = true)
            => RunHardwareAddressStep("ManageDeviceUserGroup", meta => {
                HardwareNetworkPolicy.RequireOneOf(action, HardwareNetworkPolicy.DeviceGroupActions, "action");
                using var access = action != "read" && !dryRun ? HardwareEditAccess() : null;
                meta["groupPath"] = groupPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    Dictionary<string, object?> GroupRow(DeviceGroup g) => new Dictionary<string, object?>
                    {
                        ["name"] = g.Name, ["type"] = g.GetType().Name,
                        ["devices"] = new List<object?>(HardwareGroupOperations.Items(g.Devices).Cast<Device>().Select(d => (object)d.Name).ToArray()),
                        ["groups"] = g is DeviceUserGroup u ? new List<object?>(HardwareGroupOperations.Items(u.Groups).Cast<DeviceUserGroup>().Select(x => (object)x.Name).ToArray()) : new List<object?>()
                    };
                    if (string.IsNullOrEmpty(groupPath))
                    {
                        meta["rootDevices"] = new List<object?>(HardwareGroupOperations.Items(project!.Devices).Cast<Device>().Select(d => (object)d.Name).ToArray());
                        meta["records"] = new List<object?>(HardwareGroupOperations.Items(project.DeviceGroups).Cast<DeviceUserGroup>().Select(g => (object)GroupRow(g)).ToArray());
                        meta["ungroupedDevicesGroup"] = GroupRow(project.UngroupedDevicesGroup);
                    }
                    else meta["records"] = new List<object?> { GroupRow((DeviceUserGroup)HardwareGroupOperations.Group(project!, groupPath, "DeviceGroups")) };
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    return "Device groups read; no modification.";
                }
                meta["result"] = HardwareGroupOperations.Manage(project!, groupPath, action, newName, dryRun, "Devices", "DeviceGroups");
                if (!dryRun) meta["mayHaveChanged"] = true;
                return dryRun ? "Device user group preview; nothing changed." : "Device user group operation completed (CreateFrom(MasterCopy) is not exposed here). No save/compile/download.";
            });

        public HardwareAddressingReply HardwareManagePortInterconnection(string[] devicePathJson, string[] itemPathJson, string action = "read", string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!, bool dryRun = true)
            => RunHardwareAddressStep("ManagePortInterconnection", meta => {
                HardwareNetworkPolicy.RequireOneOf(action, HardwareNetworkPolicy.PortActions, "action");
                bool write = action != "read" && !dryRun;
                using var access = write ? HardwareEditAccess() : null;
                var port = HardwareRequireHardwareService<NetworkPort>(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["port"] = HardwarePortRow(port);
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Port interconnections read; no modification."; }
                var partner = HardwareRequireHardwareService<NetworkPort>(ExactHardware(partnerDevicePathJson, partnerItemPathJson), "partnerItemPathJson");
                meta["partner"] = HardwarePortRow(partner);
                bool linked = HardwareGroupOperations.Items(port.ConnectedPorts).Any(p => ReferenceEquals(p, partner) || (p is NetworkPort np && np.OwnedBy?.Name == partner.OwnedBy?.Name && HardwareOwnerPath(np).SequenceEqual(HardwareOwnerPath(partner), StringComparer.Ordinal)));
                meta["linkedBefore"] = linked;
                if (action == "connect" && linked) throw new InvalidOperationException("Ports are already interconnected.");
                if (action == "disconnect" && !linked) throw new InvalidOperationException("Ports are not interconnected.");
                if (!write) return "Port " + action + " preview; nothing changed (TIA refuses ports of the same interface and second partners on ports without alternative partners).";
                meta["mayHaveChanged"] = true;
                if (action == "connect") port.ConnectToPort(partner); else port.DisconnectFromPort(partner);
                var after = HardwarePortRow(port); meta["after"] = after;
                int count = ((List<object?>)after["connectedPorts"]!).Count; int before = ((List<object?>)((Dictionary<string, object?>)meta["port"]!)["connectedPorts"]!).Count;
                if (count != before + (action == "connect" ? 1 : -1)) throw new InvalidOperationException("Native call returned but ConnectedPorts count did not change as expected.");
                return "Port " + action + " completed and ConnectedPorts count verified. No save/compile/download.";
            });
    }
}
