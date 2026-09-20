using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageProjectLanguage(string action="read", string culture="", bool dryRun=true)
            => RunHmiStepTool("ManageProjectLanguage", meta => {
                if (!new[] { "read", "activate", "deactivate", "setEditing", "setReference" }.Contains(action)) throw new ArgumentException("action must be one of: read/activate/deactivate/setEditing/setReference (case-sensitive).");
                var settings=_project!.LanguageSettings;
                JsonObject Snapshot() => new JsonObject {
                    ["activeCultures"]=new JsonArray(settings.ActiveLanguages.Select(x=>(JsonNode)JsonValue.Create(x.Culture.Name)!).ToArray()),
                    ["editingCulture"]=settings.EditingLanguage?.Culture.Name,
                    ["referenceCulture"]=settings.ReferenceLanguage?.Culture.Name };
                meta["before"]=Snapshot(); meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(action=="read") return "Project active, editing and reference languages read.";
                var selected=settings.Languages.Find(CultureInfo.GetCultureInfo(culture)) ?? throw new NotSupportedException("Language not supported by this project.");
                bool active=settings.ActiveLanguages.Any(x=>x.Culture.Name==selected.Culture.Name);
                if((action=="setEditing" || action=="setReference") && !active) throw new InvalidOperationException("Activate language explicitly before selecting it.");
                if(action=="deactivate" && (settings.EditingLanguage?.Culture.Name==selected.Culture.Name || settings.ReferenceLanguage?.Culture.Name==selected.Culture.Name)) throw new InvalidOperationException("Cannot deactivate current editing/reference language.");
                if(dryRun) return "Project language preview; no changes.";
                using var access=AcquireHmiEditAccess(); meta["mayHaveChanged"]=true;
                switch(action) {
                    case "activate": if(!active) settings.ActiveLanguages.Add(selected); break;
                    case "deactivate": if(active) settings.ActiveLanguages.Remove(selected); break;
                    case "setEditing": settings.EditingLanguage=selected; break;
                    case "setReference": settings.ReferenceLanguage=selected; break;
                }
                var after=Snapshot(); meta["after"]=after;
                bool nowActive=settings.ActiveLanguages.Any(x=>x.Culture.Name==selected.Culture.Name);
                bool verified=action=="activate" ? nowActive : action=="deactivate" ? !nowActive : action=="setEditing" ? settings.EditingLanguage?.Culture.Name==selected.Culture.Name : settings.ReferenceLanguage?.Culture.Name==selected.Culture.Name;
                if(!verified) throw new InvalidOperationException("Project language readback differs.");
                return "Project language changed and verified; no save/compile/download.";
            });
        public ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true)
            => RunHmiStepTool("RetrieveProjectArchive", meta => {
                if (_portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (_project != null || _session != null || _portal.Projects.Any() || _portal.LocalSessions.Any()) throw new InvalidOperationException("Use a connected Portal with no open project/session. Existing projects will never be closed automatically.");
                var file = new FileInfo(archivePath); if (!file.Exists) throw new FileNotFoundException("Archive not found.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destination required.");
                var directory = new DirectoryInfo(destinationDirectory); if (directory.Exists) throw new IOException("Destination must be new; merge/overwrite refused.");
                meta["dryRun"] = dryRun; meta["upgrade"] = upgrade; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName;
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    _project = upgrade ? _portal.Projects.RetrieveWithUpgrade(file, directory) : _portal.Projects.Retrieve(file, directory);
                    if (_project == null) throw new InvalidOperationException("Retrieve returned no project; inspect destination for partial files.");
                    InvalidateHmiSoftwareCache(); ResetHmiReadHealth();
                    meta["project"] = EngineeringScalarProperties.Read(_project); meta["openedProject"] = true;
                }
                return dryRun ? "Archive retrieval preview; no project opened or files created." : "Archive retrieved and returned project bound. No compile/download; native retrieval may upgrade only when upgrade=true.";
            }, requiresProject: false);
        public ResponseMessage ExportProjectTexts(string filePath, string sourceCulture, string targetCulture, bool dryRun = true)
            => RunHmiStepTool("ExportProjectTexts", meta => {
                var file = NativeFileOutput.Plan(filePath); var source = CultureInfo.GetCultureInfo(sourceCulture); var target = CultureInfo.GetCultureInfo(targetCulture);
                var signature = new[] { typeof(FileInfo), typeof(CultureInfo), typeof(CultureInfo) };
                if (_project!.GetType().GetMethod("ExportProjectTexts", signature) == null) throw new NotSupportedException("Native culture-pair text export unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["sourceCulture"] = source.Name; meta["targetCulture"] = target.Name;
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    EngineeringGroupOperations.Call(_project, "ExportProjectTexts", signature, file, source, target);
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                    meta["file"] = NativeFileOutput.Verify(file);
                }
                return dryRun ? "Project text export preview." : "Native project text file exported and hashed; no project modification.";
            });
        public ResponseMessage ImportProjectTexts(string filePath, bool updateSourceLanguage, bool dryRun = true)
            => RunHmiStepTool("ImportProjectTexts", meta => {
                var file = new FileInfo(filePath); if (!file.Exists) throw new FileNotFoundException("Text import file not found.");
                var signature = new[] { typeof(FileInfo), typeof(bool) };
                if (_project!.GetType().GetMethod("ImportProjectTexts", signature) == null) throw new NotSupportedException("Native project text import unavailable.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["filePath"] = file.FullName; meta["updateSourceLanguage"] = updateSourceLanguage;
                if (!dryRun) {
                    meta["mayHaveChanged"] = true;
                    var result = EngineeringGroupOperations.Call(_project, "ImportProjectTexts", signature, file, updateSourceLanguage);
                    meta["nativeResult"] = EngineeringScalarProperties.Read(result); meta["nativeState"] = EngineeringGroupOperations.Get(result, "State").ToString();
                    meta["apiCallSuccess"] = true; meta["operationSuccess"] = meta["nativeState"]!.GetValue<string>() == "Info"; meta["dataComplete"] = false;
                }
                return dryRun ? "Native project text import preview; file contents not applied." : "Native import completed; translated text changes require separate export/readback verification. No explicit save/compile/download.";
            });
    }
}
