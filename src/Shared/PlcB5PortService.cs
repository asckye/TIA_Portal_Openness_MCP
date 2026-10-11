using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal partial class PlcB5PortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<string> identity;
        internal PlcB5PortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) { this.call = call; this.identity = identity; }
        public PlcB5PortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
        protected JsonObject Invoke(PlcB5Request request)
        {
            var unavailable = PlcB5Capabilities.Unsupported(HardwareContract.ReleaseKey, request);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            Prepare(request);
            var args = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { args["confirm"] = true; args["expectedProjectFile"] = identity(); }
            var reply = call(PortedFamilies.ForTool(request.Operation).OperationPrefix + ".ExecutePlcB5", args) as JsonObject ?? throw new InvalidOperationException("Missing PLC B5 reply.");
            ObserveReply(reply);
            if (reply["Failure"] is JsonObject error)
            {
                string code = (string?)error["Code"] ?? "", message = (string?)error["Message"] ?? "";
                if (Enum.TryParse<PortalErrorCode>(code, out var native)) throw new PortalException(native, message);
                if (code == nameof(ArgumentException)) throw new ArgumentException(message);
                if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
                throw new InvalidOperationException(message);
            }
            return reply;
        }
        protected virtual void ObserveReply(JsonObject reply)
        {
            if (reply["Events"] is not JsonArray events) return;
            foreach (var item in events.OfType<JsonObject>())
                switch ((string?)item["Kind"])
                {
                    case "start": PlcExchangeContract.StartWrite((string?)item["Stage"] ?? ""); break;
                    case "changed": PlcExchangeContract.Changed(); break;
                    case "confirm": PlcExchangeContract.ConfirmWrite(); break;
                    case "field": PlcExchangeContract.Observe((string?)item["Stage"] ?? "", item["Value"]); break;
                    case "native": PlcExchangeContract.ObserveNativeResult((string?)item["State"], (bool?)item["ChangesProject"] == true, item["Value"], (string?)item["Directory"] ?? "", (string?)item["Name"] ?? ""); break;
                }
        }
        public JsonObject GenerateOpcUaModelledInterface(string softwarePath, string interfaceName, string namespaceUri, string outputPath, string unitName, bool keepFolderStructure, bool keepEmptyDataBlocks, string accessLevelsJson, bool dryRun)
            => (JsonObject)Invoke(new PlcB5Request { Operation = "GenerateOpcUaModelledInterface", SoftwarePath = softwarePath, InterfaceName = interfaceName, NamespaceUri = namespaceUri, OutputPath = outputPath, UnitName = unitName, KeepFolderStructure = keepFolderStructure, KeepEmptyDataBlocks = keepEmptyDataBlocks, AccessLevelsJson = accessLevelsJson, DryRun = dryRun })["Data"]!.DeepClone();
        private static ResponseMessage Message(JsonObject reply) => new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject?)reply["Meta"]?.DeepClone() };
        private static ResponseJsonReport Report(JsonObject reply) => new ResponseJsonReport { Ok = (bool?)reply["Ok"] ?? false, Message = (string?)reply["Message"] ?? "", Data = (JsonObject?)reply["Data"]?.DeepClone(), Meta = (JsonObject?)reply["Meta"]?.DeepClone() };
        public ResponseMessage ExportAlarmClasses(string softwarePath, string exportPath)
            => Message(Invoke(new PlcB5Request { Operation = "ExportAlarmClasses", SoftwarePath = softwarePath, ExportPath = exportPath }));
        public ResponseMessage ImportAlarmClasses(string softwarePath, string importPath)
            => Message(Invoke(new PlcB5Request { Operation = "ImportAlarmClasses", SoftwarePath = softwarePath, ImportPath = importPath, DryRun = false }));
        public ResponseMessage ExportAlarmTextLists(string softwarePath, string exportPath)
            => Message(Invoke(new PlcB5Request { Operation = "ExportAlarmTextLists", SoftwarePath = softwarePath, ExportPath = exportPath }));
        public ResponseMessage ImportAlarmTextLists(string softwarePath, string importPath)
            => Message(Invoke(new PlcB5Request { Operation = "ImportAlarmTextLists", SoftwarePath = softwarePath, ImportPath = importPath, DryRun = false }));
        public ResponseMessage ExportAlarmInstanceTexts(string softwarePath, string exportPath, bool includeInfoText = true, bool includeAdditionalTexts = true, bool includeAlarmClass = true)
            => Message(Invoke(new PlcB5Request { Operation = "ExportAlarmInstanceTexts", SoftwarePath = softwarePath, ExportPath = exportPath, IncludeInfoText = includeInfoText, IncludeAdditionalTexts = includeAdditionalTexts, IncludeAlarmClass = includeAlarmClass }));
        public ResponseMessage ExchangePlcAlarmTextListsXlsx(string softwarePath, string action, string filePath, string unitName = "", string unitKind = "unit", string textListNamesJson = "[]", string culturesJson = "[]", string importOption = "None", bool confirmImport = false, bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ExchangePlcAlarmTextLists", SoftwarePath = softwarePath, Action = action, FilePath = filePath, UnitName = unitName, UnitKind = unitKind, TextListNamesJson = textListNamesJson, CulturesJson = culturesJson, ImportOption = importOption, ConfirmImport = confirmImport, DryRun = dryRun }));
        public ResponseMessage ImportPlcAlarmInstanceTexts(string softwarePath, string filePath, string culturesJson, bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ImportPlcAlarmInstanceTexts", SoftwarePath = softwarePath, FilePath = filePath, CulturesJson = culturesJson, DryRun = dryRun }));
        public ResponseMessage ManagePlcAlarmTextList(string softwarePath, string action = "read", string name = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", bool confirmDelete = false, int offset = 0, int limit = 100, bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ManagePlcAlarmTextList", SoftwarePath = softwarePath, Action = action, Name = name, LibraryName = libraryName, MasterCopyPath = masterCopyPath, CopyMode = copyMode, ConfirmDelete = confirmDelete, Offset = offset, Limit = limit, DryRun = dryRun }));
        public ResponseJsonReport GetOpcUaConfig(string softwarePath)
            => Report(Invoke(new PlcB5Request { Operation = "GetPlcOpcUaConfiguration", SoftwarePath = softwarePath }));
        public ResponseMessage ManageOpcUaInterface(string softwarePath, string interfaceName, string action = "read", string interfaceType = "ServerInterface", bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ManageOpcUaInterface", SoftwarePath = softwarePath, InterfaceName = interfaceName, Action = action, InterfaceType = interfaceType, DryRun = dryRun }));
        public ResponseMessage SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled, string interfaceType = "ServerInterface")
            => Message(Invoke(new PlcB5Request { Operation = "SetOpcUaInterfaceEnabled", SoftwarePath = softwarePath, InterfaceName = interfaceName, Enabled = enabled, InterfaceType = interfaceType, DryRun = false }));
        public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
            => Message(Invoke(new PlcB5Request { Operation = "ExportOpcUaInterface", SoftwarePath = softwarePath, InterfaceName = interfaceName, ExportPath = exportPath, InterfaceType = interfaceType }));
        public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
            => Message(Invoke(new PlcB5Request { Operation = "ImportOpcUaInterface", SoftwarePath = softwarePath, ImportPath = importPath, InterfaceType = interfaceType, DryRun = false }));
        public ResponseMessage ReadOpcUaAccessControl(string softwarePath, string section = "roles", int offset = 0, int limit = 100)
            => Message(Invoke(new PlcB5Request { Operation = "GetOpcUaAccessControl", SoftwarePath = softwarePath, Section = section, Offset = offset, Limit = limit }));
        public ResponseMessage ManageOpcUaAccessControl(string softwarePath, string action, string roleName = "", string definedInNamespace = "", string projectRole = "", string namespaceUri = "", string permission = "", bool enabled = false, string propertiesJson = "{}", bool confirmChange = false, bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ManageOpcUaAccessControl", SoftwarePath = softwarePath, Action = action, RoleName = roleName, DefinedInNamespace = definedInNamespace, ProjectRole = projectRole, NamespaceUri = namespaceUri, Permission = permission, Enabled = enabled, PropertiesJson = propertiesJson, ConfirmChange = confirmChange, DryRun = dryRun }));
        public ResponseMessage ReadPlcBlockScopes(string softwarePath, int offset = 0, int limit = 100)
            => Message(Invoke(new PlcB5Request { Operation = "GetPlcBlockScopes", SoftwarePath = softwarePath, Offset = offset, Limit = limit }));
        public ResponseMessage ManagePlcBlockDocuments(string softwarePath, string action, string name = "", string groupPath = "", string unitName = "", string unitKind = "unit", string directoryPath = "", string importOption = "Override", bool dryRun = true, bool verifyDocumentReadback = false)
            => Message(Invoke(new PlcB5Request { Operation = "ManagePlcBlockDocuments", SoftwarePath = softwarePath, Action = action, Name = name, GroupPath = groupPath, UnitName = unitName, UnitKind = unitKind, DirectoryPath = directoryPath, ImportOption = importOption, DryRun = dryRun, VerifyDocumentReadback = verifyDocumentReadback }));
        public ResponseMessage ManagePlcDocuments(string softwarePath, string action, string objectKind = "document", string name = "", string unitName = "", string unitKind = "unit", string groupPath = "", string directoryPath = "", string importOption = "Override", string libraryName = "", string masterCopyPath = "", string copyMode = "", string typePath = "", string version = "", string updatePathsMode = "", bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ManagePlcDocuments", SoftwarePath = softwarePath, Action = action, ObjectKind = objectKind, Name = name, UnitName = unitName, UnitKind = unitKind, GroupPath = groupPath, DirectoryPath = directoryPath, ImportOption = importOption, LibraryName = libraryName, MasterCopyPath = masterCopyPath, CopyMode = copyMode, TypePath = typePath, Version = version, UpdatePathsMode = updatePathsMode, DryRun = dryRun }));
        public ResponseMessage ManagePlcSupervision(string softwarePath, string action, string blockPath = "", string providerKind = "supervision", string compositionName = "", string entryName = "", string typeName = "", string filePath = "", string attributesJson = "{}", int offset = 0, int limit = 100, bool confirmDelete = false, bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ManagePlcSupervision", SoftwarePath = softwarePath, Action = action, BlockPath = blockPath, ProviderKind = providerKind, CompositionName = compositionName, EntryName = entryName, TypeName = typeName, FilePath = filePath, AttributesJson = attributesJson, Offset = offset, Limit = limit, ConfirmDelete = confirmDelete, DryRun = dryRun }));
        public ResponseMessage ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName = "", string unitKind = "unit", bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ExportPlcProDiagInfo", SoftwarePath = softwarePath, BlockPath = blockPath, DirectoryPath = directoryPath, UnitName = unitName, UnitKind = unitKind, DryRun = dryRun }));
        public ResponseMessage ExchangePlcSupervisions(string softwarePath, string action, string filePath, string importOptions = "None", bool dryRun = true)
            => Message(Invoke(new PlcB5Request { Operation = "ExchangePlcSupervisions", SoftwarePath = softwarePath, Action = action, FilePath = filePath, ImportOptions = importOptions, DryRun = dryRun }));
    }
}
