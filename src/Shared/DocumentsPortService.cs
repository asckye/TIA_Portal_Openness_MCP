using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DocumentsService : PlcB5PortService
    {
        public DocumentsService() { }
        internal DocumentsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public IReadOnlyList<string> LastExportAsDocumentsFailures { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> LastImportFromDocumentsFailures { get; private set; } = Array.Empty<string>();
        public string[] LastImportedDocumentBlocks { get; private set; } = Array.Empty<string>();
        public int LastImportFromDocumentsScanned { get; private set; }
        public int LastImportFromDocumentsSelected { get; private set; }
        public int LastImportFromDocumentsAttempted { get; private set; }
        public int LastImportFromDocumentsSucceeded { get; private set; }
        public bool LastImportFromDocumentsStopped { get; private set; }
        protected override void ObserveReply(JsonObject reply)
        {
            base.ObserveReply(reply);
            LastExportAsDocumentsFailures = JsonSerializer.Deserialize<string[]>(reply["ExportFailures"]!.ToJsonString())!;
            LastImportFromDocumentsFailures = JsonSerializer.Deserialize<string[]>(reply["ImportFailures"]!.ToJsonString())!;
            LastImportedDocumentBlocks = JsonSerializer.Deserialize<string[]>(reply["ImportedNames"]!.ToJsonString())!;
            LastImportFromDocumentsScanned = reply["Scanned"]!.GetValue<int>(); LastImportFromDocumentsSelected = reply["Selected"]!.GetValue<int>();
            LastImportFromDocumentsAttempted = reply["Attempted"]!.GetValue<int>(); LastImportFromDocumentsSucceeded = reply["Succeeded"]!.GetValue<int>();
            LastImportFromDocumentsStopped = reply["Stopped"]!.GetValue<bool>();
        }
        public List<PlcB5Block>? GetBlocks(string softwarePath, string regexName)
        {
            var count = (int?)Invoke(new PlcB5Request { Operation = "ExportPlcBlocksDocuments", Action = "count", SoftwarePath = softwarePath, RegexName = regexName })["Count"];
            return count.HasValue ? Enumerable.Range(0, count.Value).Select(_ => new PlcB5Block()).ToList() : null;
        }
        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
            => Invoke(new PlcB5Request { Operation = "ExportPlcBlockDocuments", SoftwarePath = softwarePath, BlockPath = blockPath, ExportPath = exportPath, PreservePath = preservePath, DryRun = false })["Value"]!.GetValue<bool>();
        public IEnumerable<PlcB5Block>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName, bool preservePath = false)
            => Blocks(Invoke(new PlcB5Request { Operation = "ExportPlcBlocksDocuments", SoftwarePath = softwarePath, ExportPath = exportPath, RegexName = regexName, PreservePath = preservePath, DryRun = false }));
        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, string option)
            => Invoke(new PlcB5Request { Operation = "ImportPlcBlockDocuments", SoftwarePath = softwarePath, GroupPath = groupPath, ImportPath = importPath, FileNameWithoutExtension = fileNameWithoutExtension, ImportOption = option, DryRun = false })["Value"]!.GetValue<bool>();
        public bool VerifyLastDocumentImport(string softwarePath, string groupPath)
            => Invoke(new PlcB5Request { Operation = "ImportPlcBlockDocuments", Action = "verify", ImportedNames = LastImportedDocumentBlocks, SoftwarePath = softwarePath, GroupPath = groupPath })["Value"]!.GetValue<bool>();
        public IEnumerable<PlcB5Block>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, string option)
            => Blocks(Invoke(new PlcB5Request { Operation = "ImportPlcBlocksDocuments", SoftwarePath = softwarePath, GroupPath = groupPath, ImportPath = importPath, RegexName = regexName, ImportOption = option, DryRun = false }));
        private static PlcB5Block[]? Blocks(JsonObject reply) => reply["Blocks"] == null ? null : JsonSerializer.Deserialize<PlcB5Block[]>(reply["Blocks"]!.ToJsonString());
    }
}
