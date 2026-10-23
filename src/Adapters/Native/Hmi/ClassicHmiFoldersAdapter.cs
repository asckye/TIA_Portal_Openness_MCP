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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Logic = TiaMcp.Adapters.Native.Hmi.ClassicHmiFoldersLogic;

namespace TiaMcp.Adapters.Native.Hmi
{
    // Classic (non-Unified) WinCC folder hierarchy. Official pages: "Exporting / Importing a pop-up screen",
    // "Exporting / Importing a slide-in screen", "Exporting screen templates from a folder" / "Deleting a screen template from a
    // folder", "Deleting a user-defined folder of an HMI device", "Creating user-defined folders for HMI tags", "Exporting VB scripts
    // from a folder", "Exporting/importing graphics". Everything is the official V20/V21 Siemens.Engineering.Hmi API; the
    // V21 uses the project GraphicsProvider; earlier releases expose project.Graphics. The reference project's HMI is Unified, so these tools were
    // verified by shape checks and by their Unified refusal on TIA Portal V21 (2026-09-19);
    // see docs/reference/real-machine-ledger.md for the native acceptance scope.
    internal sealed class ClassicHmiFoldersAdapter
    {
        private readonly HmiSession _session;

        public ClassicHmiFoldersAdapter(HmiSession session) => _session = session;

        // ---- rows ------------------------------------------------------------------------------------------------------------------
        private static HmiArray HmiNames(object composition, int max = 500) => new HmiArray(PlcGroupOperations.Items(composition).Take(max).Select(x => (object)PlcGroupOperations.Get(x, "Name").ToString()!).ToArray());
        private static HmiObject ScreenFolderRow(ScreenFolder folder, int depth, int maxDepth)
        {
            ScreenComposition screens = folder.Screens; ScreenUserFolderComposition folders = folder.Folders;
            var row = new HmiObject { ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name, ["screenCount"] = screens.Count, ["folderCount"] = folders.Count, ["screens"] = HmiNames(screens) };
            if (depth < maxDepth) row["folders"] = new HmiArray(PlcGroupOperations.Items(folders).Cast<ScreenUserFolder>().Select(f => (object)ScreenFolderRow(f, depth + 1, maxDepth)).ToArray()); else row["foldersTruncated"] = folders.Count > 0;
            return row;
        }
        private static HmiObject PopupFolderRow(ScreenPopupFolder folder, int depth, int maxDepth)
        {
            ScreenPopupComposition popups = folder.ScreenPopups; ScreenPopupUserFolderComposition folders = folder.Folders;
            var row = new HmiObject { ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name, ["popupCount"] = popups.Count, ["folderCount"] = folders.Count, ["screenPopups"] = HmiNames(popups) };
            if (depth < maxDepth) row["folders"] = new HmiArray(PlcGroupOperations.Items(folders).Cast<ScreenPopupUserFolder>().Select(f => (object)PopupFolderRow(f, depth + 1, maxDepth)).ToArray()); else row["foldersTruncated"] = folders.Count > 0;
            return row;
        }
        private static HmiObject TemplateFolderRow(ScreenTemplateFolder folder, int depth, int maxDepth)
        {
            ScreenTemplateComposition templates = folder.ScreenTemplates; ScreenTemplateUserFolderComposition folders = folder.Folders;
            var row = new HmiObject { ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name, ["templateCount"] = templates.Count, ["folderCount"] = folders.Count, ["screenTemplates"] = HmiNames(templates) };
            if (depth < maxDepth) row["folders"] = new HmiArray(PlcGroupOperations.Items(folders).Cast<ScreenTemplateUserFolder>().Select(f => (object)TemplateFolderRow(f, depth + 1, maxDepth)).ToArray()); else row["foldersTruncated"] = folders.Count > 0;
            return row;
        }
        private static HmiObject TagFolderRow(TagFolder folder, int depth, int maxDepth)
        {
            TagTableComposition tables = folder.TagTables; TagUserFolderComposition folders = folder.Folders;
            var row = new HmiObject { ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name, ["tagTableCount"] = tables.Count, ["folderCount"] = folders.Count, ["tagTables"] = HmiNames(tables) };
            if (folder is TagSystemFolder system) Safe(row, "defaultTagTable", () => system.DefaultTagTable?.Name);
            if (depth < maxDepth) row["folders"] = new HmiArray(PlcGroupOperations.Items(folders).Cast<TagUserFolder>().Select(f => (object)TagFolderRow(f, depth + 1, maxDepth)).ToArray()); else row["foldersTruncated"] = folders.Count > 0;
            return row;
        }
        private static HmiObject ScriptFolderRow(VBScriptFolder folder, int depth, int maxDepth)
        {
            VBScriptComposition scripts = folder.VBScripts; VBScriptUserFolderComposition folders = folder.Folders;
            var row = new HmiObject { ["name"] = folder.Name, ["folderClass"] = folder.GetType().Name, ["scriptCount"] = scripts.Count, ["folderCount"] = folders.Count, ["vbScripts"] = new HmiArray(PlcGroupOperations.Items(scripts).Cast<VBScript>().Take(500).Select(s => (object)s.Name).ToArray()) };
            if (depth < maxDepth) row["folders"] = new HmiArray(PlcGroupOperations.Items(folders).Cast<VBScriptUserFolder>().Select(f => (object)ScriptFolderRow(f, depth + 1, maxDepth)).ToArray()); else row["foldersTruncated"] = folders.Count > 0;
            return row;
        }
        private static HmiObject SlideinRow(ScreenSlidein slidein) => new HmiObject { ["slideinType"] = slidein.SlideinType.ToString(), ["screenClass"] = slidein.GetType().Name };

        // ---- folder navigation (typed) --------------------------------------------------------------------------------------------------
        private static ScreenFolder ExactScreenFolder(HmiTarget hmi, string folderPath)
        {
            ScreenFolder current = hmi.ScreenFolder;
            foreach (var part in PlcGroupOperations.Parts(folderPath, true)) { ScreenUserFolderComposition folders = current.Folders; current = folders.Find(part) ?? throw new PlcSoftwareException("NotFound", "Screen folder not found: " + part); }
            return current;
        }
        private static ScreenPopupFolder ExactPopupFolder(HmiTarget hmi, string folderPath)
        {
            ScreenPopupFolder current = hmi.ScreenPopupFolder;
            foreach (var part in PlcGroupOperations.Parts(folderPath, true)) { ScreenPopupUserFolderComposition folders = current.Folders; current = folders.Find(part) ?? throw new PlcSoftwareException("NotFound", "Pop-up screen folder not found: " + part); }
            return current;
        }
        private static ScreenTemplateFolder ExactTemplateFolder(HmiTarget hmi, string folderPath)
        {
            ScreenTemplateFolder current = hmi.ScreenTemplateFolder;
            foreach (var part in PlcGroupOperations.Parts(folderPath, true)) { ScreenTemplateUserFolderComposition folders = current.Folders; current = folders.Find(part) ?? throw new PlcSoftwareException("NotFound", "Screen template folder not found: " + part); }
            return current;
        }
        private static TagFolder ExactTagFolder(HmiTarget hmi, string folderPath)
        {
            TagFolder current = hmi.TagFolder;
            foreach (var part in PlcGroupOperations.Parts(folderPath, true)) { TagUserFolderComposition folders = current.Folders; current = folders.Find(part) ?? throw new PlcSoftwareException("NotFound", "Tag folder not found: " + part); }
            return current;
        }
        private static HmiArray ImportedNames(object? imported) => imported is System.Collections.IEnumerable list && imported is not string ? new HmiArray(list.Cast<object>().Select(x => (object)(PlcGroupOperations.Get(x, "Name").ToString() ?? "")).ToArray()) : new HmiArray();

        // ---- ReadClassicHmiScreenTree -----------------------------------------------------------------------------------------------------
        public HmiMessage ReadClassicHmiScreenTree(string softwarePath, string kind = "all", string folderPath = "", int maxDepth = 4)
            => RunTool("GetClassicHmiScreenTree", meta => {
                Logic.ValidateTreeRequest(kind, folderPath, maxDepth);
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["kind"] = kind; meta["folderPath"] = folderPath;
                if (kind == "all" || kind == "screens") Safe(meta, "screens", () => ScreenFolderRow(ExactScreenFolder(hmi, folderPath), 1, maxDepth));
                if (kind == "all" || kind == "popups") Safe(meta, "popups", () => PopupFolderRow(ExactPopupFolder(hmi, folderPath), 1, maxDepth));
                if (kind == "all" || kind == "templates") Safe(meta, "templates", () => TemplateFolderRow(ExactTemplateFolder(hmi, folderPath), 1, maxDepth));
                if (kind == "all" || kind == "slideins") Safe(meta, "slideins", () => { ScreenSlideinSystemFolder folder = hmi.ScreenSlideinFolder; ScreenSlideinComposition slideins = folder.ScreenSlideins; return new HmiArray(PlcGroupOperations.Items(slideins).Cast<ScreenSlidein>().Select(s => (object)SlideinRow(s)).ToArray()); });
                if (kind == "all")
                {
                    Safe(meta, "screenOverview", () => { ScreenOverview overview = hmi.ScreenOverview; return overview == null ? null : new HmiObject { ["screenClass"] = overview.GetType().Name, ["exportable"] = true }; });
                    Safe(meta, "screenGlobalElements", () => { ScreenGlobalElements elements = hmi.ScreenGlobalElements; return elements == null ? null : new HmiObject { ["screenClass"] = elements.GetType().Name, ["exportable"] = true }; });
                }
                meta["apiCallSuccess"] = true;
                meta["scope"] = "ScreenSystemFolder / ScreenUserFolder (Screens, Folders), ScreenPopupSystemFolder / ScreenPopupUserFolder (ScreenPopups), ScreenTemplateSystemFolder / ScreenTemplateUserFolder (ScreenTemplates), ScreenSlideinSystemFolder (ScreenSlidein SlideinType), ScreenOverview and ScreenGlobalElements presence; names only (first 500 per folder), recursive to maxDepth. No modification.";
                return "Classic HMI screen folder tree read; no modification.";
            });

        // ---- ManageClassicHmiScreenObject ------------------------------------------------------------------------------------------------
        public HmiMessage ManageClassicHmiScreenObject(string softwarePath, string objectKind, string objectPath = "", string action = "read", string filePath = "", string importOptions = "None", bool confirmDelete = false, bool dryRun = true, string exportOptions = "WithDefaults")
            => RunTool("ManageClassicHmiScreenObject", meta => {
                var exportOption = (ExportOptions)Enum.Parse(typeof(ExportOptions), HmiExchangePolicy.ExportOption(exportOptions));
                meta["exportOptions"] = exportOptions;
                bool writing = Logic.ValidateObjectRequest(objectKind, objectPath, action, filePath, importOptions, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["objectKind"] = objectKind; meta["objectPath"] = objectPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                string Export(Action<FileInfo, ExportOptions> export, string label)
                {
                    var file = PlcSourceFileOutput.Plan(filePath);
                    if (dryRun) return label + " export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true; export(file, exportOption); meta["apiCallSuccess"] = true; meta["file"] = PlcSourceFileOutput.Verify(file);
                    return label + " exported to a new XML file and hashed; no project change.";
                }
                string Import(Func<FileInfo, ImportOptions, object?> import, string label, object? collection = null, string objectType = "")
                {
                    var source = HmiFiles.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = HmiFiles.FileRow(source);
                    var options = (ImportOptions)Enum.Parse(typeof(ImportOptions), importOptions); meta["importOptions"] = options.ToString();
                    if (collection == null && options == ImportOptions.None)
                        throw new PlcSoftwareException("InvalidState", "The singleton HMI target already exists; select importOptions=Override and confirmDelete=true explicitly.");
                    if (collection != null) HmiExchangePolicy.BeforeImport(collection, filePath, objectType, options == ImportOptions.Override);
                    if (!writing) return label + " import preview; no changes.";
                    meta["mayHaveChanged"] = true; var imported = import(source, options); meta["apiCallSuccess"] = true; meta["imported"] = ImportedNames(imported);
                    return label + " imported (native result attached); project not saved / compiled.";
                }
                if (objectKind == "overview")
                {
                    ScreenOverview overview = hmi.ScreenOverview ?? throw new NotSupportedException("ScreenOverview unavailable on this HMI device.");
                    if (action == "read") { meta["object"] = new HmiObject { ["screenClass"] = overview.GetType().Name }; return "Screen overview present; export it for its content."; }
                    return action == "export" ? Export((f, o) => overview.Export(f, o), "Screen overview") : Import((f, o) => { hmi.ImportScreenOverview(f, o); return null; }, "Screen overview");
                }
                if (objectKind == "globalElements")
                {
                    ScreenGlobalElements elements = hmi.ScreenGlobalElements ?? throw new NotSupportedException("ScreenGlobalElements unavailable on this HMI device.");
                    if (action == "read") { meta["object"] = new HmiObject { ["screenClass"] = elements.GetType().Name }; return "Global screen elements present; export them for their content."; }
                    return action == "export" ? Export((f, o) => elements.Export(f, o), "Global screen elements") : Import((f, o) => { hmi.ImportScreenGlobalElements(f, o); return null; }, "Global screen elements");
                }
                if (objectKind == "slidein")
                {
                    ScreenSlideinSystemFolder folder = hmi.ScreenSlideinFolder ?? throw new NotSupportedException("ScreenSlideinFolder unavailable on this HMI device.");
                    ScreenSlideinComposition slideins = folder.ScreenSlideins;
                    if (action == "import") return Import((f, o) => slideins.Import(f, o), "Slide-in screen", slideins, "Hmi.Screen.ScreenSlidein");
                    var type = (SlideinType)Enum.Parse(typeof(SlideinType), objectPath);
                    ScreenSlidein slidein = slideins.Find(type) ?? throw new PlcSoftwareException("NotFound", "Slide-in screen not found for SlideinType " + objectPath + " (present: " + string.Join(", ", PlcGroupOperations.Items(slideins).Cast<ScreenSlidein>().Select(s => s.SlideinType.ToString())) + ").");
                    meta["object"] = SlideinRow(slidein);
                    if (action == "read") return "Slide-in screen read; export it for its content.";
                    return Export((f, o) => slidein.Export(f, o), "Slide-in screen " + objectPath);
                }
                var parts = PlcGroupOperations.Parts(objectPath, true);
                string folderPath = action == "import" ? objectPath : string.Join("/", parts.Take(Math.Max(0, parts.Length - 1)));
                string name = action == "import" ? "" : parts.Last();
                if (objectKind == "popup")
                {
                    ScreenPopupFolder folder = ExactPopupFolder(hmi, folderPath); ScreenPopupComposition popups = folder.ScreenPopups;
                    meta["folder"] = folder.Name;
                    if (action == "import") return Import((f, o) => popups.Import(f, o), "Pop-up screen", popups, "Hmi.Screen.ScreenPopup");
                    ScreenPopup popup = popups.Find(name) ?? throw new PlcSoftwareException("NotFound", "Pop-up screen not found: " + objectPath);
                    meta["object"] = new HmiObject { ["name"] = popup.Name, ["screenClass"] = popup.GetType().Name };
                    if (action == "read") return "Pop-up screen read; export it for its content.";
                    if (action == "export") return Export((f, o) => popup.Export(f, o), "Pop-up screen " + name);
                    if (!writing) return "Pop-up screen deletion preview; no changes.";
                    meta["mayHaveChanged"] = true; popup.Delete(); meta["apiCallSuccess"] = true;
                    if (ExactPopupFolder(hmi, folderPath).ScreenPopups.Find(name) != null) throw new InvalidOperationException("Pop-up screen remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "Pop-up screen deleted and absence verified; project not saved.";
                }
                ScreenTemplateFolder templateFolder = ExactTemplateFolder(hmi, folderPath); ScreenTemplateComposition templates = templateFolder.ScreenTemplates;
                meta["folder"] = templateFolder.Name;
                if (action == "import") return Import((f, o) => templates.Import(f, o), "Screen template", templates, "Hmi.Screen.ScreenTemplate");
                ScreenTemplate template = templates.Find(name) ?? throw new PlcSoftwareException("NotFound", "Screen template not found: " + objectPath);
                meta["object"] = new HmiObject { ["name"] = template.Name, ["screenClass"] = template.GetType().Name };
                if (action == "read") return "Screen template read; export it for its content.";
                if (action == "export") return Export((f, o) => template.Export(f, o), "Screen template " + name);
                if (!writing) return "Screen template deletion preview; no changes.";
                meta["mayHaveChanged"] = true; template.Delete(); meta["apiCallSuccess"] = true;
                if (ExactTemplateFolder(hmi, folderPath).ScreenTemplates.Find(name) != null) throw new InvalidOperationException("Screen template remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Screen template deleted and absence verified; project not saved.";
            });

        // ---- ManageClassicHmiFolder -------------------------------------------------------------------------------------------------------
        public HmiMessage ManageClassicHmiFolder(string softwarePath, string folderKind, string folderPath = "", string action = "read", string newName = "", bool confirmDelete = false, bool dryRun = true)
            => RunTool("ManageClassicHmiFolder", meta => {
                bool writing = Logic.ValidateFolderRequest(folderKind, folderPath, action, newName, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var hmi = _session.ExactClassicHmi(softwarePath);
                meta["softwarePath"] = softwarePath; meta["folderKind"] = folderKind; meta["folderPath"] = folderPath; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                var parts = PlcGroupOperations.Parts(folderPath, true);
                string parentPath = string.Join("/", parts.Take(Math.Max(0, parts.Length - 1))), leaf = parts.Length == 0 ? "" : parts.Last();
                // Each family has its own typed folder / composition classes; the rows and the create / delete calls are typed per kind.
                HmiObject Row(object folder) => folder switch
                {
                    ScreenFolder s => ScreenFolderRow(s, 1, 2), ScreenPopupFolder p => PopupFolderRow(p, 1, 2), ScreenTemplateFolder t => TemplateFolderRow(t, 1, 2), TagFolder g => TagFolderRow(g, 1, 2), VBScriptFolder v => ScriptFolderRow(v, 1, 2),
                    _ => HmiScalar.Read(folder)
                };
                object Resolve(string path) => folderKind switch
                {
                    "screens" => ExactScreenFolder(hmi, path), "popups" => ExactPopupFolder(hmi, path), "templates" => ExactTemplateFolder(hmi, path), "tags" => ExactTagFolder(hmi, path),
                    _ => _session.ExactVbScriptFolder(hmi, PlcGroupOperations.Parts(path, true))
                };
                if (action == "read") { meta["folder"] = Row(Resolve(folderPath)); meta["apiCallSuccess"] = true; return "Classic HMI folder read; no modification."; }
                if (action == "create")
                {
                    var parent = Resolve(folderPath); meta["parent"] = PlcGroupOperations.Get(parent, "Name").ToString();
                    bool exists = parent switch
                    {
                        ScreenFolder s => s.Folders.Find(newName) != null, ScreenPopupFolder p => p.Folders.Find(newName) != null, ScreenTemplateFolder t => t.Folders.Find(newName) != null, TagFolder g => g.Folders.Find(newName) != null, VBScriptFolder v => v.Folders.Find(newName) != null, _ => false
                    };
                    if (exists) throw new InvalidOperationException("Folder already exists: " + newName);
                    if (!writing) return "Folder creation preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    object created = parent switch
                    {
                        ScreenFolder s => s.Folders.Create(newName), ScreenPopupFolder p => p.Folders.Create(newName), ScreenTemplateFolder t => t.Folders.Create(newName), TagFolder g => g.Folders.Create(newName), VBScriptFolder v => v.Folders.Create(newName),
                        _ => throw new NotSupportedException("Unknown folder class.")
                    };
                    meta["apiCallSuccess"] = true;
                    if (!string.Equals(PlcGroupOperations.Get(created, "Name").ToString(), newName, StringComparison.Ordinal)) throw new InvalidOperationException("Created folder name readback differs.");
                    meta["after"] = Row(created);
                    return "Classic HMI user folder created and read back; project not saved.";
                }
                var target = Resolve(folderPath); meta["before"] = Row(target);
                int children = target switch
                {
                    ScreenFolder s => s.Folders.Count + s.Screens.Count, ScreenPopupFolder p => p.Folders.Count + p.ScreenPopups.Count, ScreenTemplateFolder t => t.Folders.Count + t.ScreenTemplates.Count, TagFolder g => g.Folders.Count + g.TagTables.Count, VBScriptFolder v => v.Folders.Count + v.VBScripts.Count, _ => 0
                };
                if (children != 0) throw new InvalidOperationException("Only empty user folders can be deleted (" + children + " child objects).");
                if (!writing) return "Folder deletion preview; no changes.";
                meta["mayHaveChanged"] = true;
                switch (target)
                {
                    case ScreenUserFolder s: s.Delete(); break;
                    case ScreenPopupUserFolder p: p.Delete(); break;
                    case ScreenTemplateUserFolder t: t.Delete(); break;
                    case TagUserFolder g: g.Delete(); break;
                    case VBScriptUserFolder v: v.Delete(); break;
                    default: throw new NotSupportedException("The system folder cannot be deleted.");
                }
                meta["apiCallSuccess"] = true;
                var parent2 = Resolve(parentPath);
                bool remains = parent2 switch
                {
                    ScreenFolder s => s.Folders.Find(leaf) != null, ScreenPopupFolder p => p.Folders.Find(leaf) != null, ScreenTemplateFolder t => t.Folders.Find(leaf) != null, TagFolder g => g.Folders.Find(leaf) != null, VBScriptFolder v => v.Folders.Find(leaf) != null, _ => false
                };
                if (remains) throw new InvalidOperationException("Folder remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Classic HMI user folder deleted and absence verified; project not saved.";
            });

        // ---- ManageClassicHmiGraphic (V21 GraphicsProvider) ----------------------------------------------------------------------------------
        public HmiMessage ManageClassicHmiGraphic(string softwarePath, string action = "list", string name = "", string filePath = "", string importOptions = "None", bool confirmDelete = false, bool dryRun = true, string exportOptions = "WithDefaults")
            => RunTool("ManageClassicHmiGraphic", meta => {
                var exportOption = (ExportOptions)Enum.Parse(typeof(ExportOptions), HmiExchangePolicy.ExportOption(exportOptions));
                meta["exportOptions"] = exportOptions;
                bool writing = Logic.ValidateGraphicRequest(action, name, filePath, importOptions, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                meta["softwarePath"] = softwarePath; meta["action"] = action; meta["name"] = name; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                MultiLingualGraphicComposition graphics = _session.ClassicGraphics();
                if (action == "list") { meta["records"] = new HmiArray(PlcGroupOperations.Items(graphics).Cast<MultiLingualGraphic>().Take(1000).Select(g => (object)new HmiObject { ["name"] = g.Name }).ToArray()); meta["count"] = graphics.Count; meta["apiCallSuccess"] = true; return "Multilingual graphics listed; image bytes are not read."; }
                if (action == "import")
                {
                    var source = HmiFiles.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = HmiFiles.FileRow(source);
                    var options = (ImportOptions)Enum.Parse(typeof(ImportOptions), importOptions);
                    HmiExchangePolicy.BeforeImport(graphics, filePath, "Hmi.Globalization.MultiLingualGraphic", options == ImportOptions.Override);
                    if (!writing) return "Graphics import preview; no changes.";
                    meta["mayHaveChanged"] = true; var imported = graphics.Import(source, options); meta["apiCallSuccess"] = true; meta["imported"] = ImportedNames(imported);
                    return "Multilingual graphics imported; project not saved.";
                }
                MultiLingualGraphic graphic = graphics.Find(name) ?? throw new PlcSoftwareException("NotFound", "Multilingual graphic not found: " + name);
                meta["graphic"] = new HmiObject { ["name"] = graphic.Name, ["graphicClass"] = graphic.GetType().Name };
                if (action == "read") return "Multilingual graphic read; export it for the image files.";
                if (action == "export")
                {
                    var file = PlcSourceFileOutput.Plan(filePath);
                    if (dryRun) return "Graphic export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true; graphic.Export(file, exportOption); meta["apiCallSuccess"] = true; meta["file"] = PlcSourceFileOutput.Verify(file);
                    return "Multilingual graphic exported (XML plus image files beside it); no project change.";
                }
                if (!writing) return "Graphic deletion preview; no changes.";
                meta["mayHaveChanged"] = true; graphic.Delete(); meta["apiCallSuccess"] = true;
                if (graphics.Find(name) != null) throw new InvalidOperationException("Graphic remains after Delete.");
                meta["verifiedAbsent"] = true;
                return "Multilingual graphic deleted and absence verified; project not saved.";
            });
        private HmiMessage RunTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
            => _session.RunHmiStepTool(tool, meta => _session.Capture(meta, action), requiresProject);
    }
}
