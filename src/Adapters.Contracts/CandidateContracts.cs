using System;
using System.Collections.Generic;
using System.IO;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class DeviceCatalogEntry
    {
        public string TypeIdentifier { get; set; } = "";
        public string ArticleNumber { get; set; } = "";
        public string Version { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Description { get; set; } = "";
        public string CatalogPath { get; set; } = "";
    }

    public sealed class DeviceInventoryItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ParentId { get; set; } = "";
        public bool IsGroup { get; set; }
    }

    public interface IDeviceCreationAdapter
    {
        CandidateIdentity ReadIdentity();
        IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string typeIdentifier);
        IReadOnlyList<DeviceInventoryItem> ReadInventory();
        string RootId { get; }
        void BeforeCreate();
        DeviceInventoryItem Create(string typeIdentifier, string deviceName);
    }

    public sealed class PlcImportRequest
    {
        public string SoftwarePath { get; set; } = "";
        public string InputPath { get; set; } = "";
        public string BlockGroupPath { get; set; } = "";
        public string TypeGroupPath { get; set; } = "";
        public string TagFolderPath { get; set; } = "";
        public string TechnologyFolderPath { get; set; } = "";
        public string RegexName { get; set; } = "";
        public string FileNameWithoutExtension { get; set; } = "";
        public string[] ImportOrder { get; set; } = Array.Empty<string>();
        public bool Overwrite { get; set; }
        public string VersionPolicy { get; set; } = "exact";
        public string OnError { get; set; } = "stop";
        public bool CompileAfter { get; set; }
        public int MaxItems { get; set; } = 128;
    }

    public sealed class PlcImportObject
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public int? Number { get; set; }
        public string ContentHash { get; set; } = "";
    }

    public sealed class PlcImportInput
    {
        public string Path { get; set; } = "";
        public string[] Files { get; set; } = Array.Empty<string>();
        public PlcImportObject Target { get; set; } = new PlcImportObject();
        public string ContentHash { get; set; } = "";
        public bool Documents { get; set; }
    }

    public interface IPlcImportAdapter
    {
        CandidateIdentity ReadIdentity();
        IReadOnlyList<PlcImportInput> ReadInputs(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks);
        IReadOnlyList<PlcImportObject> ReadInventory();
        string TargetGroupIdentity(PlcImportObject target);
        bool SupportsOverwrite(PlcImportInput input);
        void BeforeImport(PlcImportInput input);
        PlcImportObject Import(PlcImportInput input, bool overwrite);
        // Export/read the actual returned native object on this same release and native thread.
        string ReadContent(PlcImportInput input, PlcImportObject imported);
    }

}
