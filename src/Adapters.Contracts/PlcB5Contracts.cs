using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class PlcB5DocumentEvent
    {
        public string Kind { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? State { get; set; }
        public bool ChangesProject { get; set; }
        public string Directory { get; set; } = "";
        public string Name { get; set; } = "";
        public object? Value { get; set; }
    }
    public sealed class PlcB5Attribute { public string Name { get; set; } = ""; public string? AccessMode { get; set; } public object? Value { get; set; } }
    public sealed class PlcB5Block
    {
        public string? Name { get; set; }
        public string? TypeName { get; set; }
        public string? Namespace { get; set; }
        public string? ProgrammingLanguage { get; set; }
        public string? MemoryLayout { get; set; }
        public bool? IsConsistent { get; set; }
        public string? HeaderName { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool? IsKnowHowProtected { get; set; }
        public List<PlcB5Attribute>? Attributes { get; set; }
        public string? Description { get; set; }
    }
    public sealed class PlcB5Input
    {
        public string? Error { get; set; }
        public string Kind { get; set; } = "";
        public Dictionary<string, HardwareScalar> Values { get; set; } = new Dictionary<string, HardwareScalar>();
        public string[] Strings { get; set; } = Array.Empty<string>();
    }
    public sealed class PlcB5Request
    {
        public bool PreservePath { get; set; }
        public string[] ImportedNames { get; set; } = Array.Empty<string>();
        public string RegexName { get; set; } = "";
        public string FileNameWithoutExtension { get; set; } = "";
        public string Operation { get; set; } = "";
        public string? ValidationError { get; set; }
        public bool ValidatedWriting { get; set; }
        public string[] TextLists { get; set; } = Array.Empty<string>();
        public string[] Cultures { get; set; } = Array.Empty<string>();
        public Dictionary<string, PlcB5Input> Inputs { get; set; } = new Dictionary<string, PlcB5Input>();
        public string SoftwarePath { get; set; } = "";
        public string ExportPath { get; set; } = "";
        public string ImportPath { get; set; } = "";
        public bool IncludeInfoText { get; set; } = true;
        public bool IncludeAdditionalTexts { get; set; } = true;
        public bool IncludeAlarmClass { get; set; } = true;
        public string Action { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string UnitName { get; set; } = "";
        public string UnitKind { get; set; } = "unit";
        public string TextListNamesJson { get; set; } = "[]";
        public string CulturesJson { get; set; } = "";
        public string ImportOption { get; set; } = "Override";
        public bool ConfirmImport { get; set; } = false;
        public bool DryRun { get; set; } = true;
        public string Name { get; set; } = "";
        public string LibraryName { get; set; } = "";
        public string MasterCopyPath { get; set; } = "";
        public string CopyMode { get; set; } = "";
        public bool ConfirmDelete { get; set; } = false;
        public int Offset { get; set; } = 0;
        public int Limit { get; set; } = 100;
        public string InterfaceName { get; set; } = "";
        public string InterfaceType { get; set; } = "ServerInterface";
        public bool Enabled { get; set; } = false;
        public string Section { get; set; } = "roles";
        public string RoleName { get; set; } = "";
        public string DefinedInNamespace { get; set; } = "";
        public string ProjectRole { get; set; } = "";
        public string NamespaceUri { get; set; } = "";
        public string Permission { get; set; } = "";
        public string PropertiesJson { get; set; } = "{}";
        public bool ConfirmChange { get; set; } = false;
        public string GroupPath { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public bool VerifyDocumentReadback { get; set; } = false;
        public string ObjectKind { get; set; } = "document";
        public string TypePath { get; set; } = "";
        public string Version { get; set; } = "";
        public string UpdatePathsMode { get; set; } = "";
        public string BlockPath { get; set; } = "";
        public string ProviderKind { get; set; } = "supervision";
        public string CompositionName { get; set; } = "";
        public string EntryName { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string AttributesJson { get; set; } = "{}";
        public string ImportOptions { get; set; } = "None";
        public string OutputPath { get; set; } = "";
        public bool KeepFolderStructure { get; set; } = false;
        public bool KeepEmptyDataBlocks { get; set; } = false;
        public string AccessLevelsJson { get; set; } = "{}";
    }
    public sealed class PlcB5Reply : IWorkerOperationReply
    {
        public PlcB5Block[]? Blocks { get; set; }
        public int? Count { get; set; }
        public List<PlcB5DocumentEvent> Events { get; set; } = new List<PlcB5DocumentEvent>();
        public string[] ExportFailures { get; set; } = Array.Empty<string>();
        public string[] ImportFailures { get; set; } = Array.Empty<string>();
        public string[] ImportedNames { get; set; } = Array.Empty<string>();
        public int Scanned { get; set; }
        public int Selected { get; set; }
        public int Attempted { get; set; }
        public int Succeeded { get; set; }
        public bool Stopped { get; set; }
        public bool Ok { get; set; }
        public bool Value { get; set; }
        public string Message { get; set; } = "";
        public Dictionary<string, object?>? Meta { get; set; }
        public Dictionary<string, object?>? Data { get; set; }
        public PlcSoftwareFailure? Failure { get; set; }
        public bool RequiresSessionReset { get; set; }
        public bool MayHaveChanged { get; set; }
        public bool BlockReadsAfterUncertain => true;
    }
}
