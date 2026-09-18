using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Hardware-network family: IO systems, sync/MRP domains, transfer areas, channels, addressing, device user groups,
    // device users and port interconnections. Every member is the official V20/V21 API (Siemens.Engineering.HW);
    // dynamic attributes are read per name with the failure captured, never assumed present.
    public partial class Portal
    {
        private Subnet ExactSubnet(string subnetName)
            => EngineeringGroupOperations.Find(_project!.Subnets, HardwareNetworkLogic.RequireExactName(subnetName, "subnetName")) as Subnet
               ?? throw new PortalException(PortalErrorCode.NotFound, "Subnet not found: " + subnetName);
        private static NetworkInterface RequireNetworkInterface(HardwareObject item, string parameter)
            => ServiceProvider(item).GetService<NetworkInterface>() ?? throw new InvalidOperationException(parameter + " must identify a DeviceItem that exposes NetworkInterface.");
        private static DeviceItem RequireDeviceItem(HardwareObject owner, string parameter)
            => owner as DeviceItem ?? throw new ArgumentException(parameter + " must address a DeviceItem (non-empty itemPathJson).");
        private static T RequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
            => ServiceProvider(owner).GetService<T>() ?? throw new NotSupportedException(parameter + " does not expose " + typeof(T).Name + " (service is null for this hardware object).");

        // Hardware owner path of any HW object or feature: [device, item, sub item ...], walking OwnedBy/Parent (bounded).
        private static JsonArray HardwareOwnerPath(object? start)
        {
            var names = new List<string>(); object? current = start;
            for (int hop = 0; hop < 16 && current != null; hop++)
            {
                if (current is Device device) { names.Insert(0, device.Name); break; }
                if (current is DeviceItem item) { names.Insert(0, item.Name); current = item.Parent; continue; }
                var ownedBy = current.GetType().GetProperty("OwnedBy")?.GetValue(current);
                current = ownedBy ?? (current as IEngineeringInstance)?.Parent;
            }
            return new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        }
        private static JsonObject DynamicAttributes(IEngineeringObject target, string[] names)
        {
            var values = new JsonObject(); var failures = new JsonArray();
            foreach (var name in names)
            {
                try { values[name] = EngineeringScalarProperties.Json(target.GetAttribute(name)); }
                catch (Exception ex) { failures.Add(new JsonObject { ["attribute"] = name, ["error"] = ex.GetBaseException().Message }); }
            }
            return new JsonObject { ["values"] = values, ["failures"] = failures };
        }
        // Dynamic attribute writes: the CLR type is taken from the current value (enum names, numbers, booleans), then read back.
        private static void SetDynamicAttributes(IEngineeringObject target, JsonObject attributes, JsonObject meta)
        {
            var applied = new JsonArray(); meta["appliedAttributes"] = applied;
            foreach (var pair in attributes)
            {
                object? current = null; try { current = target.GetAttribute(pair.Key); } catch { }
                var value = EngineeringScalarProperties.ConvertValue(pair.Value, current?.GetType() ?? typeof(object));
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = pair.Key;
                target.SetAttribute(pair.Key, value);
                applied.Add(pair.Key);
                if (!EngineeringScalarProperties.SameValue(target.GetAttribute(pair.Key), value)) throw new InvalidOperationException("Attribute readback differs: " + pair.Key + ". Changes are not rolled back.");
            }
        }
        private static void ApplyScalarsAndAttributes(object target, string propertiesJson, string attributesJson, JsonObject meta, bool write)
        {
            var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); var attributes = HardwareNetworkLogic.ParseObject(attributesJson, "attributesJson");
            var prepared = EngineeringScalarProperties.Prepare(target.GetType(), properties);
            meta["requestedProperties"] = properties.DeepClone(); meta["requestedAttributes"] = attributes.DeepClone();
            if (!write) return;
            if (prepared.Count > 0) EngineeringScalarProperties.Apply(target, prepared, meta);
            if (attributes.Count > 0) SetDynamicAttributes((IEngineeringObject)target, attributes, meta);
        }
        // 2.7.30 real project: a composition proxy fetched before Create/Delete is stale (the new MrpDomain was not found
        // on it; enumerating it after Delete raised EngineeringObjectDisposedException). Verification therefore always
        // re-navigates to a fresh composition, and a disposed-proxy error while looking for a deleted object counts as
        // evidence of its absence rather than as a session failure.
        private static object? FindOnFresh(Func<object> composition, string name, JsonObject meta, string phase)
        {
            try { return EngineeringGroupOperations.Find(composition(), name); }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex))
            {
                meta[phase + "Enumeration"] = "EngineeringObjectDisposedException on the refreshed composition (TIA released a proxy): " + ex.GetBaseException().Message;
                return null;
            }
        }
        private static int? CountOnFresh(Func<object> composition, JsonObject meta, string key)
        {
            try { int n = EngineeringGroupOperations.Items(composition()).Count(); meta[key] = n; return n; }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex)) { meta[key] = null; meta[key + "Error"] = ex.GetBaseException().Message; return null; }
        }
        private static JsonObject Page(JsonNode[] all, int offset, int limit, JsonObject meta)
        {
            var rows = all.Skip(offset).Take(limit).ToArray();
            meta["records"] = new JsonArray(rows);
            foreach (var pair in HardwareServicesLogic.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value?.DeepClone();
            return meta;
        }

        // ---- rows -------------------------------------------------------------------------------------------------
        private static JsonObject AddressRow(Address address) => new JsonObject
        {
            ["startAddress"] = address.StartAddress, ["length"] = address.Length, ["ioType"] = address.IoType.ToString(),
            ["controllers"] = new JsonArray(EngineeringGroupOperations.Items(address.AddressControllers).Select(c => (JsonNode)HardwareOwnerPath(c)).ToArray()),
            ["attributes"] = DynamicAttributes(address, HardwareNetworkLogic.AddressAttributes)
        };
        private static JsonObject HwIdentifierRow(HwIdentifier id) => new JsonObject
        {
            ["identifier"] = id.Identifier,
            ["controllers"] = new JsonArray(EngineeringGroupOperations.Items(id.HwIdentifierControllers).Select(c => (JsonNode)HardwareOwnerPath(c)).ToArray())
        };
        private static JsonObject IoSystemRow(IoSystem system) => new JsonObject
        {
            ["name"] = system.Name, ["number"] = system.Number, ["subnetName"] = system.Subnet?.Name,
            ["connectedIoDevices"] = new JsonArray(EngineeringGroupOperations.Items(system.ConnectedIoDevices).Select(c => (JsonNode)HardwareOwnerPath(c)).ToArray()),
            ["hwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(system.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)h.Identifier).ToArray()),
            ["attributes"] = DynamicAttributes(system, HardwareNetworkLogic.IoSystemAttributes)
        };
        private static JsonObject IoControllerRow(IoController controller) => new JsonObject
        {
            ["ownerPath"] = HardwareOwnerPath(controller), ["ioSystemName"] = controller.IoSystem?.Name,
            ["addresses"] = new JsonArray(EngineeringGroupOperations.Items(controller.Addresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)).ToArray()),
            ["attributes"] = DynamicAttributes(controller, HardwareNetworkLogic.IoControllerAttributes)
        };
        private static JsonObject IoConnectorRow(IoConnector connector)
        {
            IoController? remote = null; try { remote = connector.GetIoController(); } catch { }
            return new JsonObject
            {
                ["ownerPath"] = HardwareOwnerPath(connector), ["connectedToIoSystem"] = connector.ConnectedToIoSystem?.Name,
                ["ioControllerOwnerPath"] = remote == null ? null : HardwareOwnerPath(remote),
                ["attributes"] = DynamicAttributes(connector, HardwareNetworkLogic.IoConnectorAttributes)
            };
        }
        private static JsonObject SyncDomainRow(SyncDomain domain) => new JsonObject
        {
            ["name"] = domain.Name, ["convertedName"] = domain.ConvertedName, ["isDefault"] = domain.IsDefault,
            ["participants"] = new JsonArray(EngineeringGroupOperations.Items(domain.DomainParticipants).Select(p => (JsonNode)HardwareOwnerPath(p)).ToArray()),
            ["attributes"] = DynamicAttributes(domain, HardwareNetworkLogic.SyncDomainAttributes)
        };
        private static JsonObject MrpDomainRow(MrpDomain domain) => new JsonObject
        {
            ["name"] = domain.Name,
            ["participants"] = new JsonArray(EngineeringGroupOperations.Items(domain.DomainParticipants).Select(p => (JsonNode)HardwareOwnerPath(p)).ToArray()),
            ["attributes"] = DynamicAttributes(domain, HardwareNetworkLogic.MrpDomainAttributes)
        };
        private static JsonObject MrpInstanceRow(MrpInstance instance) => new JsonObject
        {
            ["name"] = instance.Name, ["connectedMrpDomain"] = instance.ConnectedMrpDomain?.Name, ["interfaceOwnerPath"] = HardwareOwnerPath(instance.Interface),
            ["ringPort1"] = instance.RingPort1 == null ? null : HardwareOwnerPath(instance.RingPort1), ["ringPort2"] = instance.RingPort2 == null ? null : HardwareOwnerPath(instance.RingPort2)
        };
        private static JsonObject MappingRuleRow(TransferAreaMappingRule rule, int index) => new JsonObject
        {
            ["index"] = index, ["positionNumber"] = rule.PositionNumber, ["begin"] = rule.Begin, ["end"] = rule.End, ["offset"] = rule.Offset,
            ["ioType"] = rule.IoType.ToString(), ["targetPath"] = rule.Target == null ? null : HardwareOwnerPath(rule.Target)
        };
        private static JsonObject TransferAreaRow(TransferArea area) => new JsonObject
        {
            ["kind"] = "standard", ["name"] = area.Name, ["type"] = area.Type.ToString(), ["direction"] = area.Direction.ToString(),
            ["positionNumber"] = area.PositionNumber, ["extendedPositionNumber"] = area.ExtendedPositionNumber,
            ["localToPartnerLength"] = area.LocalToPartnerLength, ["partnerToLocalLength"] = area.PartnerToLocalLength,
            ["localAddresses"] = new JsonArray(EngineeringGroupOperations.Items(area.LocalAddresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)).ToArray()),
            ["partnerAddresses"] = new JsonArray(EngineeringGroupOperations.Items(area.PartnerAddresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)).ToArray()),
            ["mappingRules"] = new JsonArray(EngineeringGroupOperations.Items(area.TransferAreaMappingRules).Cast<TransferAreaMappingRule>().Select((r, i) => (JsonNode)MappingRuleRow(r, i)).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkLogic.TransferAreaAttributes)
        };
        private static JsonObject MulticastRow(MulticastableTransferArea area) => new JsonObject
        {
            ["kind"] = "multicast", ["name"] = area.Name, ["type"] = area.Type.ToString(), ["direction"] = area.Direction.ToString(), ["comment"] = area.Comment, ["dataLength"] = area.DataLength,
            ["addresses"] = new JsonArray(EngineeringGroupOperations.Items(area.Addresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)).ToArray()),
            ["partnerTransferAreas"] = new JsonArray(EngineeringGroupOperations.Items(area.PartnerTransferAreas).Cast<MulticastableTransferArea>()
                .Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["ownerPath"] = HardwareOwnerPath(p) }).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkLogic.MulticastTransferAreaAttributes)
        };
        // GetAttributeInfos carries the access mode; the 2.7.30 real project showed ChannelActivated / InputDelay read-only
        // on ET 200SP DI modules, so the mode is reported and checked before any SetAttribute.
        private static Dictionary<string, string> ChannelAttributeModes(Channel channel)
        {
            var modes = new Dictionary<string, string>(StringComparer.Ordinal);
            try { foreach (var info in channel.GetAttributeInfos()) modes[info.Name] = info.AccessMode.ToString(); } catch { }
            return modes;
        }
        private static JsonObject ChannelRow(Channel channel, string[] extraAttributes)
        {
            var modes = ChannelAttributeModes(channel);
            return new JsonObject
            {
                ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(),
                ["attributeNames"] = new JsonArray(modes.Select(m => (JsonNode)new JsonObject { ["name"] = m.Key, ["accessMode"] = m.Value }).ToArray()),
                ["attributes"] = DynamicAttributes(channel, HardwareNetworkLogic.ChannelAttributes.Concat(extraAttributes).Distinct(StringComparer.Ordinal).ToArray())
            };
        }
        private static JsonObject PortRow(NetworkPort port) => new JsonObject
        {
            ["ownerPath"] = HardwareOwnerPath(port), ["interfaceOwnerPath"] = port.Interface == null ? null : HardwareOwnerPath(port.Interface),
            ["connectedPorts"] = new JsonArray(EngineeringGroupOperations.Items(port.ConnectedPorts).Select(p => (JsonNode)HardwareOwnerPath(p)).ToArray())
        };
        // WebserverUserPermissions is not marked [Flags] in the PublicAPI, so combined values print as numbers; decode bits by hand.
        private static JsonArray PermissionNames(object? permissions)
        {
            if (permissions == null || !permissions.GetType().IsEnum) return new JsonArray();
            long value = Convert.ToInt64(permissions); var names = new List<string>();
            foreach (var member in Enum.GetValues(permissions.GetType()).Cast<object>()) { long bit = Convert.ToInt64(member); if (bit != 0 && (value & bit) == bit) names.Add(member.ToString()!); }
            if (names.Count == 0) names.Add(Enum.GetName(permissions.GetType(), Enum.ToObject(permissions.GetType(), 0)) ?? "None");
            return new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        }
        private static JsonObject UserRow(object user)
        {
            var row = EngineeringScalarProperties.Read(user);
            var permissions = user.GetType().GetProperty("Permissions")?.GetValue(user);
            if (permissions != null) row["permissionNames"] = PermissionNames(permissions);
            return row;
        }
        // F_CD exists only in the V21 TransferAreaType; refuse unknown members with the version in the message.
        private static TransferAreaType ParseTransferAreaType(string type)
        {
            if (!Enum.IsDefined(typeof(TransferAreaType), type)) throw new NotSupportedException("TransferAreaType." + type + " is not defined by the connected TIA Portal Openness version.");
            return (TransferAreaType)Enum.Parse(typeof(TransferAreaType), type);
        }

        // ---- IO systems ----------------------------------------------------------------------------------------------
        public ResponseMessage ReadIoSystems(string subnetName = "", string devicePathJson = "[]", string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadIoSystems", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                bool bySubnet = !string.IsNullOrEmpty(subnetName), byInterface = devicePathJson != "[]" && !string.IsNullOrWhiteSpace(devicePathJson);
                if (bySubnet == byInterface) throw new ArgumentException("Give exactly one scope: subnetName, or devicePathJson/itemPathJson of an interface device item.");
                var rows = new List<JsonNode>();
                if (bySubnet)
                {
                    var subnet = ExactSubnet(subnetName); meta["subnet"] = EngineeringScalarProperties.Read(subnet);
                    rows.AddRange(EngineeringGroupOperations.Items(subnet.IoSystems).Cast<IoSystem>().Select(s => (JsonNode)IoSystemRow(s)));
                }
                else
                {
                    var network = RequireNetworkInterface(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                    meta["interface"] = new JsonObject { ["ownerPath"] = HardwareOwnerPath(network), ["interfaceType"] = network.InterfaceType.ToString(), ["interfaceOperatingMode"] = network.InterfaceOperatingMode.ToString() };
                    var controllers = EngineeringGroupOperations.Items(network.IoControllers).Cast<IoController>().ToArray();
                    var connectors = EngineeringGroupOperations.Items(network.IoConnectors).Cast<IoConnector>().ToArray();
                    meta["ioControllers"] = new JsonArray(controllers.Select(c => (JsonNode)IoControllerRow(c)).ToArray());
                    meta["ioConnectors"] = new JsonArray(connectors.Select(c => (JsonNode)IoConnectorRow(c)).ToArray());
                    rows.AddRange(controllers.Where(c => c.IoSystem != null).Select(c => (JsonNode)IoSystemRow(c.IoSystem)));
                    rows.AddRange(connectors.Where(c => c.ConnectedToIoSystem != null).Select(c => (JsonNode)IoSystemRow(c.ConnectedToIoSystem)));
                }
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "IoSystem Name/Number/Subnet, ConnectedIoDevices owner paths, HwIdentifiers and the official dynamic attributes (per-attribute failures listed); IoController/IoConnector rows when scoped by interface.";
                return "IO systems read; no modification.";
            });

        public ResponseMessage ManageIoSystem(string devicePathJson, string itemPathJson, string action, string name = "", string subnetName = "", string ioSystemName = "",
            string propertiesJson = "{}", string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageIoSystem", meta => {
                HardwareNetworkLogic.ValidateIoSystemRequest(action, name, subnetName, ioSystemName);
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var network = RequireNetworkInterface(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                if (action == "connect" || action == "disconnect")
                {
                    var connector = EngineeringGroupOperations.Items(network.IoConnectors).Cast<IoConnector>().FirstOrDefault() ?? throw new InvalidOperationException("Interface has no IoConnector (not an IO device / DP slave interface).");
                    meta["before"] = IoConnectorRow(connector);
                    if (action == "connect")
                    {
                        if (connector.ConnectedToIoSystem != null) throw new InvalidOperationException("IoConnector is already connected to '" + connector.ConnectedToIoSystem.Name + "'; disconnect first.");
                        var target = EngineeringGroupOperations.Items(ExactSubnet(subnetName).IoSystems).Cast<IoSystem>().FirstOrDefault(s => string.Equals(s.Name, ioSystemName, StringComparison.Ordinal))
                            ?? throw new PortalException(PortalErrorCode.NotFound, "IO system not found on subnet: " + ioSystemName);
                        meta["target"] = IoSystemRow(target);
                        if (!dryRun) { meta["mayHaveChanged"] = true; connector.ConnectToIoSystem(target); }
                    }
                    else
                    {
                        if (connector.ConnectedToIoSystem == null) throw new InvalidOperationException("IoConnector is not connected to any IO system.");
                        if (!dryRun) { meta["mayHaveChanged"] = true; connector.DisconnectFromIoSystem(); }
                    }
                    if (dryRun) return "IO connector " + action + " preview; nothing changed.";
                    meta["after"] = IoConnectorRow(connector);
                    bool connected = connector.ConnectedToIoSystem != null;
                    if (connected != (action == "connect")) throw new InvalidOperationException("Native call returned but ConnectedToIoSystem readback does not reflect the request.");
                    return "IO connector " + action + " completed and read back. No save/compile/download.";
                }
                var controller = EngineeringGroupOperations.Items(network.IoControllers).Cast<IoController>().FirstOrDefault() ?? throw new InvalidOperationException("Interface has no IoController (interface operating mode " + network.InterfaceOperatingMode + ").");
                var system = controller.IoSystem;
                if (action == "create")
                {
                    if (system != null) throw new InvalidOperationException("IoController already owns IO system '" + system.Name + "'.");
                    if (!EngineeringGroupOperations.Items(network.Nodes).Cast<Node>().Any(n => n.ConnectedSubnet != null)) throw new InvalidOperationException("Interface must be connected to a subnet before an IO system can be created.");
                    meta["requestedName"] = name;
                    if (dryRun) return "IO system create preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    system = controller.CreateIoSystem(string.IsNullOrEmpty(name) ? string.Empty : name);
                    meta["after"] = IoSystemRow(system);
                    if (controller.IoSystem == null) throw new InvalidOperationException("CreateIoSystem returned but IoController.IoSystem is still null.");
                    return "IO system created and read back (empty name = TIA default). No save/compile/download.";
                }
                if (system == null) throw new InvalidOperationException("IoController has no IO system.");
                meta["before"] = IoSystemRow(system);
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
                meta["after"] = IoSystemRow(system);
                return "IO system properties/attributes written and read back. No save/compile/download.";
            });

        // ---- sync / MRP domains ----------------------------------------------------------------------------------------
        public ResponseMessage ReadNetworkDomains(string subnetName, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadNetworkDomains", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var subnet = ExactSubnet(subnetName); meta["subnet"] = EngineeringScalarProperties.Read(subnet);
                var syncOwner = subnet.GetService<SyncDomainOwner>(); var mrpOwner = subnet.GetService<MrpDomainOwner>();
                meta["syncDomainOwnerAvailable"] = syncOwner != null; meta["mrpDomainOwnerAvailable"] = mrpOwner != null;
                var rows = new List<JsonNode>();
                if (syncOwner != null) rows.AddRange(EngineeringGroupOperations.Items(syncOwner.SyncDomains).Cast<SyncDomain>().Select(d => { var r = SyncDomainRow(d); r["kind"] = "sync"; return (JsonNode)r; }));
                if (mrpOwner != null) rows.AddRange(EngineeringGroupOperations.Items(mrpOwner.MrpDomains).Cast<MrpDomain>().Select(d => { var r = MrpDomainRow(d); r["kind"] = "mrp"; return (JsonNode)r; }));
                // MRP instances live on the interface device items of the subnet's nodes (MrpInstancesOwner service).
                var instances = new JsonArray(); var seen = new HashSet<DeviceItem>();
                foreach (var node in EngineeringGroupOperations.Items(subnet.Nodes).Cast<Node>())
                {
                    object? current = node; DeviceItem? item = null;
                    for (int hop = 0; hop < 8 && current != null && item == null; hop++) { item = current as DeviceItem; current = (current as IEngineeringInstance)?.Parent; }
                    if (item == null || !seen.Add(item)) continue;
                    var owner = ServiceProvider(item).GetService<MrpInstancesOwner>(); if (owner == null) continue;
                    foreach (var instance in EngineeringGroupOperations.Items(owner.MrpInstances).Cast<MrpInstance>()) instances.Add(MrpInstanceRow(instance));
                }
                meta["mrpInstances"] = instances;
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "SyncDomain/MrpDomain scalars, participant owner paths and official dynamic attributes; MrpInstances of the subnet's interface items.";
                return "Domain settings of the subnet read; no modification.";
            });

        public ResponseMessage ManageNetworkDomain(string subnetName, string kind, string action, string name, string propertiesJson = "{}", string attributesJson = "{}",
            string participantDevicePathJson = "[]", string participantItemPathJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageNetworkDomain", meta => {
                HardwareNetworkLogic.ValidateDomainRequest(kind, action, name);
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var subnet = ExactSubnet(subnetName);
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["name"] = name;
                // Every navigation starts from the subnet service again: compositions cached across a Create/Delete are stale.
                Func<object> composition = kind == "sync"
                    ? () => (subnet.GetService<SyncDomainOwner>() ?? throw new NotSupportedException("Subnet exposes no SyncDomainOwner (not a PROFINET subnet?).")).SyncDomains
                    : () => (object)(subnet.GetService<MrpDomainOwner>() ?? throw new NotSupportedException("Subnet exposes no MrpDomainOwner (not a PROFINET subnet?).")).MrpDomains;
                var target = EngineeringGroupOperations.Find(composition(), name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Domain exists: " + name : "Domain not found: " + name);
                int before = EngineeringGroupOperations.Items(composition()).Count(); meta["countBefore"] = before;
                JsonObject Row(object d) => d is SyncDomain s ? SyncDomainRow(s) : MrpDomainRow((MrpDomain)d);
                if (target != null) meta["before"] = Row(target);
                if (action == "create")
                {
                    if (dryRun) return "Domain create preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    target = EngineeringGroupOperations.Call(composition(), "Create", new[] { typeof(string) }, name);
                    // The returned proxy is the authority (TIA may normalize the name); the refreshed composition is the cross-check.
                    var createdName = EngineeringGroupOperations.Get(target, "Name").ToString()!; meta["createdName"] = createdName;
                    meta["after"] = Row(target);
                    var found = FindOnFresh(composition, createdName, meta, "postCreate"); meta["foundByNameAfterCreate"] = found != null;
                    var countAfter = CountOnFresh(composition, meta, "countAfter");
                    if (found == null && countAfter != before + 1) throw new InvalidOperationException("Create returned a proxy named '" + createdName + "' but the refreshed composition neither lists it nor grew by one.");
                    return "Domain created; readback from the returned proxy" + (found == null ? " (not yet visible by name on the refreshed composition, count grew by one)" : " and by name on the refreshed composition") + ". No save/compile/download.";
                }
                if (action == "delete")
                {
                    if (dryRun) return "Domain delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; EngineeringGroupOperations.Call(target!, "Delete", Type.EmptyTypes);
                    var still = FindOnFresh(composition, name, meta, "postDelete");
                    if (still != null) throw new InvalidOperationException("Delete returned but the domain is still present on the refreshed composition; do not blindly retry.");
                    meta["verifiedAbsent"] = true; meta["absenceEvidence"] = meta["postDeleteEnumeration"] != null ? "refreshed composition raised a disposed-proxy error for the deleted domain" : "not found by name on the refreshed composition";
                    CountOnFresh(composition, meta, "countAfter");
                    return "Domain deleted and verified absent. No save/compile/download.";
                }
                if (action == "addParticipant")
                {
                    var participant = RequireNetworkInterface(ExactEngineeringHardware(participantDevicePathJson, participantItemPathJson), "participantItemPathJson");
                    meta["participantOwnerPath"] = HardwareOwnerPath(participant);
                    var participants = EngineeringGroupOperations.Get(target!, "DomainParticipants");
                    int count = EngineeringGroupOperations.Items(participants).Count(); meta["participantCountBefore"] = count;
                    if (dryRun) return "Participant add preview; nothing changed (associations only support Add, removal is not exposed by the API).";
                    meta["mayHaveChanged"] = true;
                    if (target is SyncDomain sync) sync.DomainParticipants.Add(participant); else ((MrpDomain)target!).DomainParticipants.Add(participant);
                    var fresh = FindOnFresh(composition, name, meta, "postAdd") ?? target!;
                    int after = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(fresh, "DomainParticipants")).Count(); meta["participantCountAfter"] = after;
                    if (after != count + 1) throw new InvalidOperationException("Add returned but the participant count did not grow by one.");
                    meta["after"] = Row(fresh);
                    return "Participant added and count verified. No save/compile/download.";
                }
                ApplyScalarsAndAttributes(target!, propertiesJson, attributesJson, meta, !dryRun);
                if (dryRun) return "Domain update preview; nothing changed.";
                meta["after"] = Row(target!);
                return "Domain properties/attributes written and read back. No save/compile/download.";
            });

        // ---- transfer areas ---------------------------------------------------------------------------------------------
        public ResponseMessage ReadTransferAreas(string devicePathJson, string itemPathJson, int positionNumber = -1, int extendedPositionNumber = -1, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadTransferAreas", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit); HardwareNetworkLogic.RequirePosition(positionNumber, extendedPositionNumber);
                var network = RequireNetworkInterface(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                var rows = new List<JsonNode>();
                if (positionNumber >= 0)
                {
                    var found = extendedPositionNumber >= 0 ? network.TransferAreas.Find(positionNumber, extendedPositionNumber) : network.TransferAreas.Find(positionNumber);
                    if (found == null) throw new PortalException(PortalErrorCode.NotFound, "No transfer area at the requested position.");
                    rows.Add(TransferAreaRow(found));
                }
                else
                {
                    rows.AddRange(EngineeringGroupOperations.Items(network.TransferAreas).Cast<TransferArea>().Select(a => (JsonNode)TransferAreaRow(a)));
                    rows.AddRange(EngineeringGroupOperations.Items(network.MulticastableTransferAreas).Cast<MulticastableTransferArea>().Select(a => (JsonNode)MulticastRow(a)));
                }
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "TransferArea and MulticastableTransferArea scalars, address rows, mapping rules and official dynamic attributes (per-attribute failures listed).";
                return "Transfer areas of the interface read; no modification.";
            });

        public ResponseMessage ManageTransferArea(string devicePathJson, string itemPathJson, string action, string kind = "standard", string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]", string senderName = "", int length = -1,
            string propertiesJson = "{}", string attributesJson = "{}", int ruleIndex = -1, string targetDevicePathJson = "[]", string targetItemPathJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageTransferArea", meta => {
                HardwareNetworkLogic.ValidateTransferAreaRequest(kind, action, name, type, positionNumber, extendedPositionNumber, length, ruleIndex);
                if (action == "delete" || action == "deleteMappingRule") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var network = RequireNetworkInterface(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                if (kind == "multicast")
                {
                    Func<object> fresh = () => network.MulticastableTransferAreas;
                    var composition = network.MulticastableTransferAreas; var all = EngineeringGroupOperations.Items(composition).Cast<MulticastableTransferArea>().ToArray();
                    meta["countBefore"] = all.Length;
                    var areaType = ParseTransferAreaType(string.IsNullOrEmpty(type) ? "None" : type);
                    if (action == "create" || action == "createReceiver")
                    {
                        MulticastableTransferArea created;
                        if (action == "create")
                        {
                            var partner = RequireNetworkInterface(ExactEngineeringHardware(partnerDevicePathJson, partnerItemPathJson), "partnerItemPathJson");
                            meta["partnerOwnerPath"] = HardwareOwnerPath(partner); meta["requestedName"] = name; meta["requestedLength"] = length;
                            if (dryRun) return "Multicast (CCDX) transfer area create preview; nothing changed.";
                            meta["mayHaveChanged"] = true;
                            created = length >= 0 ? composition.Create(partner, areaType, HardwareNetworkLogic.RequireExactName(name, "name"), length)
                                : !string.IsNullOrEmpty(name) ? composition.Create(partner, areaType, name) : composition.Create(partner, areaType);
                        }
                        else
                        {
                            var sender = all.FirstOrDefault(a => string.Equals(a.Name, HardwareNetworkLogic.RequireExactName(senderName, "senderName"), StringComparison.Ordinal))
                                ?? throw new PortalException(PortalErrorCode.NotFound, "Sender transfer area not found on this interface: " + senderName);
                            meta["sender"] = MulticastRow(sender);
                            if (dryRun) return "Receiver transfer area create preview; nothing changed.";
                            meta["mayHaveChanged"] = true; created = composition.Create(sender, areaType);
                        }
                        meta["after"] = MulticastRow(created); meta["createdName"] = created.Name; CountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area created and read back from the returned proxy. No save/compile/download.";
                    }
                    var target = all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal)) ?? throw new PortalException(PortalErrorCode.NotFound, "Multicast transfer area not found: " + name);
                    meta["before"] = MulticastRow(target);
                    if (action == "delete")
                    {
                        meta["partnerCount"] = EngineeringGroupOperations.Items(target.PartnerTransferAreas).Count();
                        meta["deleteSemantics"] = "Deleting a sender deletes every receiver; deleting the last receiver also deletes the sender.";
                        if (dryRun) return "Multicast transfer area delete preview; nothing changed.";
                        meta["mayHaveChanged"] = true; target.Delete();
                        if (FindOnFresh(fresh, name, meta, "postDelete") != null) throw new InvalidOperationException("Delete returned but the transfer area is still present on the refreshed composition; do not blindly retry.");
                        meta["verifiedAbsent"] = true; CountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area deleted and verified absent. No save/compile/download.";
                    }
                    ApplyScalarsAndAttributes(target, propertiesJson, attributesJson, meta, !dryRun);
                    if (dryRun) return "Multicast transfer area update preview; nothing changed.";
                    meta["after"] = MulticastRow(target);
                    return "Multicast transfer area written and read back. No save/compile/download.";
                }
                Func<object> freshAreas = () => network.TransferAreas;
                var areas = network.TransferAreas; var existing = EngineeringGroupOperations.Items(areas).Cast<TransferArea>().ToArray();
                meta["countBefore"] = existing.Length;
                if (action == "create")
                {
                    if (existing.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal))) throw new InvalidOperationException("Transfer area name already used on this interface: " + name);
                    meta["requestedName"] = name; meta["requestedType"] = type; meta["requestedPositionNumber"] = positionNumber;
                    if (dryRun) return "Transfer area create preview; nothing changed (type cannot be changed afterwards).";
                    meta["mayHaveChanged"] = true;
                    var areaType = ParseTransferAreaType(type);
                    var created = positionNumber >= 0 ? areas.Create(name, areaType, positionNumber) : areas.Create(name, areaType);
                    meta["after"] = TransferAreaRow(created); meta["createdName"] = created.Name; CountOnFresh(freshAreas, meta, "countAfter");
                    return "Transfer area created and read back from the returned proxy. No save/compile/download.";
                }
                TransferArea? area = positionNumber >= 0
                    ? (extendedPositionNumber >= 0 ? areas.Find(positionNumber, extendedPositionNumber) : areas.Find(positionNumber))
                    : existing.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
                if (area == null) throw new PortalException(PortalErrorCode.NotFound, "Transfer area not found.");
                meta["before"] = TransferAreaRow(area);
                if (action == "delete")
                {
                    if (dryRun) return "Transfer area delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; area.Delete();
                    var countAfter = CountOnFresh(freshAreas, meta, "countAfter");
                    if (countAfter != null && countAfter != existing.Length - 1) throw new InvalidOperationException("Delete returned but the refreshed transfer area count did not drop by one; do not blindly retry.");
                    meta["verifiedAbsent"] = true;
                    return "Transfer area deleted and count verified. No save/compile/download.";
                }
                if (action == "update")
                {
                    ApplyScalarsAndAttributes(area, propertiesJson, attributesJson, meta, !dryRun);
                    if (dryRun) return "Transfer area update preview; nothing changed.";
                    meta["after"] = TransferAreaRow(area);
                    return "Transfer area written and read back. No save/compile/download.";
                }
                // Mapping rules (IO routing): Create() then Begin/End/IoType/Offset/Target; Target is a module of the IO device.
                var rules = area.TransferAreaMappingRules; var ruleList = EngineeringGroupOperations.Items(rules).Cast<TransferAreaMappingRule>().ToArray();
                meta["ruleCountBefore"] = ruleList.Length;
                DeviceItem? ruleTarget = targetDevicePathJson != "[]" && !string.IsNullOrWhiteSpace(targetDevicePathJson) ? RequireDeviceItem(ExactEngineeringHardware(targetDevicePathJson, targetItemPathJson), "targetItemPathJson") : null;
                if (ruleTarget != null) meta["ruleTargetPath"] = HardwareOwnerPath(ruleTarget);
                var ruleProperties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); meta["requestedProperties"] = ruleProperties.DeepClone();
                var prepared = EngineeringScalarProperties.Prepare(typeof(TransferAreaMappingRule), ruleProperties);
                if (action == "createMappingRule")
                {
                    if (dryRun) return "Mapping rule create preview; nothing changed.";
                    meta["mayHaveChanged"] = true; var rule = rules.Create();
                    if (prepared.Count > 0) EngineeringScalarProperties.Apply(rule, prepared, meta);
                    if (ruleTarget != null) { rule.Target = ruleTarget; if (!ReferenceEquals(rule.Target, ruleTarget) && rule.Target?.Name != ruleTarget.Name) throw new InvalidOperationException("Target readback differs."); }
                    meta["after"] = MappingRuleRow(rule, ruleList.Length); meta["ruleCountAfter"] = EngineeringGroupOperations.Items(rules).Count();
                    return "Mapping rule created and read back. No save/compile/download.";
                }
                if (ruleIndex >= ruleList.Length) throw new PortalException(PortalErrorCode.NotFound, "ruleIndex out of range (" + ruleList.Length + " rules).");
                var existingRule = ruleList[ruleIndex]; meta["ruleBefore"] = MappingRuleRow(existingRule, ruleIndex);
                if (action == "deleteMappingRule")
                {
                    if (dryRun) return "Mapping rule delete preview; nothing changed.";
                    meta["mayHaveChanged"] = true; existingRule.Delete();
                    var ruleCountAfter = CountOnFresh(() => area.TransferAreaMappingRules, meta, "ruleCountAfter");
                    if (ruleCountAfter != null && ruleCountAfter != ruleList.Length - 1) throw new InvalidOperationException("Delete returned but the refreshed rule count did not drop by one.");
                    meta["verifiedAbsent"] = true;
                    return "Mapping rule deleted and count verified. No save/compile/download.";
                }
                if (dryRun) return "Mapping rule update preview; nothing changed.";
                if (prepared.Count > 0) EngineeringScalarProperties.Apply(existingRule, prepared, meta);
                if (ruleTarget != null) { meta["mayHaveChanged"] = true; existingRule.Target = ruleTarget; }
                meta["ruleAfter"] = MappingRuleRow(existingRule, ruleIndex);
                return "Mapping rule written and read back. No save/compile/download.";
            });

        // ---- channels ----------------------------------------------------------------------------------------------------
        private static Channel ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels.Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
               ?? throw new PortalException(PortalErrorCode.NotFound, "Channel not found: " + channelType + "/" + channelIoType + "/" + channelNumber);
        public ResponseMessage ReadDeviceItemChannels(string devicePathJson, string itemPathJson, string channelType = "", string channelIoType = "", int channelNumber = -1, string attributeNamesJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadDeviceItemChannels", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                bool exact = HardwareNetworkLogic.ChannelIdentityGiven(channelType, channelIoType, channelNumber);
                var extra = HardwareNetworkLogic.ParseNames(attributeNamesJson, "attributeNamesJson");
                var item = RequireDeviceItem(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["ownerPath"] = HardwareOwnerPath(item);
                var channels = exact ? new[] { ExactChannel(item, channelType, channelIoType, channelNumber) } : EngineeringGroupOperations.Items(item.Channels).Cast<Channel>().ToArray();
                Page(channels.Select(c => (JsonNode)ChannelRow(c, extra)).ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Channel Number/Type/IoType, attribute names from GetAttributeInfos, values of ChannelAddress/ChannelWidth plus requested names (failures listed).";
                return "Channels of the device item read; no modification.";
            });
        public ResponseMessage UpdateDeviceItemChannel(string devicePathJson, string itemPathJson, string channelType, string channelIoType, int channelNumber, string attributesJson, bool dryRun = true)
            => RunHmiStepTool("UpdateDeviceItemChannel", meta => {
                if (!HardwareNetworkLogic.ChannelIdentityGiven(channelType, channelIoType, channelNumber)) throw new ArgumentException("channelType, channelIoType and channelNumber are required.");
                var attributes = HardwareNetworkLogic.ParseObject(attributesJson, "attributesJson");
                if (attributes.Count == 0) throw new ArgumentException("attributesJson must name at least one attribute.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var item = RequireDeviceItem(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                var channel = ExactChannel(item, channelType, channelIoType, channelNumber);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(item);
                var names = attributes.Select(p => p.Key).ToArray();
                meta["before"] = ChannelRow(channel, names); meta["requestedAttributes"] = attributes.DeepClone();
                var modes = ChannelAttributeModes(channel);
                var readOnly = names.Where(n => modes.TryGetValue(n, out var mode) && mode != "Write" && mode != "ReadWrite").ToArray();
                var unknown = names.Where(n => !modes.ContainsKey(n)).ToArray();
                meta["readOnlyAttributes"] = new JsonArray(readOnly.Select(n => (JsonNode)n).ToArray()); meta["unlistedAttributes"] = new JsonArray(unknown.Select(n => (JsonNode)n).ToArray());
                if (readOnly.Length > 0) throw new NotSupportedException("Attribute(s) are not writable on this channel per GetAttributeInfos: " + string.Join(", ", readOnly) + ". Nothing was written.");
                if (dryRun) return "Channel attribute write preview; nothing changed" + (unknown.Length > 0 ? " (attributes not listed by GetAttributeInfos are attempted as-is: " + string.Join(", ", unknown) + ")" : "") + ".";
                SetDynamicAttributes(channel, attributes, meta);
                meta["after"] = ChannelRow(channel, names);
                return "Channel attributes written and read back. No save/compile/download.";
            });

        // ---- addressing -------------------------------------------------------------------------------------------------
        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadDeviceAddressing", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["ownerPath"] = HardwareOwnerPath(owner); meta["ownerType"] = owner.GetType().Name;
                meta["hwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(owner.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)HwIdentifierRow(h)).ToArray());
                var rows = new List<JsonNode>();
                if (owner is DeviceItem item)
                {
                    rows.AddRange(EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)));
                    var addressController = ServiceProvider(item).GetService<AddressController>(); var hwController = ServiceProvider(item).GetService<HwIdentifierController>();
                    meta["isAddressController"] = addressController != null; meta["isHwIdentifierController"] = hwController != null;
                    if (addressController != null)
                        meta["registeredAddresses"] = new JsonArray(EngineeringGroupOperations.Items(addressController.RegisteredAddresses).Cast<Address>()
                            .Select(a => { var r = AddressRow(a); r["ownerPath"] = HardwareOwnerPath(a); return (JsonNode)r; }).ToArray());
                    if (hwController != null)
                        meta["registeredHwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(hwController.RegisteredHwIdentifiers).Cast<HwIdentifier>()
                            .Select(h => { var r = HwIdentifierRow(h); r["ownerPath"] = HardwareOwnerPath(h); return (JsonNode)r; }).ToArray());
                }
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Address StartAddress/Length/IoType, controller owner paths and official dynamic attributes; HwIdentifiers; AddressController/HwIdentifierController registrations when the item is a controller.";
                return "Addressing of the hardware object read; no modification.";
            });
        public ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress, string propertiesJson = "{}", string attributesJson = "{}",
            string softwarePath = "", string processImageObName = "", bool dryRun = true)
            => RunHmiStepTool("UpdateDeviceAddress", meta => {
                HardwareNetworkLogic.RequireOneOf(ioType, HardwareNetworkLogic.AddressIoTypes, "ioType");
                if (startAddress < 0) throw new ArgumentException("startAddress identifies the existing address (>= 0).");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var item = RequireDeviceItem(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                var wanted = (AddressIoType)Enum.Parse(typeof(AddressIoType), ioType);
                var matches = EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Where(a => a.IoType == wanted && a.StartAddress == startAddress).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, matches.Length == 0 ? "No address with that IoType/StartAddress on the item." : "Ambiguous address identity.");
                var address = matches[0];
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(item); meta["before"] = AddressRow(address);
                meta["restrictions"] = "Changing StartAddress may move the opposite IoType of the same module and never rewires tags; packed addresses are unsupported.";
                // Resolve (and on V21 refuse) the OB assignment before any property is written, so a refusal never leaves a partial edit.
#if TIA_V20
                OB? ob = null;
                if (!string.IsNullOrEmpty(processImageObName))
                {
                    ob = GetBlock(HardwareNetworkLogic.RequireExactName(softwarePath, "softwarePath"), processImageObName) as OB ?? throw new PortalException(PortalErrorCode.NotFound, "OB not found in the PLC software: " + processImageObName);
                    meta["processImageOb"] = ob.Name;
                }
#else
                if (!string.IsNullOrEmpty(processImageObName)) throw new NotSupportedException("Address.AssignProcessImageToOrganizationBlock exists only in the V20 PublicAPI; assign the process image partition in the OB's properties on V21.");
#endif
                ApplyScalarsAndAttributes(address, propertiesJson, attributesJson, meta, !dryRun);
#if TIA_V20
                if (ob != null && !dryRun) { meta["mayHaveChanged"] = true; address.AssignProcessImageToOrganizationBlock(ob); meta["processImageAssigned"] = true; }
#endif
                if (dryRun) return "Address update preview; nothing changed.";
                meta["after"] = AddressRow(address);
                return "Address properties/attributes written and read back. No save/compile/download.";
            });

        // ---- device user groups ------------------------------------------------------------------------------------------
        public ResponseMessage ManageDeviceUserGroup(string groupPath = "", string action = "read", string newName = "", bool dryRun = true)
            => RunHmiStepTool("ManageDeviceUserGroup", meta => {
                HardwareNetworkLogic.RequireOneOf(action, HardwareNetworkLogic.DeviceGroupActions, "action");
                using var access = action != "read" && !dryRun ? AcquireHmiEditAccess() : null;
                meta["groupPath"] = groupPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    JsonObject GroupRow(DeviceGroup g) => new JsonObject
                    {
                        ["name"] = g.Name, ["type"] = g.GetType().Name,
                        ["devices"] = new JsonArray(EngineeringGroupOperations.Items(g.Devices).Cast<Device>().Select(d => (JsonNode)d.Name).ToArray()),
                        ["groups"] = g is DeviceUserGroup u ? new JsonArray(EngineeringGroupOperations.Items(u.Groups).Cast<DeviceUserGroup>().Select(x => (JsonNode)x.Name).ToArray()) : new JsonArray()
                    };
                    if (string.IsNullOrEmpty(groupPath))
                    {
                        meta["rootDevices"] = new JsonArray(EngineeringGroupOperations.Items(_project!.Devices).Cast<Device>().Select(d => (JsonNode)d.Name).ToArray());
                        meta["records"] = new JsonArray(EngineeringGroupOperations.Items(_project.DeviceGroups).Cast<DeviceUserGroup>().Select(g => (JsonNode)GroupRow(g)).ToArray());
                        meta["ungroupedDevicesGroup"] = GroupRow(_project.UngroupedDevicesGroup);
                    }
                    else meta["records"] = new JsonArray(GroupRow((DeviceUserGroup)EngineeringGroupOperations.Group(_project!, groupPath, "DeviceGroups")));
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    return "Device groups read; no modification.";
                }
                meta["result"] = EngineeringGroupOperations.Manage(_project!, groupPath, action, newName, dryRun, "Devices", "DeviceGroups");
                if (!dryRun) meta["mayHaveChanged"] = true;
                return dryRun ? "Device user group preview; nothing changed." : "Device user group operation completed (CreateFrom(MasterCopy) is not exposed here). No save/compile/download.";
            });

        // ---- device users (web server / SIWAREX simple web server / OPC UA) --------------------------------------------------
        public ResponseMessage ManageDeviceUsers(string devicePathJson, string itemPathJson, string family, string action = "read", string userName = "", string password = "",
            string permissionsJson = "[]", bool active = true, string newName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageDeviceUsers", meta => {
                var permissionNames = HardwareNetworkLogic.ParseNames(permissionsJson, "permissionsJson", 32);
                HardwareNetworkLogic.ValidateUserRequest(family, action, userName, password, permissionNames, newName);
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                bool write = action != "read" && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["family"] = family; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(owner);
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                Func<object> fresh = family switch
                {
                    "webserver" => () => RequireHardwareService<WebserverUserManagement>(owner, "itemPathJson").WebserverUsers,
                    "simpleWebserver" => () => (object)RequireHardwareService<SimpleWebserverUserManagement>(owner, "itemPathJson").WebserverUsers,
                    _ => () => (object)RequireHardwareService<OpcUaUserManagement>(owner, "itemPathJson").OpcUaUsers
                };
                object composition = fresh();
                var users = EngineeringGroupOperations.Items(composition).ToArray(); meta["countBefore"] = users.Length;
                if (action == "read")
                {
                    meta["records"] = new JsonArray(users.Select(u => (JsonNode)UserRow(u)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["scope"] = "UserName, Permissions (flag names) and Active; passwords are never readable.";
                    return "Device users read; no modification.";
                }
                object? user = users.FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal));
                if ((action == "create") == (user != null)) throw new InvalidOperationException(action == "create" ? "User exists: " + userName : "User not found: " + userName);
                if (user != null) meta["before"] = UserRow(user);
                string joined = permissionNames.Length == 0 ? "None" : string.Join(", ", permissionNames);
                meta["requestedPermissions"] = new JsonArray(permissionNames.Select(x => (JsonNode)x).ToArray());
                if (!write) return "Device user " + action + " preview; nothing changed (TIA refuses create/delete/setPassword while the web server or OPC UA authentication is disabled).";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create":
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password))
                            user = family == "webserver"
                                ? ((WebserverUserComposition)composition).Create(userName, (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined), secure)
                                : (object)((OpcUaUserComposition)composition).Create(userName, secure);
                        meta["createdName"] = user.GetType().GetProperty("UserName")?.GetValue(user)?.ToString();
                        if (CountOnFresh(fresh, meta, "countAfter") is int grown && grown != users.Length + 1) throw new InvalidOperationException("Create returned but the refreshed user count did not grow by one.");
                        break;
                    case "delete":
                        EngineeringGroupOperations.Call(user!, "Delete", Type.EmptyTypes);
                        if (FindUserOnFresh(fresh, userName, meta) != null) throw new InvalidOperationException("Delete returned but the user is still listed on the refreshed composition; do not blindly retry.");
                        meta["verifiedAbsent"] = true; CountOnFresh(fresh, meta, "countAfter");
                        return "Device user deleted and count verified. No save/compile/download.";
                    case "setPassword":
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password)) EngineeringGroupOperations.Call(user!, "SetPassword", new[] { typeof(System.Security.SecureString) }, secure);
                        meta["passwordReadbackPossible"] = false;
                        break;
                    case "setPermissions":
                        if (user is WebserverUser web) web.Permissions = (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined);
                        else ((SimpleWebserverUser)user!).Permissions = (SimpleWebserverUserPermissions)Enum.Parse(typeof(SimpleWebserverUserPermissions), joined);
                        break;
                    case "setActive":
                        ((SimpleWebserverUser)user!).Active = active;
                        if (((SimpleWebserverUser)user).Active != active) throw new InvalidOperationException("Active readback differs.");
                        break;
                    case "rename":
                        ((SimpleWebserverUser)user!).UserName = newName;
                        if (((SimpleWebserverUser)user).UserName != newName) throw new InvalidOperationException("UserName readback differs.");
                        break;
                }
                meta["after"] = UserRow(user!); if (meta["countAfter"] == null) CountOnFresh(fresh, meta, "countAfter");
                return "Device user operation completed and read back (password never echoed). No save/compile/download.";
            });

        private static object? FindUserOnFresh(Func<object> composition, string userName, JsonObject meta)
        {
            try { return EngineeringGroupOperations.Items(composition()).FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal)); }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex)) { meta["postDeleteEnumeration"] = ex.GetBaseException().Message; return null; }
        }

        // ---- port interconnections -----------------------------------------------------------------------------------------
        public ResponseMessage ManagePortInterconnection(string devicePathJson, string itemPathJson, string action = "read", string partnerDevicePathJson = "[]", string partnerItemPathJson = "[]", bool dryRun = true)
            => RunHmiStepTool("ManagePortInterconnection", meta => {
                HardwareNetworkLogic.RequireOneOf(action, HardwareNetworkLogic.PortActions, "action");
                bool write = action != "read" && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                var port = RequireHardwareService<NetworkPort>(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["port"] = PortRow(port);
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Port interconnections read; no modification."; }
                var partner = RequireHardwareService<NetworkPort>(ExactEngineeringHardware(partnerDevicePathJson, partnerItemPathJson), "partnerItemPathJson");
                meta["partner"] = PortRow(partner);
                bool linked = EngineeringGroupOperations.Items(port.ConnectedPorts).Any(p => ReferenceEquals(p, partner) || (p is NetworkPort np && np.OwnedBy?.Name == partner.OwnedBy?.Name && HardwareOwnerPath(np).ToJsonString() == HardwareOwnerPath(partner).ToJsonString()));
                meta["linkedBefore"] = linked;
                if (action == "connect" && linked) throw new InvalidOperationException("Ports are already interconnected.");
                if (action == "disconnect" && !linked) throw new InvalidOperationException("Ports are not interconnected.");
                if (!write) return "Port " + action + " preview; nothing changed (TIA refuses ports of the same interface and second partners on ports without alternative partners).";
                meta["mayHaveChanged"] = true;
                if (action == "connect") port.ConnectToPort(partner); else port.DisconnectFromPort(partner);
                var after = PortRow(port); meta["after"] = after;
                int count = after["connectedPorts"]!.AsArray().Count; int before = ((JsonObject)meta["port"]!)["connectedPorts"]!.AsArray().Count;
                if (count != before + (action == "connect" ? 1 : -1)) throw new InvalidOperationException("Native call returned but ConnectedPorts count did not change as expected.");
                return "Port " + action + " completed and ConnectedPorts count verified. No save/compile/download.";
            });
    }
}
