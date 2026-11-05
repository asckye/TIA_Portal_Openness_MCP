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
            ["localAddresses"] = new List<object
                ?>(HardwareGroupOperations.Items(area.LocalAddresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
            ["partnerAddresses"] = new List<object
                ?>(HardwareGroupOperations.Items(area.PartnerAddresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
            ["mappingRules"] = new List<object
                ?>(HardwareGroupOperations.Items(area.TransferAreaMappingRules).Cast<TransferAreaMappingRule>()
                    .Select((r, i) => (object)HardwareMappingRuleRow(r, i)).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkPolicy.TransferAreaAttributes)
        };
#endif

#if PLC_HARDWARE_TRANSFER_MULTICAST
        private Dictionary<string, object?> HardwareMulticastRow(MulticastableTransferArea area) => new Dictionary<string, object?>
        {
            ["kind"] = "multicast", ["name"] = area.Name, ["type"] = area.Type.ToString(), ["direction"] = area.Direction.ToString(), ["comment"] = area
                .Comment,
#if PLC_HARDWARE_TRANSFER_MULTICAST_V19
            ["dataLength"] = area.DataLength,
            ["addresses"] = new List<object?>(HardwareGroupOperations.Items(area.Addresses).Cast<Address>().Select(a => (object)AddressRow(a)).ToArray()),
#else
            // V18 exposes the single MulticastableTransferArea.Address; DataLength and Addresses arrive in V19.
            ["dataLength"] = null, ["addresses"] = area.Address == null ? new List<object?>() : new List<object?> { AddressRow(area.Address) },
#endif
            ["partnerTransferAreas"] = new List<object?>(HardwareGroupOperations.Items(area.PartnerTransferAreas).Cast<MulticastableTransferArea>()
                .Select(p => (object)new Dictionary<string, object?> { ["name"] = p.Name, ["ownerPath"] = HardwareOwnerPath(p) }).ToArray()),
            ["attributes"] = DynamicAttributes(area, HardwareNetworkPolicy.MulticastTransferAreaAttributes)
        };
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        public HardwareAddressingReply HardwareReadTransferAreas(string[] devicePathJson, string[] itemPathJson, int positionNumber = -1,
                int extendedPositionNumber = -1, int offset = 0, int limit = 100)
            => RunHardwareAddressStep("ListTransferAreas", meta => {
                HardwareServicesPolicy.ValidatePagination(offset, limit); HardwareNetworkPolicy.RequirePosition(positionNumber, extendedPositionNumber);
                var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["interfaceOwnerPath"] = HardwareOwnerPath(network);
                var rows = new List<object>();
                if (positionNumber >= 0)
                {
                    #if PLC_HARDWARE_DOMAINS
                    var found = extendedPositionNumber >= 0 ? network.TransferAreas.Find(positionNumber, extendedPositionNumber) : network.TransferAreas
                        .Find(positionNumber);
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
                    rows
                        .AddRange(HardwareGroupOperations.Items(network.MulticastableTransferAreas).Cast<MulticastableTransferArea>()
                            .Select(a => (object)HardwareMulticastRow(a)));
#endif
                }
                HardwarePage(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] =
                    "TransferArea and MulticastableTransferArea scalars, address rows, mapping rules and official dynamic attributes (per-attribute failures " +
                    "listed).";
                return "Transfer areas of the interface read; no modification.";
            });
#else
        public HardwareAddressingReply HardwareReadTransferAreas(string[] devicePathJson, string[] itemPathJson, int positionNumber = -1,
                int extendedPositionNumber = -1, int offset = 0, int limit = 100) =>
            throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

#if PLC_HARDWARE_TRANSFER_AREAS
        public HardwareAddressingReply HardwareManageTransferArea(string[] devicePathJson, string[] itemPathJson, string action, string kind = "standard",
                string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!,
                string senderName = "", int length = -1,
            Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!, int ruleIndex = -1,
                string[] targetDevicePathJson = null!, string[] targetItemPathJson = null!, bool confirmDelete = false, bool dryRun = true)
            => RunHardwareAddressStep("ManageTransferArea", meta => {
                HardwareNetworkPolicy.ValidateTransferAreaRequest(kind, action, name, type, positionNumber, extendedPositionNumber, length, ruleIndex);
                if (action == "delete" || action == "deleteMappingRule") HardwareServicesPolicy.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                using var access = dryRun ? null : HardwareEditAccess();
                var network = HardwareRequireNetworkInterface(ExactHardware(devicePathJson, itemPathJson), "itemPathJson");
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["interfaceOwnerPath"] =
                    HardwareOwnerPath(network);
                if (kind == "multicast")
                {
#if PLC_HARDWARE_TRANSFER_MULTICAST_V19
                    Func<object> fresh = () => network.MulticastableTransferAreas;
                    var composition = network.MulticastableTransferAreas; var all = HardwareGroupOperations.Items(composition).Cast<MulticastableTransferArea>()
                        .ToArray();
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
                            var sender = all
                                .FirstOrDefault(a => string
                                    .Equals(a.Name, HardwareNetworkPolicy.RequireExactName(senderName, "senderName"), StringComparison.Ordinal))
                                ?? throw new HardwareAddressingException("NotFound", "Sender transfer area not found on this interface: " + senderName);
                            meta["sender"] = HardwareMulticastRow(sender);
                            if (dryRun) return "Receiver transfer area create preview; nothing changed.";
                            meta["mayHaveChanged"] = true; created = composition.Create(sender, areaType);
                        }
                        meta["after"] = HardwareMulticastRow(created); meta["createdName"] = created.Name; HardwareCountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area created and read back from the returned proxy. No save/compile/download.";
                    }
                    var target = all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                        ?? throw new HardwareAddressingException("NotFound", "Multicast transfer area not found: " + name);
                    meta["before"] = HardwareMulticastRow(target);
                    if (action == "delete")
                    {
                        meta["partnerCount"] = HardwareGroupOperations.Items(target.PartnerTransferAreas).Count();
                        meta["deleteSemantics"] = "Deleting a sender deletes every receiver; deleting the last receiver also deletes the sender.";
                        if (dryRun) return "Multicast transfer area delete preview; nothing changed.";
                        meta["mayHaveChanged"] = true; target.Delete();
                        if (HardwareFindOnFresh(fresh, name, meta, "postDelete") != null)
                            throw new InvalidOperationException("Delete returned but the transfer area is still present on the refreshed composition; do not " +
                            "blindly retry.");
                        meta["verifiedAbsent"] = true; HardwareCountOnFresh(fresh, meta, "countAfter");
                        return "Multicast transfer area deleted and verified absent. No save/compile/download.";
                    }
                    ApplyScalarsAndAttributes(target, propertiesJson, attributesJson, meta, !dryRun);
                    if (dryRun) return "Multicast transfer area update preview; nothing changed.";
                    meta["after"] = HardwareMulticastRow(target);
                    return "Multicast transfer area written and read back. No save/compile/download.";
#else
                    throw new NotSupportedException("Multicast transfer area mutations require V19 (MulticastableTransferAreaComposition.Create and " +
                        "MulticastableTransferArea.Delete).");
#endif
                }
                Func<object> freshAreas = () => network.TransferAreas;
                var areas = network.TransferAreas; var existing = HardwareGroupOperations.Items(areas).Cast<TransferArea>().ToArray();
                meta["countBefore"] = existing.Length;
                if (action == "create")
                {
                    if (existing.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
                        throw new InvalidOperationException("Transfer area name already used on this interface: " + name);
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
                    if (countAfter != null && countAfter != existing.Length - 1)
                        throw new InvalidOperationException("Delete returned but the refreshed transfer area count did not drop by one; do not blindly retry.");
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
                DeviceItem? ruleTarget = targetDevicePathJson != null && targetDevicePathJson.Length > 0
                    ? HardwareRequireDeviceItem(ExactHardware(targetDevicePathJson, targetItemPathJson), "targetItemPathJson") : null;
                if (ruleTarget != null) meta["ruleTargetPath"] = HardwareOwnerPath(ruleTarget);
                var ruleProperties = HardwareNetworkPolicy.ParseObject(propertiesJson, "propertiesJson"); meta["requestedProperties"] = HardwareScalarEvidence
                    .Requested(ruleProperties);
                var prepared = HardwareAddressingPolicy.Prepare(typeof(TransferAreaMappingRule), ruleProperties);
                if (action == "createMappingRule")
                {
                    if (dryRun) return "Mapping rule create preview; nothing changed.";
                    meta["mayHaveChanged"] = true; var rule = rules.Create();
                    if (prepared.Count > 0) HardwareScalarEvidence.Apply(rule, prepared, meta);
                    if (ruleTarget != null) {
                        rule.Target = ruleTarget; if (!ReferenceEquals(rule.Target, ruleTarget) && rule.Target?.Name != ruleTarget.Name)
                            throw new InvalidOperationException("Target readback differs.");
                    }
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
                    if (ruleCountAfter != null && ruleCountAfter != ruleList.Length - 1)
                        throw new InvalidOperationException("Delete returned but the refreshed rule count did not drop by one.");
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
        public HardwareAddressingReply HardwareManageTransferArea(string[] devicePathJson, string[] itemPathJson, string action, string kind = "standard",
                string name = "", string type = "",
            int positionNumber = -1, int extendedPositionNumber = -1, string[] partnerDevicePathJson = null!, string[] partnerItemPathJson = null!,
                string senderName = "", int length = -1,
            Dictionary<string, HardwareScalar> propertiesJson = null!, Dictionary<string, HardwareScalar> attributesJson = null!, int ruleIndex = -1,
                string[] targetDevicePathJson = null!, string[] targetItemPathJson = null!, bool confirmDelete = false, bool dryRun = true) =>
                throw new NotSupportedException("This hardware API is unavailable in this release.");
#endif

    }
}
