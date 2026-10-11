using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class LibraryPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<string> identity;
        public LibraryPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
        internal LibraryPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) { this.call = call; this.identity = identity; }
        private JsonObject Invoke(LibraryRequest request)
        {
            var unavailable = LibraryCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Action, request.SourceKind, request.Properties.Keys, request.ForceUpdateMode, request.DeleteUnusedVersionsMode, request.StructureConflictResolutionMode);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
            return call("libraries.ExecuteLibrary", arguments) as JsonObject ?? throw new InvalidOperationException("Missing library worker reply.");
        }
        private static Dictionary<string, HardwareScalar> Properties(string json)
            => HardwareAddressingService.Scalars(json);
        public ResponseMessage ManageLibraryTypeVersion(string typePath, string version, string action, string libraryName = "",
            string newVersion = "", string dependenciesMode = "", string author = "", string comment = "", string targetSoftwarePath = "", bool dryRun = true)
        {
            var request = new LibraryRequest { Operation = "ManageLibraryTypeVersion", TypePath = typePath, Version = version, Action = action, LibraryName = libraryName, NewVersion = newVersion, DependenciesMode = dependenciesMode, Author = author, Comment = comment, TargetSoftwarePath = targetSoftwarePath, DryRun = dryRun };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage CreateLibraryMasterCopy(string sourceKind, string sourcePath, string softwarePath = "", string folderPath = "", string libraryName = "", bool dryRun = true)
        {
            var request = new LibraryRequest { Operation = "CreateLibraryMasterCopy", SourceKind = sourceKind, SourcePath = sourcePath, SoftwarePath = softwarePath, FolderPath = folderPath, LibraryName = libraryName, DryRun = dryRun };
            if (sourceKind == "device") { try { request.SourceDevicePath = JsonSerializer.Deserialize<string[]>(sourcePath)!; } catch (Exception error) { request.ValidationError = error.Message; } }
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ReadLibraryOverview(string libraryName = "", bool includeTypes = true, bool includeMasterCopies = true, int maxDepth = 6, int maxItems = 500)
        {
            var request = new LibraryRequest { Operation = "ReadLibraryOverview", LibraryName = libraryName, IncludeTypes = includeTypes, IncludeMasterCopies = includeMasterCopies, MaxDepth = maxDepth, MaxItems = maxItems, DryRun = true };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ReadLibraryType(string libraryName = "", string typePath = "", string guid = "", int offset = 0, int limit = 100)
        {
            var request = new LibraryRequest { Operation = "ReadLibraryType", LibraryName = libraryName, TypePath = typePath, Guid = guid, Offset = offset, Limit = limit, DryRun = true };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage CheckLibraryUpdates(string libraryName = "", string updateCheckMode = "ReportOutOfDateOnly", int maxItems = 2000)
        {
            var request = new LibraryRequest { Operation = "CheckLibraryUpdates", LibraryName = libraryName, UpdateCheckMode = updateCheckMode, MaxItems = maxItems, DryRun = true };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ManageLibraryType(string typePath, string action, string libraryName = "", string propertiesJson = "{}", string targetLibraryName = "", string scopeSoftwarePathsJson = "[]",
            string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure", string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", bool confirmDelete = false, bool dryRun = true)
        {
            var request = new LibraryRequest { Operation = "ManageLibraryType", TypePath = typePath, Action = action, LibraryName = libraryName, TargetLibraryName = targetLibraryName, DeleteUnusedVersionsMode = deleteUnusedVersionsMode, StructureConflictResolutionMode = structureConflictResolutionMode, ForceUpdateMode = forceUpdateMode, ConfirmDelete = confirmDelete, DryRun = dryRun };
            try {
                request.Properties = Properties(propertiesJson);
                request.ScopeSoftwarePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
            } catch (Exception error) { request.ValidationError = error.Message; }
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage SynchronizeLibrary(string action, string selectionJson, string libraryName = "", string targetLibraryName = "", string scopeSoftwarePathsJson = "[]",
            string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure",
            string harmonizeOptionsJson = "[\"HarmonizeNames\",\"HarmonizePaths\"]", string cleanUpMode = "PreserveDefaultVersionOfUnusedTypes", bool confirmChange = false, bool dryRun = true)
        {
            var request = new LibraryRequest { Operation = "SynchronizeLibrary", Action = action, LibraryName = libraryName, TargetLibraryName = targetLibraryName, ForceUpdateMode = forceUpdateMode, DeleteUnusedVersionsMode = deleteUnusedVersionsMode, StructureConflictResolutionMode = structureConflictResolutionMode, CleanUpMode = cleanUpMode, ConfirmChange = confirmChange, DryRun = dryRun };
            try {
                request.Selection = LibraryDeepLogic.ParseSelection(selectionJson).Select(s => new LibrarySelection { IsFolder = s.IsFolder, Path = s.Path }).ToArray();
                request.ScopeSoftwarePaths = LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson);
                if (action == "harmonizeProject") request.HarmonizeOptions = HardwareNetworkLogic.ParseNames(harmonizeOptionsJson, "harmonizeOptionsJson", 2);
            } catch (Exception error) { request.ValidationError = error.Message; }
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage CompareLibraryObjects(string kind, string leftPath, string rightPath, string leftLibraryName = "", string rightLibraryName = "", string leftVersion = "", string rightVersion = "",
            bool includeIdentical = false, int offset = 0, int limit = 100)
        {
            var request = new LibraryRequest { Operation = "CompareLibraryObjects", Kind = kind, LeftPath = leftPath, RightPath = rightPath, LeftLibraryName = leftLibraryName, RightLibraryName = rightLibraryName, LeftVersion = leftVersion, RightVersion = rightVersion, IncludeIdentical = includeIdentical, Offset = offset, Limit = limit, DryRun = true };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ManageLibraryMasterCopy(string sourcePath,string action,string libraryName="",string destinationLibraryName="",string destinationPath="",bool dryRun=true)
        {
            var request = new LibraryRequest { Operation = "ManageLibraryMasterCopy", SourcePath = sourcePath, Action = action, LibraryName = libraryName, DestinationLibraryName = destinationLibraryName, DestinationPath = destinationPath, DryRun = dryRun };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ImportLibraryTypeDocuments(string filePath,string folderPath="",string libraryName="",string importOptions="None",string typePath="",string createOptions="None",string targetSoftwarePath="",string targetGroupKind="",string targetGroupPath="",bool dryRun=true)
        {
            var request = new LibraryRequest { Operation = "ImportLibraryTypeDocuments", FilePath = filePath, FolderPath = folderPath, LibraryName = libraryName, ImportOptions = importOptions, TypePath = typePath, CreateOptions = createOptions, TargetSoftwarePath = targetSoftwarePath, TargetGroupKind = targetGroupKind, TargetGroupPath = targetGroupPath, DryRun = dryRun };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false,
            string archiveName="", string archiveMode="Compressed", bool dryRun=true)
        {
            var request = new LibraryRequest { Operation = "ManageGlobalLibrary", Action = action, LibraryName = libraryName, FilePath = filePath, DestinationDirectory = destinationDirectory, OpenMode = openMode, Upgrade = upgrade, ArchiveName = archiveName, ArchiveMode = archiveMode, DryRun = dryRun };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
        {
            var request = new LibraryRequest { Operation = "ManageLibraryFolder", FolderKind = folderKind, FolderPath = folderPath, Action = action, LibraryName = libraryName, NewName = newName, DryRun = dryRun };
            var reply = Invoke(request)["Step"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseGlobalLibraryProbe ProbeGlobalLibrary(string libraryPath, int maxItems = 500)
        {
            var request = new LibraryRequest { Operation = "ProbeGlobalLibrary", LibraryPath = libraryPath, MaxItems = maxItems, DryRun = true };
            var reply = Invoke(request)["Probe"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return JsonSerializer.Deserialize<ResponseGlobalLibraryProbe>(reply.ToJsonString())!;
        }
        public ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
        {
            var request = new LibraryRequest { Operation = "ImportMasterCopyFromGlobalLibrary", LibraryPath = libraryPath, MasterCopyName = masterCopyName, HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ImportedItemName = importedItemName, Left = left, Top = top, DryRun = false };
            var reply = Invoke(request)["Import"] as JsonObject ?? throw new InvalidOperationException("Missing library evidence.");
            return JsonSerializer.Deserialize<ResponseGlobalLibraryImport>(reply.ToJsonString())!;
        }
    }
}
