using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class HmiExchangeService : HmiPortService {
        public HmiExchangeService() { }
        internal HmiExchangeService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public List<string>? GetHmiScreens(string softwarePath) => Invoke<List<string>?>(new HmiRequest { Operation = "ListHmiScreens", SoftwarePath = softwarePath, DryRun = true });
        public List<string>? GetHmiTagTables(string softwarePath) => Invoke<List<string>?>(new HmiRequest { Operation = "ListHmiTagTables", SoftwarePath = softwarePath, DryRun = true });
        public List<string>? GetHmiTags(string softwarePath, string tagTableName = "") => Invoke<List<string>?>(new HmiRequest { Operation = "ListHmiTags", SoftwarePath = softwarePath, TagTableName = tagTableName, DryRun = true });
        public List<string>? GetHmiConnections(string softwarePath) => Invoke<List<string>?>(new HmiRequest { Operation = "ListHmiConnections", SoftwarePath = softwarePath, DryRun = true });
        public JsonObject ExportHmiScreen(string softwarePath, string screenName, string exportPath, string exportOptions = "WithDefaults") => Invoke<JsonObject>(new HmiRequest { Operation = "ExportHmiScreen", SoftwarePath = softwarePath, ScreenName = screenName, ExportPath = exportPath, ExportOptions = exportOptions, DryRun = true });
        public JsonObject ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath, string exportOptions = "WithDefaults") => Invoke<JsonObject>(new HmiRequest { Operation = "ExportHmiTagTable", SoftwarePath = softwarePath, TagTableName = tagTableName, ExportPath = exportPath, ExportOptions = exportOptions, DryRun = true });
        public JsonObject ExportHmiConnection(string softwarePath, string connectionName, string exportPath, string exportOptions = "WithDefaults") => Invoke<JsonObject>(new HmiRequest { Operation = "ExportHmiConnection", SoftwarePath = softwarePath, ConnectionName = connectionName, ExportPath = exportPath, ExportOptions = exportOptions, DryRun = true });
        public (List<string> Exported, List<string> Failed, JsonArray Items)? ExportHmiProgram(string softwarePath, string exportDir, bool exportScreens = true, bool exportTagTables = true, string exportOptions = "WithDefaults") { var value = Invoke<HmiExportProgram?>(new HmiRequest { Operation = "ExportHmiProgram", SoftwarePath = softwarePath, ExportDir = exportDir, ExportScreens = exportScreens, ExportTagTables = exportTagTables, ExportOptions = exportOptions, DryRun = true }); return value == null ? null : (value.Exported, value.Failed, JsonSerializer.SerializeToNode(value.Items)!.AsArray()); }
        public void ImportHmiScreen(string softwarePath, string folderPath, string importPath, bool overwrite = false) => Invoke<object?>(new HmiRequest { Operation = "ImportHmiScreen", SoftwarePath = softwarePath, FolderPath = folderPath, ImportPath = importPath, Overwrite = overwrite, DryRun = false });
        public void ImportHmiTagTable(string softwarePath, string folderPath, string importPath, bool overwrite = false) => Invoke<object?>(new HmiRequest { Operation = "ImportHmiTagTable", SoftwarePath = softwarePath, FolderPath = folderPath, ImportPath = importPath, Overwrite = overwrite, DryRun = false });
        public void ImportHmiConnection(string softwarePath, string importPath, bool overwrite = false) => Invoke<object?>(new HmiRequest { Operation = "ImportHmiConnection", SoftwarePath = softwarePath, ImportPath = importPath, Overwrite = overwrite, DryRun = false });
        public ResponseImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = false) => Invoke<ResponseImportBatch>(new HmiRequest { Operation = "ImportHmiScreensFromDirectory", SoftwarePath = softwarePath, FolderPath = folderPath, Dir = dir, RegexName = regexName, Overwrite = overwrite, DryRun = false });
        public ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = false) => Invoke<ResponseImportBatch>(new HmiRequest { Operation = "ImportHmiTagTablesFromDirectory", SoftwarePath = softwarePath, FolderPath = folderPath, Dir = dir, RegexName = regexName, Overwrite = overwrite, DryRun = false });
        public string? LastImportNotes => LastNotes;
    }
}
