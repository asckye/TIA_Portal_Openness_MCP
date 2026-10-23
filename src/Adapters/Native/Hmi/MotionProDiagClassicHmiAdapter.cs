using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcp.Adapters.Native.Hmi.HmiAccessHelpers;
#if HMI_UNIFIED
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Logic = TiaMcp.Adapters.Native.Hmi.ClassicHmiLogic;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class MotionProDiagClassicHmiAdapter
    {
        private readonly HmiSession _session;

        public MotionProDiagClassicHmiAdapter(HmiSession session) => _session = session;
        private static object? OptionalOfficialService(object owner, string typeName, out string state)
            => TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.ResolveMotionOfficialService(owner, typeName, out state);
        private static object RequireOfficialService(object owner, string typeName)
            => TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.RequireMotionOfficialService(owner, typeName);



        // Typed VBScriptSystemFolder / VBScriptUserFolder navigation (Portal.ClassicHmiFolders.cs).
        private object ClassicScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath) => _session.ExactVbScriptFolder(hmi, folderPath);
        private static HmiObject ClassicRow(object target, string? path = null)
        {
            var row = HmiDynamic.Read(target); row["clrProperties"] = HmiScalar.Read(target); row["name"] = HmiDynamic.Name(target);
            if (path != null) row["path"] = path;
            row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && row["clrProperties"]!.Field("dataComplete")!.GetValue<bool>();
            return row;
        }
        private static HmiObject ClassicPage(HmiObject meta, object[] items, int offset, int limit, Func<object, object> project)
        {
            var rows = items.Skip(offset).Take(limit).Select(project).ToArray();
            meta["records"] = new HmiArray(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length;
            meta["offset"] = offset; meta["limit"] = limit; meta["total"] = items.Length;
            meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null;
            meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Length == items.Length && rows.All(r => r!.Field("dataComplete")?.GetValue<bool>() != false);
            return meta;
        }
        private static string ClassicExport(object target, string filePath, HmiObject meta, bool dryRun, ExportOptions exportOption)
        {
            var file = PlcSourceFileOutput.Plan(filePath);
            var signature = new[] { typeof(FileInfo), typeof(ExportOptions) };
            if (target.GetType().GetMethod("Export", signature) == null) throw new NotSupportedException("Native Export(FileInfo, ExportOptions) unavailable on " + target.GetType().FullName + ".");
            if (dryRun) return "Native export preview; no file written.";
            meta["mayHaveWrittenFiles"] = true;
            PlcGroupOperations.Call(target, "Export", signature, file, exportOption);
            meta["file"] = PlcSourceFileOutput.Verify(file); meta["apiCallSuccess"] = true;
            return "Native XML export written and hashed; content semantics not verified.";
        }
        private static string ClassicImport(object composition, string filePath, string importOptions, bool confirmDelete, HmiObject meta, bool writing, string objectType)
        {
            var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
            if (!file.Exists) throw new FileNotFoundException("Import input not found.");
            var options = (ImportOptions)HmiScalar.ConvertValue(HmiValues.Value(importOptions), typeof(ImportOptions))!;
            Logic.RequireImportConfirmation(options.ToString(), confirmDelete);
            var signature = new[] { typeof(FileInfo), typeof(ImportOptions) };
            if (composition.GetType().GetMethod("Import", signature) == null) throw new NotSupportedException("Native Import(FileInfo, ImportOptions) unavailable on " + composition.GetType().FullName + ".");
            meta["importOptions"] = options.ToString();
            HmiExchangePolicy.BeforeImport(composition, filePath, objectType, options == ImportOptions.Override);
            if (!writing) return "Native import preview; no changes.";
            meta["mayHaveChanged"] = true;
            var result = PlcGroupOperations.Call(composition, "Import", signature, file, options);
            HmiFiles.AttachResult(meta, result);
            meta["importedNames"] = new HmiArray((result as IEnumerable)?.Cast<object>().Select(x => (object)(HmiDynamic.Name(x) ?? "")).ToArray() ?? Array.Empty<object>());
            return "Native import returned; inspect imported names. No save/compile/download.";
        }
        private static string ClassicDelete(object target, object composition, string name, bool confirmDelete, HmiObject meta, bool writing)
        {
            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required for delete.");
            if (target.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete unavailable on " + target.GetType().FullName + ".");
            if (!writing) return "Native deletion preview; no changes.";
            meta["mayHaveChanged"] = true;
            PlcGroupOperations.Call(target, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
            if (PlcGroupOperations.Find(composition, name) != null) throw new InvalidOperationException("Object remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "Object deleted and verified absent; no save/compile/download.";
        }
        private string ClassicSetAttributes(object target, string attributesJson, HmiObject meta, bool writing)
        {
            var prepared = HmiDynamic.Prepare(target, _session.Attributes(attributesJson, true));
            meta["before"] = ClassicRow(target);
            if (!writing) return "Attribute update preview; advertised writable attributes validated, no changes.";
            HmiDynamic.Apply(target, prepared, meta); meta["apiCallSuccess"] = true;
            meta["after"] = ClassicRow(target);
            return "Attributes changed and verified by readback; no save/compile/download.";
        }

        public HmiMessage ReadClassicHmiScripts(string softwarePath, string folderPath = "", int offset = 0, int limit = 100)
            => RunTool("ListClassicHmiScripts", meta => {
                Logic.RequirePagination(offset, limit);
                var hmi = _session.ExactClassicHmi(softwarePath);
                var root = ClassicScriptFolder(hmi, PlcGroupOperations.Parts(folderPath, true));
                var scripts = new List<(string Path, object Script)>(); var folders = new HmiArray();
                void Walk(object folder, string path, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Script folder depth exceeds 64.");
                    foreach (var script in PlcGroupOperations.Items(PlcGroupOperations.Get(folder, "VBScripts")))
                    {
                        if (scripts.Count >= 10000) throw new InvalidOperationException("More than 10000 scripts; narrow folderPath.");
                        scripts.Add((path + (HmiDynamic.Name(script) ?? ""), script));
                    }
                    foreach (var child in PlcGroupOperations.Items(PlcGroupOperations.Get(folder, "Folders")))
                    {
                        var childPath = path + (HmiDynamic.Name(child) ?? "") + "/"; folders.Add(childPath.TrimEnd('/'));
                        Walk(child, childPath, depth + 1);
                    }
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["softwarePath"] = softwarePath; meta["folderPath"] = folderPath; meta["folders"] = folders;
                meta["codeAccess"] = "VB script source is not a typed Openness property; readable attributes are listed per script and the native XML is available via ManageClassicHmiScript export.";
                ClassicPage(meta, scripts.Select(s => s.Script).ToArray(), offset, limit, s => ClassicRow(s, scripts.First(x => ReferenceEquals(x.Script, s)).Path));
                return "Classic HMI VB scripts listed with advertised attributes; live pagination.";
            });

        public HmiMessage ManageClassicHmiScript(string softwarePath, string scriptPath, string action, string filePath = "", string importOptions = "None",
            string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true, string exportOptions = "WithDefaults")
            => RunTool("ManageClassicHmiScript", meta => {
                var exportOption = (ExportOptions)Enum.Parse(typeof(ExportOptions), HmiExchangePolicy.ExportOption(exportOptions));
                meta["exportOptions"] = exportOptions;
                Logic.RequireAction(action, Logic.ScriptActions);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["scriptPath"] = scriptPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action == "import")
                {
                    _session.Attributes(attributesJson, false);
                    var folder = ClassicScriptFolder(hmi, PlcGroupOperations.Parts(scriptPath, true));
                    return ClassicImport(PlcGroupOperations.Get(folder, "VBScripts"), filePath, importOptions, confirmDelete, meta, writing, "Hmi.RuntimeScripting.VBScript");
                }
                var (folderPath, name) = Logic.SplitObjectPath(scriptPath);
                var parent = ClassicScriptFolder(hmi, folderPath);
                if (action == "createFolder" || action == "deleteFolder")
                {
                    _session.Attributes(attributesJson, false);
                    var folders = PlcGroupOperations.Get(parent, "Folders");
                    var existing = PlcGroupOperations.Find(folders, name);
                    if (action == "createFolder")
                    {
                        if (existing != null) throw new InvalidOperationException("Script folder already exists.");
                        if (folders.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native VBScriptUserFolderComposition.Create(string) unavailable.");
                        if (!writing) return "Script folder creation preview; no changes.";
                        meta["mayHaveChanged"] = true;
                        object created = folders is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptUserFolderComposition typedFolders ? typedFolders.Create(name) : PlcGroupOperations.Call(folders, "Create", new[] { typeof(string) }, name); meta["apiCallSuccess"] = true;
                        if (!string.Equals(HmiDynamic.Name(created), name, StringComparison.Ordinal)) throw new InvalidOperationException("Created folder name readback differs.");
                        meta["after"] = ClassicRow(created);
                        return "Script folder created and verified; no save/compile/download.";
                    }
                    if (existing == null) throw new PlcSoftwareException("NotFound", "Script folder not found: " + name);
                    int childCount = PlcGroupOperations.Items(PlcGroupOperations.Get(existing, "Folders")).Count(), scriptCount = PlcGroupOperations.Items(PlcGroupOperations.Get(existing, "VBScripts")).Count();
                    meta["childCount"] = childCount; meta["scriptCount"] = scriptCount;
                    if (childCount != 0 || scriptCount != 0) throw new InvalidOperationException("Only empty script folders can be deleted.");
                    return ClassicDelete(existing, folders, name, confirmDelete, meta, writing);
                }
                var scripts = PlcGroupOperations.Get(parent, "VBScripts");
                var script = PlcGroupOperations.Find(scripts, name) ?? throw new PlcSoftwareException("NotFound", "Exact VB script not found: " + scriptPath);
                switch (action)
                {
                    case "read": _session.Attributes(attributesJson, false); meta["script"] = ClassicRow(script, scriptPath); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["script"]!.Field("dataComplete")!.DeepClone(); return "VB script read.";
                    case "export":
                        _session.Attributes(attributesJson, false);
                        if (script is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScript typedScript)
                        {
                            // Typed VBScript.Export(FileInfo, ExportOptions).
                            var file = PlcSourceFileOutput.Plan(filePath);
                            if (dryRun) return "Native export preview; no file written.";
                            meta["mayHaveWrittenFiles"] = true; typedScript.Export(file, exportOption); meta["apiCallSuccess"] = true; meta["file"] = PlcSourceFileOutput.Verify(file);
                            return "VB script exported to a new XML file and hashed; no project change.";
                        }
                        return ClassicExport(script, filePath, meta, dryRun, exportOption);
                    case "delete":
                        _session.Attributes(attributesJson, false); meta["before"] = ClassicRow(script, scriptPath);
                        if (script is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScript deletable && scripts is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptComposition typedScripts)
                        {
                            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete a VB script.");
                            if (!writing) return "VB script deletion preview; no changes.";
                            meta["mayHaveChanged"] = true; deletable.Delete(); meta["apiCallSuccess"] = true;
                            if (typedScripts.Find(name) != null) throw new InvalidOperationException("VB script remains after Delete.");
                            meta["verifiedAbsent"] = true;
                            return "VB script deleted and absence verified; no save/compile/download.";
                        }
                        return ClassicDelete(script, scripts, name, confirmDelete, meta, writing);
                    default: return ClassicSetAttributes(script, attributesJson, meta, writing);
                }
            });

        public HmiMessage ManageClassicHmiCycle(string softwarePath, string action, string cycleName = "", string filePath = "", string importOptions = "None",
            string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true, string exportOptions = "WithDefaults")
            => RunTool("ManageClassicHmiCycle", meta => {
                var exportOption = (ExportOptions)Enum.Parse(typeof(ExportOptions), HmiExchangePolicy.ExportOption(exportOptions));
                meta["exportOptions"] = exportOptions;
                Logic.RequireAction(action, Logic.CycleActions); Logic.RequirePagination(offset, limit);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var cycles = hmi.Cycles ?? throw new NotSupportedException("Cycles composition unavailable.");
                meta["softwarePath"] = softwarePath; meta["cycleName"] = cycleName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "CycleComposition exposes no Create; new cycles arrive only through native XML Import. Cycle time/unit are dynamic attributes (see schema).";
                if (action == "import") { _session.Attributes(attributesJson, false); return ClassicImport(cycles, filePath, importOptions, confirmDelete, meta, writing, "Hmi.Cycle.Cycle"); }
                if (action == "read" && cycleName == "")
                {
                    _session.Attributes(attributesJson, false);
                    ClassicPage(meta, PlcGroupOperations.Items(cycles).ToArray(), offset, limit, c => ClassicRow(c));
                    return "Classic HMI cycles listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(cycleName, "cycleName");
                var cycle = PlcGroupOperations.Find(cycles, cycleName) ?? throw new PlcSoftwareException("NotFound", "Exact cycle not found: " + cycleName);
                bool system = cycle.GetType().GetProperty("IsSystemObject")?.GetValue(cycle) as bool? == true;
                meta["isSystemObject"] = system;
                switch (action)
                {
                    case "read": _session.Attributes(attributesJson, false); meta["cycle"] = ClassicRow(cycle); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["cycle"]!.Field("dataComplete")!.DeepClone(); return "Cycle read.";
                    case "export": _session.Attributes(attributesJson, false); return ClassicExport(cycle, filePath, meta, dryRun, exportOption);
                    case "delete":
                        _session.Attributes(attributesJson, false); meta["before"] = ClassicRow(cycle);
                        if (system) throw new InvalidOperationException("System cycles cannot be deleted.");
                        return ClassicDelete(cycle, cycles, cycleName, confirmDelete, meta, writing);
                    default:
                        if (system) throw new InvalidOperationException("System cycles are not editable.");
                        return ClassicSetAttributes(cycle, attributesJson, meta, writing);
                }
            });

        public HmiMessage ManageClassicHmiTextGraphicList(string softwarePath, string listKind, string action, string listName = "", string compositionName = "", string entryName = "",
            string typeName = "", string filePath = "", string importOptions = "None", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true, string exportOptions = "WithDefaults")
            => RunTool("ManageClassicHmiTextGraphicList", meta => {
                var exportOption = (ExportOptions)Enum.Parse(typeof(ExportOptions), HmiExchangePolicy.ExportOption(exportOptions));
                meta["exportOptions"] = exportOptions;
                Logic.RequireAction(action, Logic.ListActions); Logic.RequirePagination(offset, limit);
                if (!Logic.ListKinds.ContainsKey(listKind)) throw new ArgumentException("listKind must be text/graphic.");
                bool write = action is "createEntry" or "deleteEntry" or "import" or "delete" or "setAttributes";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var (property, expectedType) = Logic.ListKinds[listKind];
                var lists = PlcGroupOperations.Get(hmi, property);
                meta["softwarePath"] = softwarePath; meta["listKind"] = listKind; meta["listName"] = listName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "Text/graphic list compositions expose no Create; new lists arrive only through native XML Import. Entries are reachable only as compositions advertised by GetCompositionInfos.";
                if (action == "import") { _session.Attributes(attributesJson, false); return ClassicImport(lists, filePath, importOptions, confirmDelete, meta, writing, listKind == "text" ? "Hmi.TextGraphicList.TextList" : "Hmi.TextGraphicList.GraphicList"); }
                if (action == "read" && listName == "")
                {
                    _session.Attributes(attributesJson, false);
                    ClassicPage(meta, PlcGroupOperations.Items(lists).ToArray(), offset, limit, l => ClassicRow(l));
                    return "Classic HMI " + listKind + " lists listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(listName, "listName");
                var list = PlcGroupOperations.Find(lists, listName) ?? throw new PlcSoftwareException("NotFound", "Exact " + listKind + " list not found: " + listName);
                if (list.GetType().FullName != expectedType) throw new NotSupportedException("Unexpected native list type: " + list.GetType().FullName);
                switch (action)
                {
                    case "read":
                        _session.Attributes(attributesJson, false);
                        var row = ClassicRow(list); row["compositions"] = new HmiArray(HmiDynamic.CompositionNames(list).Select(n => (object)n).ToArray());
                        meta["list"] = row; meta["apiCallSuccess"] = true; meta["dataComplete"] = row["dataComplete"]!.DeepClone();
                        return "List read with advertised compositions; use readEntries with a compositionName.";
                    case "export": _session.Attributes(attributesJson, false); return ClassicExport(list, filePath, meta, dryRun, exportOption);
                    case "delete": _session.Attributes(attributesJson, false); meta["before"] = ClassicRow(list); return ClassicDelete(list, lists, listName, confirmDelete, meta, writing);
                    case "setAttributes": return ClassicSetAttributes(list, attributesJson, meta, writing);
                }
                var composition = HmiDynamic.Composition(list, compositionName);
                meta["compositionName"] = compositionName;
                if (action == "readEntries")
                {
                    _session.Attributes(attributesJson, false);
                    ClassicPage(meta, PlcGroupOperations.Items(composition).ToArray(), offset, limit, e => ClassicRow(e));
                    try { meta["creationTypes"] = new HmiArray(HmiDynamic.CreationTypes(composition).Select(t => (object)t.FullName!).ToArray()); }
                    catch (NotSupportedException ex) { meta["creationTypes"] = null; meta["creationTypesError"] = ex.Message; }
                    return "List entries read from the advertised composition; live pagination.";
                }
                if (action == "createEntry")
                {
                    var type = HmiDynamic.CreationType(composition, typeName);
                    var attributes = _session.Attributes(attributesJson, true);
                    meta["typeName"] = type.FullName; meta["requestedAttributes"] = attributes.DeepClone();
                    if (!writing) return "List entry creation preview; advertised type validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    var created = HmiDynamic.Create(composition, type, attributes); meta["apiCallSuccess"] = true;
                    meta["after"] = ClassicRow(created);
                    if (attributes.ContainsKey("Name") && !string.Equals(HmiDynamic.Name(created), attributes["Name"]?.GetValue<string>(), StringComparison.Ordinal)) throw new InvalidOperationException("Created entry Name readback differs.");
                    return "List entry created through the official dynamic composition; no save/compile/download.";
                }
                _session.Attributes(attributesJson, false); Logic.RequireName(entryName, "entryName");
                var entry = HmiDynamic.FindByName(composition, entryName);
                meta["before"] = ClassicRow(entry);
                if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete an entry.");
                if (!HmiDynamic.CanDelete(entry)) throw new NotSupportedException("Native Delete is not advertised for this entry type.");
                if (!writing) return "List entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                HmiDynamic.Delete(entry); meta["apiCallSuccess"] = true;
                if (PlcGroupOperations.Items(composition).Any(x => string.Equals(HmiDynamic.Name(x), entryName, StringComparison.Ordinal))) throw new InvalidOperationException("Entry remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "List entry deleted and verified absent; no save/compile/download.";
            });

        public HmiMessage ReadClassicHmiGlobalization(string softwarePath, int offset = 0, int limit = 100)
            => RunTool("GetClassicHmiGlobalization", meta => {
                Logic.RequirePagination(offset, limit);
                var graphics = _session.ClassicGraphics();
                meta["softwarePath"] = softwarePath; meta["provider"] = "Project graphics";
                ClassicPage(meta, PlcGroupOperations.Items(graphics).ToArray(), offset, limit, g => ClassicRow(g));
                meta["scope"] = "Project multilingual graphics: names, CLR scalars and advertised attributes. Image bytes are not read.";
                return "Classic HMI multilingual graphics listed; live pagination.";
            });

        public HmiMessage ReadClassicHmiFaceplates(string kind = "faceplate", string libraryName = "", string folderPath = "", int offset = 0, int limit = 100)
            => RunTool("ListClassicHmiFaceplates", meta => {
                Logic.RequirePagination(offset, limit);
                if (kind != "all" && !Logic.LibraryTypeKinds.ContainsKey(kind)) throw new ArgumentException("kind must be faceplate/vbScript/cScript/all.");
                var wanted = kind == "all" ? Logic.LibraryTypeKinds.Values.ToArray() : new[] { Logic.LibraryTypeKinds[kind] };
                var library = _session.ExactOpenEngineeringLibrary(libraryName);
                var root = _session.EngineeringLibraryFolder(library, folderPath, "TypeFolder");
                var found = new List<(string Path, object Type)>();
                void Walk(object folder, string path, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Library folder depth exceeds 64.");
                    foreach (var type in PlcGroupOperations.Items(PlcGroupOperations.Get(folder, "Types")))
                        if (wanted.Contains(type.GetType().FullName)) { if (found.Count >= 10000) throw new InvalidOperationException("More than 10000 matching types; narrow folderPath."); found.Add((path + (HmiDynamic.Name(type) ?? ""), type)); }
                    foreach (var child in PlcGroupOperations.Items(PlcGroupOperations.Get(folder, "Folders")))
                        Walk(child, path + (HmiDynamic.Name(child) ?? "") + "/", depth + 1);
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["kind"] = kind; meta["libraryName"] = libraryName; meta["folderPath"] = folderPath; meta["nativeTypes"] = new HmiArray(wanted.Select(w => (object)w).ToArray());
                ClassicPage(meta, found.Select(f => f.Type).ToArray(), offset, limit, t => {
                    var row = HmiScalar.Read(t); row["path"] = found.First(f => ReferenceEquals(f.Type, t)).Path;
                    var versions = PlcGroupOperations.Items(PlcGroupOperations.Get(t, "Versions")).Take(101).ToArray();
                    row["versions"] = new HmiArray(versions.Take(100).Select(v => (object)HmiScalar.Read(v)).ToArray()); row["versionsTruncated"] = versions.Length > 100;
                    row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && versions.Length <= 100 && row["versions"]!.Array().All(v => v!.Field("dataComplete")!.GetValue<bool>());
                    return (object)row;
                });
                meta["scope"] = "Classic HMI faceplate/VB-script/C-script library types with their versions (scalar properties only). No instantiation.";
                return "Classic HMI library types listed; live pagination.";
            });
        // ---- ProDiag CSV export -------------------------------------------------------------------------------------------------------------------


        private HmiMessage RunTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
            => _session.RunHmiStepTool(tool, meta => _session.Capture(meta, action), requiresProject);
    }
}
