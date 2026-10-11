using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class EngineeringServicesPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<string> identity;
        internal EngineeringServicesPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
        { this.call = call; this.identity = identity; }
        public EngineeringServicesPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
        internal IEngineeringTransaction BeginTransaction(string text)
        {
            var arguments = new JsonObject { ["text"] = text, ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = identity() };
            string id = call("engineering-services.BeginEngineeringTransaction", arguments)?.GetValue<string>()
                ?? throw new InvalidOperationException("Missing worker transaction scope.");
            return new WorkerTransaction(this, id);
        }
        private sealed class WorkerTransaction : IEngineeringTransaction
        {
            private readonly EngineeringServicesPortService owner; private readonly string id; private bool disposed;
            internal WorkerTransaction(EngineeringServicesPortService owner, string id) { this.owner = owner; this.id = id; }
            private bool Read(string member) => owner.call("engineering-services.ReadEngineeringTransaction", new JsonObject { ["id"] = id, ["member"] = member })!.GetValue<bool>();
            public bool CanCommit => Read("CanCommit");
            public bool CommitRequested => Read("CommitRequested");
            public bool IsCancellationRequested => Read("IsCancellationRequested");
            public void Commit() => owner.call("engineering-services.CommitEngineeringTransaction", new JsonObject { ["id"] = id, ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = owner.identity() });
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                owner.call("engineering-services.DisposeEngineeringTransaction", new JsonObject { ["id"] = id });
            }
        }
        private ResponseMessage Invoke(EngineeringServicesRequest request)
        {
            var unavailable = EngineeringServicesCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Kind, request.CustomIdentityKey, request.CompilationSettings.Keys);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
            var reply = call("engineering-services.ExecuteEngineeringServices", arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing engineering-services worker reply.");
            return new ResponseMessage { Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone() };
        }
        public ResponseMessage ManageProjectLanguage(string action="read", string culture="", bool dryRun=true)
            => Invoke(new EngineeringServicesRequest { Operation = "ManageProjectLanguage", Action = action, Culture = culture, DryRun = dryRun });

        public ResponseMessage ExportProjectTexts(string filePath, string sourceCulture, string targetCulture, bool dryRun = true)
            => Invoke(new EngineeringServicesRequest { Operation = "ExportProjectTexts", FilePath = filePath, SourceCulture = sourceCulture, TargetCulture = targetCulture, DryRun = dryRun });

        public ResponseMessage ImportProjectTexts(string filePath, bool updateSourceLanguage, bool dryRun = true)
            => Invoke(new EngineeringServicesRequest { Operation = "ImportProjectTexts", FilePath = filePath, UpdateSourceLanguage = updateSourceLanguage, DryRun = dryRun });

        public ResponseMessage ReadObjectIdentifier(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", string identifier = "")
            => Invoke(new EngineeringServicesRequest { Operation = "GetObjectIdentifier", Kind = kind, DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!, ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!, SoftwarePath = softwarePath, ObjectPath = objectPath, Identifier = identifier });

        public ResponseMessage ShowObjectInEditor(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", bool dryRun = true)
            => Invoke(new EngineeringServicesRequest { Operation = "ShowObjectInEditor", Kind = kind, DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!, ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!, SoftwarePath = softwarePath, ObjectPath = objectPath, DryRun = dryRun });

        public ResponseMessage CompareLibraries(string leftLibraryName = "", string rightLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100)
            => Invoke(new EngineeringServicesRequest { Operation = "CompareLibraries", LeftLibraryName = leftLibraryName, RightLibraryName = rightLibraryName, IncludeIdentical = includeIdentical, MaxDepth = maxDepth, Offset = offset, Limit = limit });

        public ResponseMessage CompareProjects(string kind, string softwarePath = "", string devicePathJson = "[]", string itemPathJson = "[]", string targetProjectName = "", string targetSoftwarePath = "",
            string targetDevicePathJson = "[]", string targetItemPathJson = "[]", string targetLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100)
            => Invoke(new EngineeringServicesRequest { Operation = "CompareProjects", SameDevicePaths = devicePathJson == targetDevicePathJson, Kind = kind, SoftwarePath = softwarePath, DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!, ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!, TargetProjectName = targetProjectName, TargetSoftwarePath = targetSoftwarePath, TargetDevicePath = JsonSerializer.Deserialize<string[]>(targetDevicePathJson)!, TargetItemPath = JsonSerializer.Deserialize<string[]>(targetItemPathJson)!, TargetLibraryName = targetLibraryName, IncludeIdentical = includeIdentical, MaxDepth = maxDepth, Offset = offset, Limit = limit });

        public ResponseMessage ReadProjectSettings(string folderPath = "", string customIdentityKey = "", int offset = 0, int limit = 100)
            => Invoke(new EngineeringServicesRequest { Operation = "GetProjectSettings", FolderPath = folderPath, CustomIdentityKey = customIdentityKey, Offset = offset, Limit = limit });

        public ResponseMessage ManageProjectCompilationSettings(string action = "read", string propertiesJson = "{}", bool dryRun = true)
            => Invoke(new EngineeringServicesRequest { Operation = "ManageProjectCompilationSettings", Action = action, CompilationSettings = SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest(action, propertiesJson, dryRun).Values, DryRun = dryRun });

    }
}
