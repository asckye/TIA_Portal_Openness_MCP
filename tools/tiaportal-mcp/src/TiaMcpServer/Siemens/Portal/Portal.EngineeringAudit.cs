using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static bool RecoverableAuditError(Exception ex)
        {
            for (var cause = ex; cause != null; cause = cause.InnerException)
                if (cause is NonRecoverableException) return false;
            return true;
        }
        private static void ValidateUnitKind(string kind)
        { if (kind != "unit" && kind != "safety") throw new ArgumentException("unitKind must be unit or safety."); }

        private static IEnumerable<(string Name, string Kind, PlcUnitBase? Unit)> PlcScopes(PlcSoftware plc)
        {
            yield return ("", "root", null);
            var provider = InvocationJournal.Native("PlcUnitProvider.GetService", () => plc.GetService<PlcUnitProvider>());
            if (provider == null) yield break; // a PLC without unit support still has its root scope
            foreach (PlcUnit unit in EngineeringGroupOperations.Items(provider.UnitGroup.Units)) yield return (unit.Name, "unit", unit);
            foreach (PlcSafetyUnit unit in EngineeringGroupOperations.Items(provider.UnitGroup.SafetyUnits)) yield return (unit.Name, "safety", unit);
        }
        private static IEnumerable<(string Path, object Value)> ScopedObjects(object group, string collection, string prefix = "", int depth = 0)
        {
            if (depth > 64) throw new InvalidOperationException("Group depth exceeds 64; result is incomplete.");
            foreach (var value in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(group, collection)))
                yield return (prefix + EngineeringGroupOperations.Get(value, "Name"), value);
            foreach (var sub in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(group, "Groups")))
                foreach (var item in ScopedObjects(sub, collection, prefix + EngineeringGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
            var systemGroups = group.GetType().GetProperty("SystemBlockGroups");
            if (systemGroups != null && collection == "Blocks")
                foreach (var sub in EngineeringGroupOperations.Items(systemGroups.GetValue(group)!))
                    foreach (var item in ScopedObjects(sub, collection, prefix + EngineeringGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
        }
        private List<(string Path, bool? Consistent)> ReadPlcConsistency(string softwarePath)
        {
            var plc = GetPlcSoftware(softwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "PLC not found: " + softwarePath + AvailablePlcPathsSuffix());
            return InvocationJournal.Native("PLC.consistency.rootAndUnits", () => {
                var rows = new List<(string Path, bool? Consistent)>();
                foreach (var scope in PlcScopes(plc))
                {
                    string prefix = scope.Kind + ":" + scope.Name + "/";
                    foreach (var item in ScopedObjects(BlockRootOf(plc, scope.Unit), "Blocks"))
                        rows.Add((prefix + item.Path, ((PlcBlock)item.Value).IsConsistent));
                    foreach (var item in ScopedObjects(TypeRootOf(plc, scope.Unit), "Types"))
                        rows.Add((prefix + "Types/" + item.Path, ((PlcType)item.Value).IsConsistent));
                }
                return rows;
            });
        }
        private IEngineeringServiceProvider ExactCrossReferenceTarget(string softwarePath, string objectPath, string kind, string unitName, string unitKind)
        {
            ValidateUnitKind(unitKind);
            var plc = GetPlcSoftware(softwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "PLC not found: " + softwarePath + AvailablePlcPathsSuffix());
            var unit = OptionalUnit(plc, unitName, unitKind);
            if (kind.Equals("Block", StringComparison.OrdinalIgnoreCase)) return (PlcBlock)ExactObjectUnder(BlockRootOf(plc, unit), objectPath, "Blocks", "block");
            if (kind.Equals("Type", StringComparison.OrdinalIgnoreCase)) return (PlcType)ExactObjectUnder(TypeRootOf(plc, unit), objectPath, "Types", "type");
            if (!kind.Equals("Tag", StringComparison.OrdinalIgnoreCase) && !kind.Equals("SystemConstant", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("objectKind must be Block, Type, Tag or SystemConstant.");
            var parts = EngineeringGroupOperations.Parts(objectPath);
            if (parts.Length < 2) throw new ArgumentException("Tag/SystemConstant path must include the table: [group/]table/name.");
            object root = unit == null ? plc.TagTableGroup : unit.TagTableGroup;
            var table = (PlcTagTable)ExactObjectUnder(root, string.Join("/", parts.Take(parts.Length - 1)), "TagTables", "tag table");
            object collection = kind.Equals("Tag", StringComparison.OrdinalIgnoreCase) ? table.Tags : (object)table.SystemConstants;
            return (IEngineeringServiceProvider)(EngineeringGroupOperations.Find(collection, parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact tag/constant not found: " + objectPath));
        }
        public bool VerifyLastDocumentImport(string softwarePath, string groupPath)
        {
            var group = GetPlcBlockGroupByPath(softwarePath, groupPath) ?? throw new PortalException(PortalErrorCode.NotFound, "Import target group not found.");
            return InvocationJournal.Native("ImportFromDocuments.exactReadback", () => EngineeringAuditLogic.ExactNamesPresent(LastImportedDocumentBlocks,
                EngineeringGroupOperations.Items(group.Blocks).Cast<PlcBlock>().Select(b => b.Name)));
        }

        public ResponseMessage ReadPlcBlockScopes(string softwarePath, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadPlcBlockScopes", meta => {
                if (offset < 0 || limit < 1 || limit > 1000) throw new ArgumentException("offset >= 0; limit 1..1000.");
                var plc = ExactPlcForEngineering(softwarePath, false);
                var rows = new List<JsonNode>();
                foreach (var scope in PlcScopes(plc))
                    foreach (var block in ScopedObjects(BlockRootOf(plc, scope.Unit), "Blocks"))
                    {
                        var typed = (PlcBlock)block.Value;
                        rows.Add(new JsonObject { ["unitName"] = scope.Name, ["unitKind"] = scope.Kind, ["blockPath"] = block.Path,
                            ["name"] = typed.Name, ["programmingLanguage"] = typed.ProgrammingLanguage.ToString(), ["isConsistent"] = typed.IsConsistent });
                        if (rows.Count > 100000) throw new InvalidOperationException("Block inventory exceeds 100000 objects; incomplete inventory refused.");
                    }
                Page(rows.ToArray(), offset, limit, meta);
                meta["scopeEnumerationComplete"] = true;
                return "Root, software-unit and safety-unit block inventory read; paginated results preserve scope identity.";
            });

        public ResponseMessage ManagePlcBlockDocuments(string softwarePath, string action, string name = "", string groupPath = "",
            string unitName = "", string unitKind = "unit", string directoryPath = "", string importOption = "Override", bool dryRun = true, bool verifyDocumentReadback = false)
            => RunHmiStepTool("ManagePlcBlockDocuments", meta => {
                ValidateUnitKind(unitKind);
                if (!new[] { "list", "read", "export", "import" }.Contains(action)) throw new ArgumentException("action: list/read/export/import.");
                if (action != "list" && (EngineeringGroupOperations.Parts(name).Length != 1 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new ArgumentException("name must be one exact block/document basename.");
                bool writing = action == "import" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                var unit = OptionalUnit(plc, unitName, unitKind);
                var group = (PlcBlockGroup)EngineeringGroupOperations.Group(BlockRootOf(plc, unit), groupPath);
                meta["unitName"] = unitName; meta["unitKind"] = unitName == "" ? "root" : unitKind; meta["groupPath"] = groupPath;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action == "list")
                {
                    meta["blocks"] = new JsonArray(EngineeringGroupOperations.Items(group.Blocks).Cast<PlcBlock>().Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["isConsistent"] = b.IsConsistent }).ToArray());
                    return "Exact group blocks listed.";
                }
                var block = group.Blocks.Find(name);
                if (action != "import" && block == null) throw new PortalException(PortalErrorCode.NotFound, "Exact block not found: " + name);
                if (action == "read") { meta["block"] = EngineeringScalarProperties.Read(block!); return "Exact block properties read."; }
                if (!Path.IsPathRooted(directoryPath) || !Directory.Exists(directoryPath)) throw new ArgumentException("directoryPath must be an existing absolute directory on the server.");
                var directory = new DirectoryInfo(directoryPath);
                if (action == "export")
                {
                    if (directory.GetFiles(name + ".*").Length != 0) throw new IOException("Export basename already exists; choose a fresh directory/name.");
                    if (dryRun) return "Export preview; no files written.";
                    meta["mayHaveWrittenFiles"] = true;
                    var exported = InvocationJournal.Native("ScopedBlock.ExportAsDocuments", () => block!.ExportAsDocuments(directory, name));
                    meta["result"] = DocumentExportRow(exported);
                    if (exported.State != DocumentResultState.Success) throw new PortalException(PortalErrorCode.ExportFailed, "Document export did not return Success.");
                    return "Exact scoped block exported as SIMATIC SD; format limitations apply.";
                }
                var declaration = new FileInfo(Path.Combine(directoryPath, name + ".s7dcl"));
                if (!declaration.Exists) throw new FileNotFoundException("SIMATIC SD declaration missing.", declaration.FullName);
                meta["formatPreflight"] = EngineeringAuditLogic.DocumentPreflight(File.ReadAllText(declaration.FullName), Engineering.TiaMajorVersion, null);
                var option = (ImportDocumentOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportDocumentOptions))!;
                if (dryRun) return "Exact scope import preview; review formatPreflight before applying.";
                meta["mayHaveChanged"] = true;
                var result = InvocationJournal.Native("ScopedBlock.ImportFromDocuments", () => group.Blocks.ImportFromDocuments(directory, name, option));
                meta["result"] = DocumentImportRow(result, Names(result.ImportedPlcBlocks));
                if (result.State != DocumentResultState.Success) throw new PortalException(PortalErrorCode.ImportFailed, "Document import did not return Success; may have changed, inspect the native messages before retrying.");
                var imported = EngineeringGroupOperations.Items(result.ImportedPlcBlocks).Cast<PlcBlock>().Select(b => b.Name).ToArray();
                meta["existsVerified"] = EngineeringAuditLogic.ExactNamesPresent(imported, EngineeringGroupOperations.Items(group.Blocks).Cast<PlcBlock>().Select(b => b.Name));
                if (meta["existsVerified"]!.GetValue<bool>() != true) throw new InvalidOperationException("Import returned but exact target readback failed.");
                meta["contentVerified"] = null;
                if (verifyDocumentReadback)
                {
                    if (imported.Length != 1) throw new InvalidOperationException("Import succeeded; automatic document comparison supports one native imported block only. Inspect all returned names.");
                    var readbackDir = Directory.CreateDirectory(Path.Combine(directoryPath, "tia-readback-" + Guid.NewGuid().ToString("N")));
                    meta["mayHaveWrittenFiles"] = true; meta["readbackDirectory"] = readbackDir.FullName;
                    var readback = InvocationJournal.Native("ScopedBlock.verifyExport", () => group.Blocks.Find(imported[0]).ExportAsDocuments(readbackDir, name));
                    if (readback.State != DocumentResultState.Success) throw new InvalidOperationException("Import succeeded, but verification export failed.");
                    var comparisons = new JsonArray(); bool equal = true;
                    foreach (var extension in new[] { ".s7dcl", ".s7res" })
                    {
                        string input = Path.Combine(directoryPath, name + extension), output = Path.Combine(readbackDir.FullName, name + extension);
                        bool match = File.Exists(input) == File.Exists(output) && (!File.Exists(input) || NormalizeDocument(File.ReadAllText(input)) == NormalizeDocument(File.ReadAllText(output)));
                        comparisons.Add(new JsonObject { ["extension"] = extension, ["matches"] = match }); equal &= match;
                    }
                    meta["documentComparison"] = comparisons; meta["sdDocumentsMatch"] = equal;
                    meta["comparisonScope"] = "Exact text after line-ending normalization; excludes properties not represented by SIMATIC SD. Readback files retained for review.";
                }
                return "Block documents imported into the exact root/unit group; existence verified; project not saved or compiled.";
            });
        private static string NormalizeDocument(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
