using TiaMcp.Logic.V4;
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

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class EngineeringAuditService
    {
        private readonly IEngineeringSession _session;

        public EngineeringAuditService(IEngineeringSession session) => _session = session;

        public ResponseMessage ReadPlcBlockScopes(string softwarePath, int offset = 0, int limit = 100)
            => _session.RunHmiStepTool(nameof(EngineeringAuditService.ReadPlcBlockScopes), meta => {
                if (offset < 0 || limit < 1 || limit > 1000) throw new ArgumentException("offset >= 0; limit 1..1000.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var rows = new List<JsonNode>();
                foreach (var scope in _session.PlcScopes(plc))
                    foreach (var block in _session.ScopedObjects(_session.BlockRootOf(plc, scope.Unit), "Blocks"))
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
            => _session.RunHmiStepTool("ManagePlcBlockDocuments", meta => {
                _session.ValidateUnitKind(unitKind);
                if (!new[] { "list", "read", "export", "import" }.Contains(action)) throw new ArgumentException("action: list/read/export/import.");
                if (action != "list" && (EngineeringGroupOperations.Parts(name).Length != 1 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new ArgumentException("name must be one exact block/document basename.");
                bool writing = action == "import" && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                var group = (PlcBlockGroup)EngineeringGroupOperations.Group(_session.BlockRootOf(plc, unit), groupPath);
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
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directoryPath));
                if (action == "export")
                {
                    if (directory.GetFiles(name + ".*").Length != 0) throw new IOException("Export basename already exists; choose a fresh directory/name.");
                    if (dryRun) return "Export preview; no files written.";
                    meta["mayHaveWrittenFiles"] = true;
                    var exported = InvocationJournal.Native("ScopedBlock.ExportAsDocuments", () => block!.ExportAsDocuments(directory, name));
                    meta["result"] = _session.DocumentExportRow(exported);
                    NativeResultState.Record(meta, exported.State, false, messages: _session.DocumentMessages(exported.Messages));
                    meta["targetFiles"] = new JsonArray(new[] { ".s7dcl", ".s7res" }.Select(ext => (JsonNode)NativeResultState.FileRow(Path.Combine(directory.FullName, name + ext))).ToArray());
                    if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(exported.State)) throw new PortalException(PortalErrorCode.ExportFailed, "Document export did not return Success.");
                    return "Exact scoped block exported as SIMATIC SD; format limitations apply.";
                }
                var declaration = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.Combine(directoryPath, name + ".s7dcl")));
                if (!declaration.Exists) throw new FileNotFoundException("SIMATIC SD declaration missing.", declaration.FullName);
                meta["formatPreflight"] = EngineeringAuditLogic.DocumentPreflight(File.ReadAllText(declaration.FullName), Engineering.TiaMajorVersion, null);
                var option = (ImportDocumentOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportDocumentOptions))!;
                if (dryRun) return "Exact scope import preview; review formatPreflight before applying.";
                meta["mayHaveChanged"] = true;
                var result = InvocationJournal.Native("ScopedBlock.ImportFromDocuments", () => group.Blocks.ImportFromDocuments(directory, name, option));
                meta["result"] = _session.DocumentImportRow(result, Names(result.ImportedPlcBlocks));
                NativeResultState.Record(meta, result.State, true, messages: _session.DocumentMessages(result.Messages));
                if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State)) throw new PortalException(PortalErrorCode.ImportFailed, "Document import did not return Success; may have changed, inspect the native messages before retrying.");
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
                    meta["verificationResult"] = _session.DocumentExportRow(readback);
                    if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(readback.State)) throw new InvalidOperationException("Import succeeded, but verification export failed.");
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
