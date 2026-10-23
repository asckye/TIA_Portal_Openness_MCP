using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class ClassicHmiFoldersService : HmiPortService {
        public ClassicHmiFoldersService() { }
        internal ClassicHmiFoldersService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadClassicHmiScreenTree(string softwarePath, string kind = "all", string folderPath = "", int maxDepth = 4) => Invoke<ResponseMessage>(new HmiRequest { Operation = "GetClassicHmiScreenTree", SoftwarePath = softwarePath, Kind = kind, FolderPath = folderPath, MaxDepth = maxDepth, DryRun = true });
        public ResponseMessage ManageClassicHmiScreenObject(string softwarePath, string objectKind, string objectPath = "", string action = "read", string filePath = "", string importOptions = "None", bool confirmDelete = false, bool dryRun = true,
            string exportOptions = "WithDefaults") => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiScreenObject", SoftwarePath = softwarePath, ObjectKind = objectKind, ObjectPath = objectPath, Action = action, FilePath = filePath, ImportOptions = importOptions, ConfirmDelete = confirmDelete, DryRun = dryRun, ExportOptions = exportOptions });
        public ResponseMessage ManageClassicHmiFolder(string softwarePath, string folderKind, string folderPath = "", string action = "read", string newName = "", bool confirmDelete = false, bool dryRun = true) => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiFolder", SoftwarePath = softwarePath, FolderKind = folderKind, FolderPath = folderPath, Action = action, NewName = newName, ConfirmDelete = confirmDelete, DryRun = dryRun });
        public ResponseMessage ManageClassicHmiGraphic(string softwarePath, string action = "list", string name = "", string filePath = "", string importOptions = "None", bool confirmDelete = false, bool dryRun = true,
            string exportOptions = "WithDefaults") => Invoke<ResponseMessage>(new HmiRequest { Operation = "ManageClassicHmiGraphic", SoftwarePath = softwarePath, Action = action, Name = name, FilePath = filePath, ImportOptions = importOptions, ConfirmDelete = confirmDelete, DryRun = dryRun, ExportOptions = exportOptions });
    }
}
