using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcpServer.ModelContextProtocol;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Plc.PlcB5Scalar;
using NativeResultState = TiaMcp.Adapters.Native.Plc.PlcB5Result;
using NativeFileOutput = TiaMcp.Adapters.Native.Plc.PlcSourceFileOutput;
using ResponseMeta = TiaMcp.Adapters.Native.Plc.PlcB5Meta;
using PortalException = TiaMcp.Adapters.Contracts.PlcSoftwareException;

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
        public PlcB5Reply ReadPlcBlockScopes(string softwarePath, int offset = 0, int limit = 100)
            => RunTool("EngineeringAuditService.ReadPlcBlockScopes", meta => {
                if (offset < 0 || limit < 1 || limit > 1000) throw new ArgumentException("offset >= 0; limit 1..1000.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var rows = new List<object>();
                foreach (var scope in PlcScopes(plc))
                    foreach (var block in ScopedObjects(_session.BlockRootOf(plc, scope.Unit), "Blocks"))
                    {
                        var typed = (PlcBlock)block.Value;
                        rows.Add(new PlcB5Object { ["unitName"] = scope.Name, ["unitKind"] = scope.Kind, ["blockPath"] = block.Path,
                            ["name"] = typed.Name, ["programmingLanguage"] = typed.ProgrammingLanguage.ToString(), ["isConsistent"] = typed.IsConsistent });
                        if (rows.Count > 100000) throw new InvalidOperationException("Block inventory exceeds 100000 objects; incomplete inventory refused.");
                    }
                Page(rows.ToArray(), offset, limit, meta);
                meta["scopeEnumerationComplete"] = true;
                return "Root, software-unit and safety-unit block inventory read; paginated results preserve scope identity.";
            });

#if PLC_DOCUMENT_EXPORT
        public PlcB5Reply ManagePlcBlockDocuments(string softwarePath, string action, string name = "", string groupPath = "",
            string unitName = "", string unitKind = "unit", string directoryPath = "", string importOption = "Override", bool dryRun = true, bool verifyDocumentReadback = false)
            => RunTool("ManagePlcBlockDocuments", meta => {
                ValidateUnitKind(unitKind);
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
                    meta["blocks"] = new PlcB5Array(EngineeringGroupOperations.Items(group.Blocks).Cast<PlcBlock>().Select(b => (object)new PlcB5Object { ["name"] = b.Name, ["isConsistent"] = b.IsConsistent }).ToArray());
                    return "Exact group blocks listed.";
                }
                var block = group.Blocks.Find(name);
                if (action != "import" && block == null) throw new PortalException("NotFound", "Exact block not found: " + name);
                if (action == "read") { meta["block"] = EngineeringScalarProperties.Read(block!); return "Exact block properties read."; }
                if (!Path.IsPathRooted(directoryPath) || !Directory.Exists(directoryPath)) throw new ArgumentException("directoryPath must be an existing absolute directory on the server.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directoryPath));
                if (action == "export")
                {
                    if (directory.GetFiles(name + ".*").Length != 0) throw new IOException("Export basename already exists; choose a fresh directory/name.");
                    if (dryRun) return "Export preview; no files written.";
                    meta["mayHaveWrittenFiles"] = true;
                    var exported = InvocationJournal.Native("ScopedBlock.ExportAsDocuments", () => block!.ExportAsDocuments(directory, name));
                    meta["result"] = DocumentExportRow(exported);
                    NativeResultState.Record(meta, exported.State, false, messages: DocumentMessages(exported.Messages));
                    meta["targetFiles"] = new PlcB5Array(new[] { ".s7dcl", ".s7res" }.Select(ext => (object)NativeResultState.FileRow(Path.Combine(directory.FullName, name + ext))).ToArray());
                    if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(exported.State)) throw new PortalException("ExportFailed", "Document export did not return Success.");
                    return "Exact scoped block exported as SIMATIC SD; format limitations apply.";
                }
                var declaration = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.Combine(directoryPath, name + ".s7dcl")));
                if (!declaration.Exists) throw new FileNotFoundException("SIMATIC SD declaration missing.", declaration.FullName);
                meta["formatPreflight"] = DocumentPreflight(declaration.FullName);
                var option = (ImportDocumentOptions)EngineeringScalarProperties.ConvertValue(PlcB5Scalar.Value(importOption), typeof(ImportDocumentOptions))!;
                if (dryRun) return "Exact scope import preview; review formatPreflight before applying.";
                meta["mayHaveChanged"] = true;
                var result = InvocationJournal.Native("ScopedBlock.ImportFromDocuments", () => group.Blocks.ImportFromDocuments(directory, name, option));
                meta["result"] = DocumentImportRow(result, Names(result.ImportedPlcBlocks));
                NativeResultState.Record(meta, result.State, true, messages: DocumentMessages(result.Messages));
                if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State)) throw new PortalException("ImportFailed", "Document import did not return Success; may have changed, inspect the native messages before retrying.");
                var imported = EngineeringGroupOperations.Items(result.ImportedPlcBlocks).Cast<PlcBlock>().Select(b => b.Name).ToArray();
                meta["existsVerified"] = ExactNamesPresent(imported, EngineeringGroupOperations.Items(group.Blocks).Cast<PlcBlock>().Select(b => b.Name));
                if (meta["existsVerified"]!.GetValue<bool>() != true) throw new InvalidOperationException("Import returned but exact target readback failed.");
                meta["contentVerified"] = null;
                if (verifyDocumentReadback)
                {
                    if (imported.Length != 1) throw new InvalidOperationException("Import succeeded; automatic document comparison supports one native imported block only. Inspect all returned names.");
                    var readbackDir = Directory.CreateDirectory(Path.Combine(directoryPath, "tia-readback-" + Guid.NewGuid().ToString("N")));
                    meta["mayHaveWrittenFiles"] = true; meta["readbackDirectory"] = readbackDir.FullName;
                    var readback = InvocationJournal.Native("ScopedBlock.verifyExport", () => group.Blocks.Find(imported[0]).ExportAsDocuments(readbackDir, name));
                    meta["verificationResult"] = DocumentExportRow(readback);
                    if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(readback.State)) throw new InvalidOperationException("Import succeeded, but verification export failed.");
                    var comparisons = new PlcB5Array(); bool equal = true;
                    foreach (var extension in new[] { ".s7dcl", ".s7res" })
                    {
                        string input = Path.Combine(directoryPath, name + extension), output = Path.Combine(readbackDir.FullName, name + extension);
                        bool match = File.Exists(input) == File.Exists(output) && (!File.Exists(input) || NormalizeDocument(File.ReadAllText(input)) == NormalizeDocument(File.ReadAllText(output)));
                        comparisons.Add(new PlcB5Object { ["extension"] = extension, ["matches"] = match }); equal &= match;
                    }
                    meta["documentComparison"] = comparisons; meta["sdDocumentsMatch"] = equal;
                    meta["comparisonScope"] = "Exact text after line-ending normalization; excludes properties not represented by SIMATIC SD. Readback files retained for review.";
                }
                return "Block documents imported into the exact root/unit group; existence verified; project not saved or compiled.";
            });
        private static string NormalizeDocument(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
#endif
    }
}
