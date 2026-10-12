using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class HmiRequest
    {
        public string Operation { get; set; } = "";
        public string? AttributesError { get; set; }
        public Dictionary<string, HardwareScalar> Attributes { get; set; } = new Dictionary<string, HardwareScalar>();
        public string SoftwarePath { get; set; } = "";
        public int MaxMembers { get; set; }
        public string ScreenName { get; set; } = "";
        public string TagTableName { get; set; } = "";
        public string TagName { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string ExportPath { get; set; } = "";
        public string ConnectionName { get; set; } = "";
        public string ExportDir { get; set; } = "";
        public bool ExportScreens { get; set; }
        public bool ExportTagTables { get; set; }
        public string FolderPath { get; set; } = "";
        public string ImportPath { get; set; } = "";
        public string Dir { get; set; } = "";
        public string RegexName { get; set; } = "";
        public bool Overwrite { get; set; }
        public string ScreenPath { get; set; } = "";
        public int MaxDepth { get; set; }
        public int MaxNodes { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public string TagTablePath { get; set; } = "";
        public bool DryRun { get; set; }
        public bool ConfirmDelete { get; set; }
        public string Kind { get; set; } = "";
        public string ObjectKind { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string Action { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ImportOptions { get; set; } = "";
        public string FolderKind { get; set; } = "";
        public string NewName { get; set; } = "";
        public string Name { get; set; } = "";
        public string ScriptPath { get; set; } = "";
        public string AttributesJson { get; set; } = "";
        public string CycleName { get; set; } = "";
        public string ListKind { get; set; } = "";
        public string ListName { get; set; } = "";
        public string CompositionName { get; set; } = "";
        public string EntryName { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string LibraryName { get; set; } = "";
    }
    public sealed class HmiReply : IWorkerOperationReply
    {
        public object? Value { get; set; }
        public string? Notes { get; set; }
        public PlcSoftwareFailure? Failure { get; set; }
        public bool MayHaveChanged { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool BlockReadsAfterUncertain => true;
    }
    public sealed class HmiMessage
    {
        public string? Message { get; set; }
        public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    }
    public sealed class HmiDescribe
    {
        public Dictionary<string, object?>? Meta { get; set; }
        public string? Message { get; set; }
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? TypeName { get; set; }
        public IEnumerable<OpennessMember>? Members { get; set; }
    }
    public sealed class HmiImportFailure { public string? Path { get; set; } public string? Error { get; set; } }
    public sealed class HmiImportBatch
    {
        public List<string> Imported { get; set; } = new List<string>();
        public List<HmiImportFailure> Failed { get; set; } = new List<HmiImportFailure>();
        public Dictionary<string, object?>? Meta { get; set; }
    }
    public sealed class HmiProgramInfo { public string? Name { get; set; } public string ProgramType { get; set; } = ""; public List<string> Screens { get; set; } = new List<string>(); }
    public sealed class HmiExportProgram { public List<string> Exported { get; set; } = new List<string>(); public List<string> Failed { get; set; } = new List<string>(); public object? Items { get; set; } }
    public static class HmiCapabilities
    {
        public static string? Unsupported(string release, string tool)
            => tool == "ManageClassicHmiGraphic" && release != "21" || tool == "GetClassicHmiGlobalization" && release != "21"
                ? "GraphicsProvider (multilingual graphics) exists in the V21 PublicAPI only." : null;
    }
}
