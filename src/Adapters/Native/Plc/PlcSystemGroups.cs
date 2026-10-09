using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static Dictionary<string, object?> SystemBlockGroupRow(PlcSystemBlockGroup group, bool includeBlocks, int depth, int maxDepth)
        {
            var blocks = group.Blocks; var groups = group.Groups;
            var row = new Dictionary<string, object?> { ["name"] = group.Name, ["blockCount"] = blocks.Count, ["groupCount"] = groups.Count };
            if (includeBlocks) row["blocks"] = PlcGroupOperations.Items(blocks).Cast<PlcBlock>().Take(200).Select(b => new Dictionary<string, object?> {
                ["name"] = PlcDocumentPrimitives.Name(b), ["number"] = b.Number, ["blockClass"] = b.GetType().Name,
                ["programmingLanguage"] = PlcDocumentPrimitives.Language(b).ToString()
            }).ToArray();
            if (depth < maxDepth) row["groups"] = PlcGroupOperations.Items(groups).Cast<PlcSystemBlockGroup>().Select(g => SystemBlockGroupRow(g, includeBlocks, depth + 1, maxDepth)).ToArray();
            else row["groupsTruncated"] = groups.Count > 0;
            return row;
        }

        public HardwareAddressingReply ReadPlcSystemGroups(string softwarePath, string unitName = "", string unitKind = "unit", bool includeBlocks = true, int maxDepth = 4)
            => _session.RunHmiStepTool("ListPlcSystemGroups", meta => {
                if (!string.IsNullOrEmpty(unitName) && unitKind != "unit" && unitKind != "safety") throw new ArgumentException("unitKind must be one of: unit/safety (case-sensitive).");
                if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var blockRoot = (PlcBlockSystemGroup)_session.BlockRootOf(plc, unit);
                var typeRoot = (PlcTypeSystemGroup)_session.TypeRootOf(plc, unit);
                var systemBlockGroups = blockRoot.SystemBlockGroups;
#if PLC_SOFTWARE_PROTECTION
                var systemTypeGroups = typeRoot.SystemTypeGroups;
                meta["unit"] = unit == null ? null : PlcGroupOperations.Get(unit, "Name");
                meta["systemBlockGroups"] = PlcGroupOperations.Items(systemBlockGroups).Cast<PlcSystemBlockGroup>().Select(g => SystemBlockGroupRow(g, includeBlocks, 1, maxDepth)).ToArray();
                meta["systemTypeGroups"] = PlcGroupOperations.Items(systemTypeGroups).Cast<PlcSystemTypeGroup>().Select(g => {
                    var types = g.Types;
                    return new Dictionary<string, object?> { ["name"] = g.Name, ["typeCount"] = types.Count,
                        ["types"] = PlcGroupOperations.Items(types).Cast<PlcType>().Take(200).Select(t => PlcDocumentPrimitives.Name(t)).ToArray() };
                }).ToArray();
#else
                throw new NotSupportedException("SystemTypeGroups requires V15.1.");
#endif
                meta["apiCallSuccess"] = true;
                meta["scope"] = "PlcBlockSystemGroup.SystemBlockGroups (PlcSystemBlockGroup Name / Blocks / Groups, recursive to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types); first 200 objects per group. No modification.";
                return "System block / type groups read; no modification.";
            });
    }
}
