using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public PlcSeedHmiReply ImportTechnologyObjectsFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            if (!IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                _session.PlcSoftwarePath(_session.GetPlcSoftware(softwarePath)), true);

            var imported = new List<string>();
            var failed = new List<PlcSeedImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = "Project is null" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed, Meta = BatchRejection("PROJECT_NOT_BOUND") };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new PlcSeedImportFailure { Path = dir, Error = "Directory not found" });
                    return new PlcSeedHmiReply { Imported = imported, Failed = failed, Meta = BatchRejection("NOT_FOUND") };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                var selected = Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal).ToList();
                foreach (var file in selected)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    // Preserve native returned identities, including those read before an enumeration failure.
                    try { ImportTechnologyObject(softwarePath, folderPath, file, overwrite, imported); }
                    catch (PlcSoftwareException pex)
                    {
                        failed.Add(new PlcSeedImportFailure { Path = file, Error = pex.Message + " Batch stopped; later files were not attempted." });
                        break;
                    }
                }

                return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new PlcSeedImportFailure { Path = dir, Error = ex.ToString() });
                return new PlcSeedHmiReply { Imported = imported, Failed = failed };
            }
        }

        public PlcSeedHmiReply ExportTechnologyObjectsToDirectory(
            string softwarePath, string exportDir, string regexName = "")
        {
            if (!IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                _session.PlcSoftwarePath(_session.GetPlcSoftware(softwarePath)), true);

            var exported = new List<string>();
            var failed = new List<PlcSeedImportFailure>();

            if (IsProjectNull())
            {
                failed.Add(new PlcSeedImportFailure { Path = softwarePath, Error = "No project open." });
                return new PlcSeedHmiReply { Imported = exported, Failed = failed, Meta = BatchRejection("PROJECT_NOT_BOUND") };
            }

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                failed.Add(new PlcSeedImportFailure { Path = softwarePath, Error = "PLC software not found." + _session.AvailablePlcPathsSuffix() });
                return new PlcSeedHmiReply { Imported = exported, Failed = failed, Meta = BatchRejection("NOT_FOUND") };
            }

            try
            {
                Directory.CreateDirectory(exportDir);
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);

                foreach (var (item, folder) in EnumerateTechnologyObjectsRecursive(plc))
                {
                    var name = WatchNative.Name(item) ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (regex != null && !regex.IsMatch(name)) continue;

                    // TOs of a user folder land in a sub directory of the same name (ImportTechnologyObjectsFromDirectory takes folderPath)
                    var directory = folder.Length == 0 ? exportDir : Path.Combine(exportDir, folder.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, name + ".xml");
                    var evidence = TechnologyExport(item, path);
                    var err = Equals(evidence["success"], true) ? null : _session.SerializeEvidence(evidence);
                    var label = folder.Length == 0 ? name : folder + "/" + name;
                    if (err == null) exported.Add(label);
                    else failed.Add(new PlcSeedImportFailure { Path = label, Error = err });
                }
            }
            catch (Exception ex)
            {
                failed.Add(new PlcSeedImportFailure { Path = exportDir, Error = ex.ToString() });
            }

            return new PlcSeedHmiReply { Imported = exported, Failed = failed };
        }

        private static List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB To, string Folder)> EnumerateTechnologyObjectsRecursive(PlcSoftware plc)
        {
            var list = new List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB, string)>();
            void Walk(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup group, string folder)
            {
                foreach (var to in PlcGroupOperations.Items(WatchNative.Objects(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>())
                    list.Add((to, folder));
#if PLC_TECH_GROUP_READ
                foreach (var sub in PlcGroupOperations.Items(WatchNative.Groups(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup>())
                    Walk(sub, folder.Length == 0 ? WatchNative.Name(sub) : folder + "/" + WatchNative.Name(sub));
#endif
            }
            Walk(WatchNative.TechnologyGroup(plc), "");
            return list;
        }

        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath, bool overwrite, List<string> importedNames)
        {
            if (IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) throw new PlcSoftwareException("NotFound", $"PlcSoftware not found at '{softwarePath}'" + _session.AvailablePlcPathsSuffix());

            try
            {
                // Use the typed TechnologicalObjectGroup property.
                // Native observation: the misspelled reflective lookup fell through to the PLC with "TechnologyObjects collection not found".
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                var group = (global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup)PlcGroupOperations.Group(plc.TechnologicalObjectGroup, folderPath ?? "");
                var col = group.TechnologicalObjects;
                if (TryImportEngineeringObjectIntoCollection(col, importPath, overwrite, importedNames, out var err)) return;
                throw new PlcSoftwareException("ImportFailed", err ?? "ImportTechnologyObject failed");
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

        private bool TryImportEngineeringObjectIntoCollection(object collection, string importPath, bool overwrite, List<string> importedNames, out string? error)
        {
            error = null;
            var nativeEntered = false;
            var firstNameIndex = importedNames.Count;
            try
            {
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                if (!file.Exists)
                {
                    error = "File not found";
                    return false;
                }

                var method = collection.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (method == null)
                {
                    error = "Import(FileInfo, ImportOptions) is unavailable; no import was attempted.";
                    return false;
                }

                // Never fall back to an option-less overload, including after native entry.
                nativeEntered = true; MutationStarted = true;
                var result = method.Invoke(collection, new object[] { file, overwrite ? ImportOptions.Override : ImportOptions.None });
                if (result is not IEnumerable items || result is string)
                    throw new InvalidOperationException("Import returned no enumerable object identities.");
                foreach (var item in items)
                {
                    var name = item?.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException("An imported object identity could not be read.");
                    importedNames.Add(name);
                }
                if (importedNames.Count == firstNameIndex)
                    throw new InvalidOperationException("Import returned no object identities.");
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                error = cause.GetType().FullName + ": " + cause.Message;
                if (nativeEntered)
                {
                    error += " Import was entered; the project may have changed. No retry was attempted.";
                    if (importedNames.Count > firstNameIndex)
                        error += " Returned object identities read before failure: " + string.Join(", ", importedNames.Skip(firstNameIndex)) + ".";
                }
                return false;
            }
        }


        private Dictionary<string, object?> TechnologyExport(object target, string path, bool overwrite = true)
            => EngineeringFileExport.Export(target, path, overwrite, _session.RecordExportFailure);

        private static Dictionary<string, object?> BatchRejection(string code) => LegacyResponseMetadata.Basic(null, false, code);

    }
}
