using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static string ResolveExportFile(string exportPath, string name, string groupPath, bool preservePath)
        {
            if (!preservePath && exportPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(exportPath))
                return exportPath;
            return preservePath ? Path.Combine(exportPath, groupPath.Replace('/', '\\'), name + ".xml") : Path.Combine(exportPath, name + ".xml");
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {

            _session.RecordExportPath(null);

            try
            {
                if (_session.IsProjectNull())
                {
                    throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
                }

                var block = _session.GetBlock(softwarePath, blockPath) ?? throw new PlcSoftwareException("NotFound", "Block not found: '" + blockPath + "'");

                var blockGroupPath = block.Parent is PlcBlockGroup parentGroup ? _session.GetPlcBlockGroupPath(parentGroup) : "";
                exportPath = ResolveExportFile(exportPath, block.Name, blockGroupPath, preservePath);

                // TIA Portal never exports inconsistent blocks
                TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks", block.IsConsistent ? Array.Empty<string>() : new[] { blockPath }, "blockPath");

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                PlcBlockPrimitives.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), ExportOptions.None);
                _session.RecordExportPath(exportPath);

                return block;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                //If the exception is already a PlcSoftwareException, use it; otherwise, wrap it in a new PlcSoftwareException
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ExportFailed", "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                throw pex;
            }
        }

        // A caller's SimaticML document is imported as written (API audit 2026-10-19 F6): its Engineering version is never
        // rewritten. A header outside the documented window of this release is refused before any native call. Only a missing
        // UTF-8 BOM is added, on a temporary copy with identical content.
        private sealed class PreparedImport
        {
            internal string Path = "";
            internal string Kind = "";
            internal string Name = "";
        }

        private PreparedImport PrepareXmlForImport(string path, string parameter = "importPath")
        {
            var bytes = TiaOpenness.Shared.NativeInputPolicy.Read(parameter, () => File.ReadAllBytes(path));
            SimaticMlImportWindow.Require(_session.ReleaseKey, bytes, parameter);
            var native = SimaticMlImportWindow.ReadObject(SimaticMlImportWindow.Load(bytes))
                ?? throw new AdapterPreconditionException("The import file holds no SW.Blocks, SW.Types or SW.Tags object with a Name.", parameter);
            var prepared = new PreparedImport { Path = path, Kind = native.Kind, Name = native.Name };
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (hasBom) return prepared;
            var tmp = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, TiaOpenness.Shared.NativeInputPolicy.Read(parameter, () => File.ReadAllText(path, Encoding.UTF8)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            prepared.Path = tmp;
            return prepared;
        }

        // overwrite=false (the default of every tool): an existing object of the same name gives a typed refusal before the native
        // import, which then runs with ImportOptions.None ("Throw if exists"). overwrite=true uses ImportOptions.Override; an existing
        // object in another group is refused because Siemens documents that such an import is cancelled ("Importing configuration data").
        private static void RequireImportTarget(string noun, string name, IEnumerable<(object Parent, string Name)> existing, object destination, bool overwrite)
        {
            var hits = existing.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hits.Length == 0) return;
            if (!overwrite)
                throw new AdapterPreconditionException("The PLC " + noun + " '" + name + "' already exists and overwrite=false; nothing was imported. Pass overwrite=true to replace it (ImportOptions.Override).", "overwrite", false);
            if (hits.Any(h => !ReferenceEquals(h.Parent, destination) && !object.Equals(h.Parent, destination)))
                throw new AdapterPreconditionException("The PLC " + noun + " '" + name + "' exists in another group; TIA cancels an Override import into a different group. Nothing was imported.", "groupPath", false);
        }

        private static IEnumerable<(object Parent, string Name)> ExistingTypes(PlcTypeGroup group, int depth = 0)
        {
            if (depth > 64) throw new InvalidOperationException("PLC type group depth exceeds 64.");
            foreach (var type in group.Types) yield return (group, type.Name);
            foreach (var child in group.Groups) foreach (var item in ExistingTypes(child, depth + 1)) yield return item;
        }

        private static IEnumerable<(object Parent, string Name)> ExistingTagTables(PlcTagTableGroup group, int depth = 0)
        {
            if (depth > 64) throw new InvalidOperationException("PLC tag table group depth exceeds 64.");
            foreach (var table in group.TagTables) yield return (group, table.Name);
            foreach (var child in group.Groups) foreach (var item in ExistingTagTables(child, depth + 1)) yield return item;
        }

        private IEnumerable<(object Parent, string Name)> ExistingBlocks(PlcSoftware plc)
        {
            var all = new List<PlcBlock>();
            _session.GetBlocksRecursive(plc.BlockGroup, all);
            return all.Select(b => ((object)b.Parent, b.Name));
        }

        private static void RequireKind(PreparedImport prepared, bool block, bool type, string parameter)
        {
            bool isType = prepared.Kind == "PlcStruct", isTable = prepared.Kind == "PlcTagTable";
            if (block ? isType || isTable : type ? !isType : !isTable)
                throw new AdapterPreconditionException("The import file holds a " + prepared.Kind + ", not a " + (block ? "block" : type ? "PLC data type" : "PLC tag table") + ".", parameter);
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath, bool overwrite)
        {


            try
            {
                if (_session.IsProjectNull())
                    throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

                var softwareContainer = _session.GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PlcSoftwareException("NotFound",
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = _session.GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PlcSoftwareException("NotFound",
                        $"PLC block group not found for groupPath='{groupPath}'; use empty string for root program blocks");

                if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)).Exists)
                    throw new PlcSoftwareException("InvalidParams", $"Import file not found: {importPath}");
                var prepared = PrepareXmlForImport(importPath);
                RequireKind(prepared, true, false, "importPath");
                RequireImportTarget("block", prepared.Name, ExistingBlocks(plcSoftware), group, overwrite);
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(prepared.Path));

                MutationStarted = true;
                var imported = PlcBlockPrimitives.Import(PlcBlockPrimitives.Blocks(group), fileInfo, overwrite ? ImportOptions.Override : ImportOptions.None);
                if (imported == null || imported.Count == 0)
                    throw new PlcSoftwareException("ImportFailed", "Blocks.Import returned an empty collection");

                return true;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                // Surface the real Openness error to callers — without this the message
                // is just "Import failed" which is useless for diagnosing bad LAD/SCL XML.
                var inner = UnwrapImportError(ex);
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ImportFailed", $"Import failed: {inner}{ImportOptionsNote}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                throw pex;
            }
        }

        // API audit F14: the documented import options this path does not pass (SWImportOptions on 15.1+, the culture
        // ImportOptions on 19+) are named beside a native refusal; the refusal itself is not reinterpreted.
        internal const string ImportOptionsNote = " TIA refused the import natively. This tool imports with ImportOptions.None or Override only;"
            + " it does not pass SWImportOptions (IgnoreStructuralChanges, IgnoreMissingReferencedObjects, IgnoreUnitAttributes) or the"
            + " culture options, so a file exported from a software unit, one that references objects not yet in the project, or one with"
            + " inactive cultures is refused by TIA.";

        private static string UnwrapImportError(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return string.Join(" | ", parts);
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath, bool overwrite)
        {


            try
            {
                if (_session.IsProjectNull())
                    throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

                var softwareContainer = _session.GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PlcSoftwareException("NotFound",
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = _session.GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PlcSoftwareException("NotFound",
                        $"PLC type group not found for groupPath='{groupPath}'; use empty string for root PLC data types");

                if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)).Exists)
                    throw new PlcSoftwareException("InvalidParams", $"Import file not found: {importPath}");
                var prepared = PrepareXmlForImport(importPath);
                RequireKind(prepared, false, true, "importPath");
                RequireImportTarget("data type", prepared.Name, ExistingTypes(plcSoftware.TypeGroup), group, overwrite);
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(prepared.Path));

                MutationStarted = true;
                var imported = PlcBlockPrimitives.Types(group).Import(fileInfo, overwrite ? ImportOptions.Override : ImportOptions.None);
                if (imported == null || imported.Count == 0)
                    throw new PlcSoftwareException("ImportFailed", "Types.Import returned an empty collection");

                return true;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ImportFailed", "Import failed: " + UnwrapImportError(ex) + ImportOptionsNote, null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                throw pex;
            }
        }

        // API audit F4: the destination group is resolved exactly through the typed PlcTagTableSystemGroup/PlcTagTableUserGroup
        // Groups.Find path; a folderPath that does not resolve is refused, never redirected to the root group.
        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath, bool overwrite)
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) throw new PlcSoftwareException("NotFound", $"PlcSoftware not found at '{softwarePath}'" + _session.AvailablePlcPathsSuffix());

            try
            {
                PlcTagTableGroup group = plc.TagTableGroup;
                foreach (var part in PlcGroupOperations.Parts(folderPath, true))
                    group = group.Groups.Find(part) ?? throw new PlcSoftwareException("NotFound",
                        $"PLC tag table group not found for folderPath='{folderPath}' (segment '{part}'); use empty string for the root tag table group. Nothing was imported.");

                if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)).Exists)
                    throw new PlcSoftwareException("InvalidParams", $"Import file not found: {importPath}");
                var prepared = PrepareXmlForImport(importPath);
                RequireKind(prepared, false, false, "importPath");
                RequireImportTarget("tag table", prepared.Name, ExistingTagTables(plc.TagTableGroup), group, overwrite);

                MutationStarted = true;
                var imported = PlcOrganisationPrimitives.TagTables(group).Import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(prepared.Path)), overwrite ? ImportOptions.Override : ImportOptions.None);
                if (imported == null || imported.Count == 0)
                    throw new PlcSoftwareException("ImportFailed", "TagTables.Import returned an empty collection");
            }
            catch (AdapterPreconditionException) { throw; }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed", ex.GetBaseException().GetType().FullName + ": " + ex.GetBaseException().Message + ImportOptionsNote, null, ex);
            }
        }

        internal static string? BestEffortExtractFirstName(object? importReturnValue)
        {
            try
            {
                if (importReturnValue is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch /* swallow(enumerate-optional): an unavailable import result leaves the optional first-name hint unset */ { }
            return null;
        }

        public PlcSeedHmiReply ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true)
        {
            if (!_session.IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                _session.PlcSoftwarePath(_session.ResolvePlc(softwarePath, false)), true);

            var imported = new List<string>();
            var failed = new List<PlcSeedImportFailure>();

            try
            {
                if (_session.IsProjectNull())
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = "Project is null" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = "Directory not found" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed };
                }

                var softwareContainer = _session.GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = $"PlcSoftware not found at '{softwarePath}'" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed };
                }

                var group = _session.GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = $"Block group not found (groupPath='{groupPath}')" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                // Every file is checked (version window, object kind, overwrite=false collisions) before the first native import.
                var planned = new List<(string File, PreparedImport Prepared)>();
                var existing = ExistingBlocks(plcSoftware).ToArray();
                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (regex != null && !regex.IsMatch(Path.GetFileNameWithoutExtension(file))) continue;
                    var prepared = PrepareXmlForImport(file, "dir");
                    RequireKind(prepared, true, false, "dir");
                    RequireImportTarget("block", prepared.Name, existing, group, overwrite);
                    planned.Add((file, prepared));
                }

                foreach (var item in planned.OrderBy(p => PlcImportOrder.BlockRank(p.Prepared.Kind)).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(item.File);
                    try
                    {
                        var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(item.Prepared.Path));
                        MutationStarted = true;
                        var list = PlcBlockPrimitives.Import(group.Blocks, fi, overwrite ? ImportOptions.Override : ImportOptions.None);
                        if (list != null && list.Count > 0)
                        {
                            imported.AddRange(list.Select(b => b?.Name).Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>());
                        }
                        else
                        {
                            imported.Add(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new PlcSeedImportFailure { Path = item.File, Error = ex.ToString() });
                    }
                }

                return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                failed.Add(new PlcSeedImportFailure { Path = dir, Error = ex.ToString() });
                return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
        }
    }
}
