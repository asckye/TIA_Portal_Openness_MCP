using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        // A caller's SimaticML document is imported as written (API audit 2026-10-19 F6): its Engineering version is never
        // rewritten. A header outside the documented window of this release is refused before any native call. Only a missing
        // UTF-8 BOM is added. Blocks use private copies so a directory import consumes exactly the bytes admitted during planning.
        private sealed class PreparedImport : IDisposable
        {
            internal string Path = "";
            internal string Kind = "";
            internal string Name = "";
            internal bool Temporary;
            internal string? SingleTag;
            internal byte[]? SourceHash;
            internal byte[] HashFile()
            {
                using var sha = SHA256.Create();
                using var file = File.OpenRead(Path);
                return sha.ComputeHash(file);
            }
            public void Dispose()
            {
                if (!Temporary) return;
                try { File.Delete(Path); }
                catch (IOException) { /* swallow(cleanup): a locked snapshot does not change the native import verdict. */ }
                catch (UnauthorizedAccessException) { /* swallow(cleanup): preserve the original operation outcome. */ }
            }
        }

        private PreparedImport PrepareXmlForImport(string path, string parameter = "importPath")
        {
            var bytes = TiaOpenness.Shared.NativeInputPolicy.Read(parameter, () => File.ReadAllBytes(path));
            SimaticMlImportWindow.Require(_session.ReleaseKey, bytes, parameter);
            var document = SimaticMlImportWindow.Load(bytes);
            SimaticMlImportWindow.RequireBlockProgram(_session.ReleaseKey, document, parameter);
            var native = SimaticMlImportWindow.ReadObject(document)
                ?? throw new AdapterPreconditionException("The import file holds no SW.Blocks, SW.Types or SW.Tags object with a Name.", parameter);
            var prepared = new PreparedImport { Path = path, Kind = native.Kind, Name = native.Name,
                SingleTag = native.Kind == "PlcTagTable" ? SimaticMlImportWindow.SingleImportName(document, "SW.Tags.PlcTagTable") : null };
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            bool block = document.Root!.Elements().Any(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            if (hasBom && !block)
            {
                if (prepared.SingleTag != null)
                {
                    using var sha = SHA256.Create();
                    prepared.SourceHash = sha.ComputeHash(bytes);
                }
                return prepared;
            }
            prepared.Path = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory,
                "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                using var output = new FileStream(prepared.Path, FileMode.CreateNew, FileAccess.Write);
                prepared.Temporary = true;
                if (hasBom) output.Write(bytes, 0, bytes.Length);
                else
                {
                    using var data = new MemoryStream(bytes, false);
                    using var text = new StreamReader(data, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    writer.Write(text.ReadToEnd());
                }
            }
            catch { prepared.Dispose(); throw; }
            return prepared;
        }

        // overwrite=false (the default of every tool): an existing object of the same name gives a typed refusal before the native
        // import, which then runs with ImportOptions.None ("Throw if exists"). overwrite=true uses ImportOptions.Override; an existing
        // object in another group is refused because Siemens documents that such an import is cancelled ("Importing configuration data").
        private static void RequireImportTarget<T>(string noun, string name, IEnumerable<(T Parent, string Name)> existing, object destination,
                bool overwrite) where T : class?
        {
            var hits = existing.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hits.Length == 0) return;
            if (!overwrite)
                throw new AdapterPreconditionException("The PLC " + noun + " '" + name
                        + "' already exists and overwrite=false; nothing was imported. Pass overwrite=true to replace it (ImportOptions.Override).",
                        "overwrite", false);
            if (hits.Any(h => !ReferenceEquals(h.Parent, destination) && !object.Equals(h.Parent, destination)))
                throw new AdapterPreconditionException("The PLC " + noun + " '" + name
                        + "' exists in another group; TIA cancels an Override import into a different group. Nothing was imported.", "groupPath", false);
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

        private IEnumerable<(object? Parent, string Name)> ExistingBlocks(PlcSoftware plc)
        {
            var all = new List<PlcBlock>();
            _session.GetBlocksRecursive(plc.BlockGroup, all);
            return all.Select(b => ((object?)b.Parent, b.Name));
        }

        private static void RequireKind(PreparedImport prepared, bool block, bool type, string parameter)
        {
            bool isType = prepared.Kind == "PlcStruct", isTable = prepared.Kind == "PlcTagTable";
            if (block ? isType || isTable : type ? !isType : !isTable)
                throw new AdapterPreconditionException("The import file holds a " + prepared.Kind + ", not a "
                        + (block ? "block" : type ? "PLC data type" : "PLC tag table") + ".", parameter);
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath, bool overwrite)
        {


            try
            {
                if (_session.IsProjectNull())
                    throw new PlcSoftwareException("InvalidState",
                            "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call " +
                            "OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

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
                using var prepared = PrepareXmlForImport(importPath);
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
                    throw new PlcSoftwareException("InvalidState",
                            "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call " +
                            "OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

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
                using var prepared = PrepareXmlForImport(importPath);
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
                var pex = ex as PlcSoftwareException
                    ?? new PlcSoftwareException("ImportFailed", "Import failed: " + UnwrapImportError(ex) + ImportOptionsNote, null, ex);
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
            if (_session.IsProjectNull())
                throw new PlcSoftwareException("InvalidState",
                    "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call " +
                    "OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

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
                using var prepared = PrepareXmlForImport(importPath);
                RequireKind(prepared, false, false, "importPath");
                RequireImportTarget("tag table", prepared.Name, ExistingTagTables(plc.TagTableGroup), group, overwrite);

                var tables = PlcOrganisationPrimitives.TagTables(group);
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(prepared.Path));
                if (!overwrite && prepared.Temporary && prepared.SingleTag != null)
                {
                    try { prepared.SourceHash = prepared.HashFile(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { /* swallow(probe-optional): import still runs; an unreadable baseline cannot establish an unchanged source. */ }
                }
                bool priorWrite = MutationStarted;
                MutationStarted = true;
                IList<PlcTagTable> imported;
                try { imported = tables.Import(file, overwrite ? ImportOptions.Override : ImportOptions.None); }
                catch (Exception error)
                {
                    // Let Import unwind its own file handles before opening the admitted path for readback.
                    // A single named declaration cannot choose another name. Admission retained no destination count.
                    bool MayContainTarget(string name) => prepared.SourceHash == null || !prepared.SourceHash.SequenceEqual(prepared.HashFile())
                        || tables.Find(name) != null || tables.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (!overwrite && NativeRefusalProbe.ProvesAbsent(error, prepared.SingleTag, MayContainTarget))
                    {
                        MutationStarted = priorWrite;
                        error.Data["verifiedAbsent"] = true;
                    }
                    throw;
                }
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
                var failure = new PlcSoftwareException("ImportFailed", ex.GetBaseException().GetType().FullName + ": " + ex.GetBaseException().Message
                        + ImportOptionsNote, null, ex);
                if (ex.Data["verifiedAbsent"] is true) failure.Data["verifiedAbsent"] = true;
                throw failure;
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
            var snapshots = new List<PreparedImport>();
            string? unconfirmedFile = null;

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
                    snapshots.Add(prepared);
                    RequireKind(prepared, true, false, "dir");
                    RequireImportTarget("block", prepared.Name, existing, group, overwrite);
                    planned.Add((file, prepared));
                }

                foreach (var item in planned.OrderBy(p => PlcImportOrder.BlockRank(p.Prepared.Kind)).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(item.Prepared.Path));
                        MutationStarted = true;
                        var list = PlcBlockPrimitives.Import(group.Blocks, fi, overwrite ? ImportOptions.Override : ImportOptions.None);
                        var names = list?.Select(block => block?.Name).ToArray();
                        if (names == null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
                        {
                            unconfirmedFile = item.File;
                            break;
                        }
                        imported.AddRange(names!);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new PlcSeedImportFailure { Path = item.File, Error = ex.ToString() });
                    }
                }

                if (unconfirmedFile == null) return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                failed.Add(new PlcSeedImportFailure { Path = dir, Error = ex.ToString() });
                return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
            finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
            const string reason = "Block import returned no confirmed object names";
            failed.Add(new PlcSeedImportFailure { Path = unconfirmedFile, Error = reason });
            var failure = new PlcSoftwareException("OpennessError", reason + " for " + unconfirmedFile);
            failure.Data["nativeResultEvidence"] = new Dictionary<string, object?> {
                ["imported"] = imported.ToArray(), ["failed"] = failed.ToArray()
            };
            throw failure;
        }
    }
}
