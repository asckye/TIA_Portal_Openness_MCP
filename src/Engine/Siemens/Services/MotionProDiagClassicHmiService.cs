using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.MotionProDiagClassicHmiLogic;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class MotionProDiagClassicHmiService
    {
        private readonly IEngineeringSession _session;

        public MotionProDiagClassicHmiService(IEngineeringSession session) => _session = session;
        private static object? OptionalOfficialService(object owner, string typeName, out string state)
            => TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.ResolveMotionOfficialService(owner, typeName, out state);
        private static object RequireOfficialService(object owner, string typeName)
            => TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter.RequireMotionOfficialService(owner, typeName);



        // Typed VBScriptSystemFolder / VBScriptUserFolder navigation (Portal.ClassicHmiFolders.cs).
        private object ClassicScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath) => _session.ExactVbScriptFolder(hmi, folderPath);
        private static JsonObject ClassicRow(object target, string? path = null)
        {
            var row = EngineeringDynamicAccess.Read(target); row["clrProperties"] = EngineeringScalarProperties.Read(target); row["name"] = EngineeringDynamicAccess.Name(target);
            if (path != null) row["path"] = path;
            row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && row["clrProperties"]!["dataComplete"]!.GetValue<bool>();
            return row;
        }
        private static JsonObject ClassicPage(JsonObject meta, object[] items, int offset, int limit, Func<object, JsonNode> project)
        {
            var rows = items.Skip(offset).Take(limit).Select(project).ToArray();
            meta["records"] = new JsonArray(rows); meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Length;
            meta["offset"] = offset; meta["limit"] = limit; meta["total"] = items.Length;
            meta["nextOffset"] = offset + rows.Length < items.Length ? offset + rows.Length : (int?)null;
            meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Length == items.Length && rows.All(r => r!["dataComplete"]?.GetValue<bool>() != false);
            return meta;
        }
        private static string ClassicExport(object target, string filePath, JsonObject meta, bool dryRun)
        {
            var file = NativeFileOutput.Plan(filePath);
            var signature = new[] { typeof(FileInfo), typeof(ExportOptions) };
            if (target.GetType().GetMethod("Export", signature) == null) throw new NotSupportedException("Native Export(FileInfo, ExportOptions) unavailable on " + target.GetType().FullName + ".");
            if (dryRun) return "Native export preview; no file written.";
            meta["mayHaveWrittenFiles"] = true;
            EngineeringGroupOperations.Call(target, "Export", signature, file, ExportOptions.None);
            meta["file"] = NativeFileOutput.Verify(file); meta["apiCallSuccess"] = true;
            return "Native XML export written and hashed; content semantics not verified.";
        }
        private static string ClassicImport(object composition, string filePath, string importOptions, bool confirmDelete, JsonObject meta, bool writing)
        {
            var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
            if (!file.Exists) throw new FileNotFoundException("Import input not found.");
            var options = (ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions), typeof(ImportOptions))!;
            Logic.RequireImportConfirmation(options.ToString(), confirmDelete);
            var signature = new[] { typeof(FileInfo), typeof(ImportOptions) };
            if (composition.GetType().GetMethod("Import", signature) == null) throw new NotSupportedException("Native Import(FileInfo, ImportOptions) unavailable on " + composition.GetType().FullName + ".");
            meta["importOptions"] = options.ToString();
            if (!writing) return "Native import preview; no changes.";
            meta["mayHaveChanged"] = true;
            var result = EngineeringGroupOperations.Call(composition, "Import", signature, file, options);
            OfficialServiceAccess.AttachResult(meta, result);
            meta["importedNames"] = new JsonArray((result as IEnumerable)?.Cast<object>().Select(x => (JsonNode)(EngineeringDynamicAccess.Name(x) ?? "")).ToArray() ?? Array.Empty<JsonNode>());
            return "Native import returned; inspect imported names. No save/compile/download.";
        }
        private static string ClassicDelete(object target, object composition, string name, bool confirmDelete, JsonObject meta, bool writing)
        {
            if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required for delete.");
            if (target.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete unavailable on " + target.GetType().FullName + ".");
            if (!writing) return "Native deletion preview; no changes.";
            meta["mayHaveChanged"] = true;
            EngineeringGroupOperations.Call(target, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
            if (EngineeringGroupOperations.Find(composition, name) != null) throw new InvalidOperationException("Object remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "Object deleted and verified absent; no save/compile/download.";
        }
        private static string ClassicSetAttributes(object target, string attributesJson, JsonObject meta, bool writing)
        {
            var prepared = EngineeringDynamicAccess.Prepare(target, Logic.ParseAttributes(attributesJson, true));
            meta["before"] = ClassicRow(target);
            if (!writing) return "Attribute update preview; advertised writable attributes validated, no changes.";
            EngineeringDynamicAccess.Apply(target, prepared, meta); meta["apiCallSuccess"] = true;
            meta["after"] = ClassicRow(target);
            return "Attributes changed and verified by readback; no save/compile/download.";
        }

        public ResponseMessage ReadClassicHmiScripts(string softwarePath, string folderPath = "", int offset = 0, int limit = 100)
            => RunTool("ListClassicHmiScripts", meta => {
                Logic.RequirePagination(offset, limit);
                var hmi = _session.ExactClassicHmi(softwarePath);
                var root = ClassicScriptFolder(hmi, EngineeringGroupOperations.Parts(folderPath, true));
                var scripts = new List<(string Path, object Script)>(); var folders = new JsonArray();
                void Walk(object folder, string path, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Script folder depth exceeds 64.");
                    foreach (var script in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "VBScripts")))
                    {
                        if (scripts.Count >= 10000) throw new InvalidOperationException("More than 10000 scripts; narrow folderPath.");
                        scripts.Add((path + (EngineeringDynamicAccess.Name(script) ?? ""), script));
                    }
                    foreach (var child in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Folders")))
                    {
                        var childPath = path + (EngineeringDynamicAccess.Name(child) ?? "") + "/"; folders.Add(childPath.TrimEnd('/'));
                        Walk(child, childPath, depth + 1);
                    }
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["softwarePath"] = softwarePath; meta["folderPath"] = folderPath; meta["folders"] = folders;
                meta["codeAccess"] = "VB script source is not a typed Openness property; readable attributes are listed per script and the native XML is available via ManageClassicHmiScript export.";
                ClassicPage(meta, scripts.Select(s => s.Script).ToArray(), offset, limit, s => ClassicRow(s, scripts.First(x => ReferenceEquals(x.Script, s)).Path));
                return "Classic HMI VB scripts listed with advertised attributes; live pagination.";
            });

        public ResponseMessage ManageClassicHmiScript(string softwarePath, string scriptPath, string action, string filePath = "", string importOptions = "None",
            string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManageClassicHmiScript", meta => {
                Logic.RequireAction(action, Logic.ScriptActions);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["scriptPath"] = scriptPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action == "import")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    var folder = ClassicScriptFolder(hmi, EngineeringGroupOperations.Parts(scriptPath, true));
                    return ClassicImport(EngineeringGroupOperations.Get(folder, "VBScripts"), filePath, importOptions, confirmDelete, meta, writing);
                }
                var (folderPath, name) = Logic.SplitObjectPath(scriptPath);
                var parent = ClassicScriptFolder(hmi, folderPath);
                if (action == "createFolder" || action == "deleteFolder")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    var folders = EngineeringGroupOperations.Get(parent, "Folders");
                    var existing = EngineeringGroupOperations.Find(folders, name);
                    if (action == "createFolder")
                    {
                        if (existing != null) throw new InvalidOperationException("Script folder already exists.");
                        if (folders.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native VBScriptUserFolderComposition.Create(string) unavailable.");
                        if (!writing) return "Script folder creation preview; no changes.";
                        meta["mayHaveChanged"] = true;
                        object created = folders is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScriptUserFolderComposition typedFolders ? typedFolders.Create(name) : EngineeringGroupOperations.Call(folders, "Create", new[] { typeof(string) }, name); meta["apiCallSuccess"] = true;
                        if (!string.Equals(EngineeringDynamicAccess.Name(created), name, StringComparison.Ordinal)) throw new InvalidOperationException("Created folder name readback differs.");
                        meta["after"] = ClassicRow(created);
                        return "Script folder created and verified; no save/compile/download.";
                    }
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "Script folder not found: " + name);
                    int childCount = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(existing, "Folders")).Count(), scriptCount = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(existing, "VBScripts")).Count();
                    meta["childCount"] = childCount; meta["scriptCount"] = scriptCount;
                    if (childCount != 0 || scriptCount != 0) throw new InvalidOperationException("Only empty script folders can be deleted.");
                    return ClassicDelete(existing, folders, name, confirmDelete, meta, writing);
                }
                var scripts = EngineeringGroupOperations.Get(parent, "VBScripts");
                var script = EngineeringGroupOperations.Find(scripts, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact VB script not found: " + scriptPath);
                switch (action)
                {
                    case "read": Logic.ParseAttributes(attributesJson, false); meta["script"] = ClassicRow(script, scriptPath); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["script"]!["dataComplete"]!.DeepClone(); return "VB script read.";
                    case "export":
                        Logic.ParseAttributes(attributesJson, false);
                        if (script is global::Siemens.Engineering.Hmi.RuntimeScripting.VBScript typedScript)
                        {
                            // Typed VBScript.Export(FileInfo, ExportOptions).
                            var file = NativeFileOutput.Plan(filePath);
                            if (dryRun) return "Native export preview; no file written.";
                            meta["mayHaveWrittenFiles"] = true; typedScript.Export(file, ExportOptions.None); meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file);
                            return "VB script exported to a new XML file and hashed; no project change.";
                        }
                        return ClassicExport(script, filePath, meta, dryRun);
                    case "delete":
                        Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(script, scriptPath);
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

        public ResponseMessage ManageClassicHmiCycle(string softwarePath, string action, string cycleName = "", string filePath = "", string importOptions = "None",
            string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManageClassicHmiCycle", meta => {
                Logic.RequireAction(action, Logic.CycleActions); Logic.RequirePagination(offset, limit);
                bool write = action != "read" && action != "export";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var cycles = hmi.Cycles ?? throw new NotSupportedException("Cycles composition unavailable.");
                meta["softwarePath"] = softwarePath; meta["cycleName"] = cycleName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "CycleComposition exposes no Create; new cycles arrive only through native XML Import. Cycle time/unit are dynamic attributes (see schema).";
                if (action == "import") { Logic.ParseAttributes(attributesJson, false); return ClassicImport(cycles, filePath, importOptions, confirmDelete, meta, writing); }
                if (action == "read" && cycleName == "")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(cycles).ToArray(), offset, limit, c => ClassicRow(c));
                    return "Classic HMI cycles listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(cycleName, "cycleName");
                var cycle = EngineeringGroupOperations.Find(cycles, cycleName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact cycle not found: " + cycleName);
                bool system = cycle.GetType().GetProperty("IsSystemObject")?.GetValue(cycle) as bool? == true;
                meta["isSystemObject"] = system;
                switch (action)
                {
                    case "read": Logic.ParseAttributes(attributesJson, false); meta["cycle"] = ClassicRow(cycle); meta["apiCallSuccess"] = true; meta["dataComplete"] = meta["cycle"]!["dataComplete"]!.DeepClone(); return "Cycle read.";
                    case "export": Logic.ParseAttributes(attributesJson, false); return ClassicExport(cycle, filePath, meta, dryRun);
                    case "delete":
                        Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(cycle);
                        if (system) throw new InvalidOperationException("System cycles cannot be deleted.");
                        return ClassicDelete(cycle, cycles, cycleName, confirmDelete, meta, writing);
                    default:
                        if (system) throw new InvalidOperationException("System cycles are not editable.");
                        return ClassicSetAttributes(cycle, attributesJson, meta, writing);
                }
            });

        public ResponseMessage ManageClassicHmiTextGraphicList(string softwarePath, string listKind, string action, string listName = "", string compositionName = "", string entryName = "",
            string typeName = "", string filePath = "", string importOptions = "None", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManageClassicHmiTextGraphicList", meta => {
                Logic.RequireAction(action, Logic.ListActions); Logic.RequirePagination(offset, limit);
                if (!Logic.ListKinds.ContainsKey(listKind)) throw new ArgumentException("listKind must be text/graphic.");
                bool write = action is "createEntry" or "deleteEntry" or "import" or "delete" or "setAttributes";
                bool writing = write && !dryRun;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                var (property, expectedType) = Logic.ListKinds[listKind];
                var lists = EngineeringGroupOperations.Get(hmi, property);
                meta["softwarePath"] = softwarePath; meta["listKind"] = listKind; meta["listName"] = listName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["nativeLimits"] = "Text/graphic list compositions expose no Create; new lists arrive only through native XML Import. Entries are reachable only as compositions advertised by GetCompositionInfos.";
                if (action == "import") { Logic.ParseAttributes(attributesJson, false); return ClassicImport(lists, filePath, importOptions, confirmDelete, meta, writing); }
                if (action == "read" && listName == "")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(lists).ToArray(), offset, limit, l => ClassicRow(l));
                    return "Classic HMI " + listKind + " lists listed with advertised attributes; live pagination.";
                }
                Logic.RequireName(listName, "listName");
                var list = EngineeringGroupOperations.Find(lists, listName) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + listKind + " list not found: " + listName);
                if (list.GetType().FullName != expectedType) throw new NotSupportedException("Unexpected native list type: " + list.GetType().FullName);
                switch (action)
                {
                    case "read":
                        Logic.ParseAttributes(attributesJson, false);
                        var row = ClassicRow(list); row["compositions"] = new JsonArray(EngineeringDynamicAccess.CompositionNames(list).Select(n => (JsonNode)n).ToArray());
                        meta["list"] = row; meta["apiCallSuccess"] = true; meta["dataComplete"] = row["dataComplete"]!.DeepClone();
                        return "List read with advertised compositions; use readEntries with a compositionName.";
                    case "export": Logic.ParseAttributes(attributesJson, false); return ClassicExport(list, filePath, meta, dryRun);
                    case "delete": Logic.ParseAttributes(attributesJson, false); meta["before"] = ClassicRow(list); return ClassicDelete(list, lists, listName, confirmDelete, meta, writing);
                    case "setAttributes": return ClassicSetAttributes(list, attributesJson, meta, writing);
                }
                var composition = EngineeringDynamicAccess.Composition(list, compositionName);
                meta["compositionName"] = compositionName;
                if (action == "readEntries")
                {
                    Logic.ParseAttributes(attributesJson, false);
                    ClassicPage(meta, EngineeringGroupOperations.Items(composition).ToArray(), offset, limit, e => ClassicRow(e));
                    try { meta["creationTypes"] = new JsonArray(EngineeringDynamicAccess.CreationTypes(composition).Select(t => (JsonNode)t.FullName!).ToArray()); }
                    catch (NotSupportedException ex) { meta["creationTypes"] = null; meta["creationTypesError"] = ex.Message; }
                    return "List entries read from the advertised composition; live pagination.";
                }
                if (action == "createEntry")
                {
                    var type = EngineeringDynamicAccess.CreationType(composition, typeName);
                    var attributes = Logic.ParseAttributes(attributesJson, true);
                    meta["typeName"] = type.FullName; meta["requestedAttributes"] = attributes.DeepClone();
                    if (!writing) return "List entry creation preview; advertised type validated, no changes.";
                    meta["mayHaveChanged"] = true;
                    var created = EngineeringDynamicAccess.Create(composition, type, attributes); meta["apiCallSuccess"] = true;
                    meta["after"] = ClassicRow(created);
                    if (attributes.ContainsKey("Name") && !string.Equals(EngineeringDynamicAccess.Name(created), attributes["Name"]?.GetValue<string>(), StringComparison.Ordinal)) throw new InvalidOperationException("Created entry Name readback differs.");
                    return "List entry created through the official dynamic composition; no save/compile/download.";
                }
                Logic.ParseAttributes(attributesJson, false); Logic.RequireName(entryName, "entryName");
                var entry = EngineeringDynamicAccess.FindByName(composition, entryName);
                meta["before"] = ClassicRow(entry);
                if (!confirmDelete) throw new ArgumentException("confirmDelete=true is required to delete an entry.");
                if (!EngineeringDynamicAccess.CanDelete(entry)) throw new NotSupportedException("Native Delete is not advertised for this entry type.");
                if (!writing) return "List entry deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                EngineeringDynamicAccess.Delete(entry); meta["apiCallSuccess"] = true;
                if (EngineeringGroupOperations.Items(composition).Any(x => string.Equals(EngineeringDynamicAccess.Name(x), entryName, StringComparison.Ordinal))) throw new InvalidOperationException("Entry remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "List entry deleted and verified absent; no save/compile/download.";
            });

        public ResponseMessage ReadClassicHmiGlobalization(string softwarePath, int offset = 0, int limit = 100)
            => RunTool("GetClassicHmiGlobalization", meta => {
                Logic.RequirePagination(offset, limit);
                var hmi = _session.ExactClassicHmi(softwarePath);
                var provider = RequireOfficialService(hmi, Logic.GraphicsProvider);
                var graphics = EngineeringGroupOperations.Get(provider, "Graphics");
                meta["softwarePath"] = softwarePath; meta["provider"] = provider.GetType().FullName;
                ClassicPage(meta, EngineeringGroupOperations.Items(graphics).ToArray(), offset, limit, g => ClassicRow(g));
                meta["scope"] = "Multilingual graphics of the classic HMI GraphicsProvider: names, CLR scalars and advertised attributes. Image bytes are not read.";
                return "Classic HMI multilingual graphics listed; live pagination.";
            });

        public ResponseMessage ReadClassicHmiFaceplates(string kind = "faceplate", string libraryName = "", string folderPath = "", int offset = 0, int limit = 100)
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
                    foreach (var type in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Types")))
                        if (wanted.Contains(type.GetType().FullName)) { if (found.Count >= 10000) throw new InvalidOperationException("More than 10000 matching types; narrow folderPath."); found.Add((path + (EngineeringDynamicAccess.Name(type) ?? ""), type)); }
                    foreach (var child in EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Folders")))
                        Walk(child, path + (EngineeringDynamicAccess.Name(child) ?? "") + "/", depth + 1);
                }
                Walk(root, folderPath == "" ? "" : folderPath.TrimEnd('/') + "/", 0);
                meta["kind"] = kind; meta["libraryName"] = libraryName; meta["folderPath"] = folderPath; meta["nativeTypes"] = new JsonArray(wanted.Select(w => (JsonNode)w).ToArray());
                ClassicPage(meta, found.Select(f => f.Type).ToArray(), offset, limit, t => {
                    var row = EngineeringScalarProperties.Read(t); row["path"] = found.First(f => ReferenceEquals(f.Type, t)).Path;
                    var versions = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(t, "Versions")).Take(101).ToArray();
                    row["versions"] = new JsonArray(versions.Take(100).Select(v => (JsonNode)EngineeringScalarProperties.Read(v)).ToArray()); row["versionsTruncated"] = versions.Length > 100;
                    row["dataComplete"] = row["dataComplete"]!.GetValue<bool>() && versions.Length <= 100 && row["versions"]!.AsArray().All(v => v!["dataComplete"]!.GetValue<bool>());
                    return (JsonNode)row;
                });
                meta["scope"] = "Classic HMI faceplate/VB-script/C-script library types with their versions (scalar properties only). No instantiation.";
                return "Classic HMI library types listed; live pagination.";
            });
        // ---- ProDiag CSV export -------------------------------------------------------------------------------------------------------------------


        private ResponseMessage RunTool(string tool, Func<JsonObject, string> action, bool requiresProject = true)
            => _session.RunHmiStepTool(tool, meta => ClassicMotionToolContract.Capture(meta, action), requiresProject);
    }
}
