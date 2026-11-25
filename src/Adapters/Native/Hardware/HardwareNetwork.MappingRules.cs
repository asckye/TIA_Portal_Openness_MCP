using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Engineering;
using static TiaMcp.Adapters.Hardware.HardwareAddressPrimitives;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
#if PLC_HARDWARE_TRANSFER_AREAS
        internal string ManageMappingRule(TransferArea area, string action, int ruleIndex, Dictionary<string, HardwareScalar> propertiesJson,
            string[] targetDevicePathJson, string[] targetItemPathJson, bool dryRun, Dictionary<string, object?> meta)
        {
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
                var rule = EngineeringWriteJournal.ProbeCreate(meta, "TransferAreaMappingRules.Create",
                    () => new object?[] { HardwareGroupOperations.Items(area.TransferAreaMappingRules).Count() }, () => rules.Create());
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
            if (ruleTarget != null)
            {
                EngineeringWriteJournal.ProbeWrite(meta, "TransferAreaMappingRule.Target",
                    () => new object?[] { existingRule.Target is { } target ? ProjectOwnerPath(project!, target) : null },
                    () => existingRule.Target = ruleTarget);
            }
            meta["ruleAfter"] = HardwareMappingRuleRow(existingRule, ruleIndex);
            return "Mapping rule written and read back. No save/compile/download.";
        }
#endif
    }
}
