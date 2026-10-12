using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public sealed class UnifiedHmiRequest
    {
        public string Operation { get; set; } = "";
        public bool DryRun { get; set; }
        public bool IsReadOnly => DryRun || Operation == "ManageUnifiedScreenItem"
            && (Action == "read" || Action == "list");
        public string HmiSoftwarePath { get; set; } = "";
        public string ScreenName { get; set; } = "";
        public string TagTableName { get; set; } = "";
        public string PlcName { get; set; } = "";
        public string ConnectionName { get; set; } = "";
        public uint Width { get; set; }
        public uint Height { get; set; }
        public string TagName { get; set; } = "";
        public string HmiDataType { get; set; } = "";
        public string PlcTag { get; set; } = "";
        public string Address { get; set; } = "";
        public bool RequireVerifiedBinding { get; set; }
        public string ItemName { get; set; } = "";
        public string ItemType { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public string Text { get; set; } = "";
        public string TextProperty { get; set; } = "";
        public string DesignJson { get; set; } = "";
        public bool Strict { get; set; }
        public string ButtonName { get; set; } = "";
        public string NameContains { get; set; } = "";
        public int Limit { get; set; }
        public string EventType { get; set; } = "";
        public int MaxMembers { get; set; }
        public string ScriptCode { get; set; } = "";
        public string GlobalDefinitionAreaScriptCode { get; set; } = "";
        public bool Async { get; set; }
        public bool SyntaxCheck { get; set; }
        public string PropertyName { get; set; } = "";
        public string DynamizationType { get; set; } = "";
        public string DataType { get; set; } = "";
        public int Depth { get; set; }
        public string SoftwarePath { get; set; } = "";
        public string ScreenPath { get; set; } = "";
        public string Action { get; set; } = "";
        public string PropertiesJson { get; set; } = "";
        public bool ConfirmDelete { get; set; }
        public int Offset { get; set; }
        public string ContainedType { get; set; } = "";
        public string Family { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string NewName { get; set; } = "";
        public string ExpectedToken { get; set; } = "";
        public bool OnlyIfEmpty { get; set; }
        public Dictionary<string, UnifiedHmiInput> Inputs { get; set; } = new Dictionary<string, UnifiedHmiInput>();
    }
    public sealed class UnifiedHmiInput
    {
        public string Kind { get; set; } = "null";
        public string Text { get; set; } = "";
        public Dictionary<string, UnifiedHmiInput> Properties { get; set; } = new Dictionary<string, UnifiedHmiInput>();
        public List<UnifiedHmiInput> Items { get; set; } = new List<UnifiedHmiInput>();
    }
}
