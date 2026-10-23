using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class MotionProDiagClassicHmiService : HmiPortService {
        public MotionProDiagClassicHmiService() { }
        internal MotionProDiagClassicHmiService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadClassicHmiScripts(string softwarePath, string folderPath = "", int offset = 0, int limit = 100) => Invoke<ResponseMessage>(new HmiRequest { Operation = "ListClassicHmiScripts", SoftwarePath = softwarePath, FolderPath = folderPath, Offset = offset, Limit = limit, DryRun = true });
        public ResponseMessage ManageClassicHmiScript(string softwarePath, string scriptPath, string action, string filePath = "", string importOptions = "None",
            string attributesJson = "{}", bool confirmDelete = false, bool dryRun = true,
            string exportOptions = "WithDefaults") => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiScript", SoftwarePath = softwarePath, ScriptPath = scriptPath, Action = action, FilePath = filePath, ImportOptions = importOptions, AttributesJson = attributesJson, ConfirmDelete = confirmDelete, DryRun = dryRun, ExportOptions = exportOptions });
        public ResponseMessage ManageClassicHmiCycle(string softwarePath, string action, string cycleName = "", string filePath = "", string importOptions = "None",
            string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true,
            string exportOptions = "WithDefaults") => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiCycle", SoftwarePath = softwarePath, Action = action, CycleName = cycleName, FilePath = filePath, ImportOptions = importOptions, AttributesJson = attributesJson, Offset = offset, Limit = limit, ConfirmDelete = confirmDelete, DryRun = dryRun, ExportOptions = exportOptions });
        public ResponseMessage ManageClassicHmiTextGraphicList(string softwarePath, string listKind, string action, string listName = "", string compositionName = "", string entryName = "",
            string typeName = "", string filePath = "", string importOptions = "None", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true,
            string exportOptions = "WithDefaults") => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiTextGraphicList", SoftwarePath = softwarePath, ListKind = listKind, Action = action, ListName = listName, CompositionName = compositionName, EntryName = entryName, TypeName = typeName, FilePath = filePath, ImportOptions = importOptions, AttributesJson = attributesJson, Offset = offset, Limit = limit, ConfirmDelete = confirmDelete, DryRun = dryRun, ExportOptions = exportOptions });
        public ResponseMessage ReadClassicHmiGlobalization(string softwarePath, int offset = 0, int limit = 100) => Invoke<ResponseMessage>(new HmiRequest { Operation = "GetClassicHmiGlobalization", SoftwarePath = softwarePath, Offset = offset, Limit = limit, DryRun = true });
        public ResponseMessage ReadClassicHmiFaceplates(string kind = "faceplate", string libraryName = "", string folderPath = "", int offset = 0, int limit = 100) => Invoke<ResponseMessage>(new HmiRequest { Operation = "ListClassicHmiFaceplates", Kind = kind, LibraryName = libraryName, FolderPath = folderPath, Offset = offset, Limit = limit, DryRun = true });
    }
}
