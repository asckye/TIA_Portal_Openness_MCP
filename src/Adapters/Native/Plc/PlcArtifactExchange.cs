using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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

        private string PrepareXmlForImport(string path)
        {
            try
            {
                var bytes = TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () => File.ReadAllBytes(path));
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () => File.ReadAllText(path, Encoding.UTF8));

                var fixedText = text;
                int major = int.Parse(_session.ReleaseKey == "14sp1" ? "14" : _session.ReleaseKey == "15.1" ? "15" : _session.ReleaseKey);
                if (major > 0)
                {
                    fixedText = Regex.Replace(text,
                        "<Engineering\\s+version=\"V\\d+\"\\s*/>",
                        $"<Engineering version=\"V{major}\" />");
                }

                // Already correct: version matches (or unknown) AND a BOM is present -> import as-is.
                if (fixedText == text && hasBom) return path;

                var tmp = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, fixedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return tmp;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch
            {
 /* swallow(parse-fallback): If XML preparation fails, preserve the original path so native import reports the input error. */                return path; // best effort; on any failure import the original file
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
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
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(importPath)));

                MutationStarted = true;
                var imported = PlcBlockPrimitives.Import(PlcBlockPrimitives.Blocks(group), fileInfo, ImportOptions.Override);
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
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ImportFailed", $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                throw pex;
            }
        }

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

        public bool ImportType(string softwarePath, string groupPath, string importPath)
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
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(importPath)));

                MutationStarted = true;
                var imported = PlcBlockPrimitives.Types(group).Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PlcSoftwareException("ImportFailed", "Types.Import returned an empty collection");

                return true;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ImportFailed", "Import failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                throw pex;
            }
        }

        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) throw new PlcSoftwareException("NotFound", $"PlcSoftware not found at '{softwarePath}'" + _session.AvailablePlcPathsSuffix());

            try
            {
                object root = TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                // TagTables collection lives on group
                var tables = TryGetPropertyValue(group, "TagTables") ?? TryGetPropertyValue(root, "TagTables");
                if (tables == null)
                    throw new PlcSoftwareException("NotFound", $"TagTables collection not found. plcType={plc.GetType().FullName} groupType={group.GetType().FullName}");

                // Route through PrepareXmlForImport so the hardcoded <Engineering version="V21"/>
                // header is rewritten to the connected portal version (and a UTF-8 BOM is ensured).
                // Without this, tag-table imports fail on a V20 portal with
                // "The engineering version 'V21' ... is not supported." (block/type imports already
                // sanitize via PrepareXmlForImport; tag tables previously skipped it).
                if (TryImportEngineeringObjectIntoCollection(tables, PrepareXmlForImport(importPath), out _, out var err)) return;
                throw new PlcSoftwareException("ImportFailed", err ?? "ImportPlcTagTable failed");
            }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed", ex.Message, null, ex);
            }
        }

        private bool TryImportEngineeringObjectIntoCollection(object collection, string importPath, out string? importedName, out string? error)
        {
            importedName = null;
            error = null;

            try
            {
                var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                if (!fi.Exists)
                {
                    error = "File not found";
                    return false;
                }

                var t = collection.GetType();

                // Prefer Import(FileInfo, ImportOptions)
                var m2 = t.GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (m2 != null)
                {
                    MutationStarted = true;
                    var list = m2.Invoke(collection, new object[] { fi, ImportOptions.Override });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                // Import(FileInfo)
                var m1 = t.GetMethod("Import", new[] { typeof(FileInfo) });
                if (m1 != null)
                {
                    MutationStarted = true;
                    var list = m1.Invoke(collection, new object[] { fi });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                error = $"No Import method found on collection type {t.FullName}";
                return false;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                error = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }



        private static object? TryResolveChildGroupByPath(object rootGroup, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return rootGroup;

            var parts = groupPath.Trim().Trim('/').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            object? current = rootGroup;
            foreach (var part in parts)
            {
                if (current == null) return null;

                // common group collections used by HMI objects
                var next = TryFindByNameInCollection(current, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                if (next == null)
                {
                    // Some shapes: current.ScreenGroups or current.Groups are nested under another property
                    var groupContainer = TryGetPropertyValue(current, "Groups", "ScreenGroups", "TagTableGroups");
                    if (groupContainer != null)
                    {
                        next = TryFindByNameInCollection(groupContainer, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                    }
                }

                current = next;
            }

            return current;
        }        internal static string? BestEffortExtractFirstName(object? importReturnValue)
        {
            try
            {
                if (importReturnValue is IEnumerable enumerable)
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

        private static object? TryFindByNameInCollection(object root, string[] propertyHints, string wantedName)
        {
            try
            {
                var rootType = root.GetType();
                foreach (var propName in propertyHints)
                {
                    object? collection = null;

                    var prop = rootType.GetProperty(propName);
                    if (prop != null)
                    {
                        collection = prop.GetValue(root);
                    }
                    else if (propName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        var folder = rootType.GetProperty(propName)?.GetValue(root);
                        if (folder != null)
                        {
                            collection = folder.GetType().GetProperty(propName.Replace("Folder", "s"))?.GetValue(folder);
                        }
                    }

                    if (collection is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                            if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
                            {
                                return item;
                            }
                        }
                    }
                }
            }
            catch /* swallow(enumerate-optional): an unavailable candidate collection yields no matching library object */
            {
                // ignore
            }

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
                if (softwareContainer?.Software is not PlcSoftware)
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

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name))
                    {
                        continue;
                    }

                    try
                    {
                        if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(file)).Exists)
                        {
                            failed.Add(new PlcSeedImportFailure { Path = file, Error = "File not found" });
                            continue;
                        }
                        var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(file)));

                        // Let Openness check the XML object's identity atomically. A filename
                        // lookup cannot enforce overwrite=false (and may miss renamed files).
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
                    catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
                    {
                        failed.Add(new PlcSeedImportFailure { Path = file, Error = ex.ToString() });
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
