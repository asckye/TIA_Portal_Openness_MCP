using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class UnifiedValue
    {
        public string Kind { get; set; } = "null";
        public string Text { get; set; } = "";
        public string? Error { get; set; }
        public Dictionary<string, UnifiedValue> Properties { get; set; } = new Dictionary<string, UnifiedValue>(StringComparer.Ordinal);
        public List<UnifiedValue> Items { get; set; } = new List<UnifiedValue>();
    }
    public sealed class UnifiedRequest
    {
        public string Operation { get; set; } = "";
        public Dictionary<string, UnifiedValue> Inputs { get; set; } = new Dictionary<string, UnifiedValue>(StringComparer.Ordinal);
        public string SoftwarePath { get; set; } = "";
        public string ObjectPathJson { get; set; } = "";
        public int Offset { get; set; }
        public int Limit { get; set; }
        public string PropertiesJson { get; set; } = "";
        public bool Overwrite { get; set; }
        public bool DryRun { get; set; }
        public string Property { get; set; } = "";
        public string Culture { get; set; } = "";
        public string RawText { get; set; } = "";
        public string Filter { get; set; } = "";
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string DestinationDirectory { get; set; } = "";
        public string TagPathJson { get; set; } = "";
        public string Action { get; set; } = "";
        public string DurationPathJson { get; set; } = "";
        public string Kind { get; set; } = "";
        public uint Days { get; set; }
        public uint Hours { get; set; }
        public uint Minutes { get; set; }
        public uint Seconds { get; set; }
        public uint HundredNanoseconds { get; set; }
        public string NodeId { get; set; } = "";
        public string Connection { get; set; } = "";
        public string PlantPath { get; set; } = "";
        public string PlantObjectType { get; set; } = "";
        public string CollectionProperty { get; set; } = "";
        public string PartName { get; set; } = "";
        public int PartIndex { get; set; }
        public string PartKind { get; set; } = "";
        public bool ConfirmDelete { get; set; }
        public string PropertyName { get; set; } = "";
        public string DynamizationKind { get; set; } = "";
        public string MappingEntriesJson { get; set; } = "";
        public string ListName { get; set; } = "";
        public string EntryKey { get; set; } = "";
        public string EntryJson { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ExpectedNamesJson { get; set; } = "";
        public string EventType { get; set; } = "";
        public string ScriptPropertiesJson { get; set; } = "";
        public string ExpectedToken { get; set; } = "";
        public string Directory { get; set; } = "";
        public string TagTable { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ExpectedTagNamesJson { get; set; } = "";
        public string ModuleName { get; set; } = "";
        public string ConnectionName { get; set; } = "";
        public string XmlPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ExpectedProject { get; set; } = "";
        public string ScriptCode { get; set; } = "";
        public string ScreenPath { get; set; } = "";
        public string ItemNamesJson { get; set; } = "";
        public string Cursor { get; set; } = "";
        public int PageSize { get; set; }
        public int BudgetMs { get; set; }
        public string BeforePagesJson { get; set; } = "";
        public string AfterPagesJson { get; set; } = "";
        public string FieldsJson { get; set; } = "";
        public string ChangesJson { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string BranchJson { get; set; } = "";
        public string TypePath { get; set; } = "";
        public string Version { get; set; } = "";
        public string FolderPath { get; set; } = "";
    }
}
