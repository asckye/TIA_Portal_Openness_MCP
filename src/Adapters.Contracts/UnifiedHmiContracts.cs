using System;
using System.Collections.Generic;
using System.Linq;
namespace TiaMcp.Adapters.Contracts
{
    public static class UnifiedHmiInputValidation
    {
        public static void PlcBinding(string plcName, string plcTag)
        {
            if (string.IsNullOrWhiteSpace(plcName) || string.IsNullOrWhiteSpace(plcTag))
                throw new ArgumentException("EnsureUnifiedHmiTag requires explicit nonempty plcName and plcTag. Read the exact PLC software and tag names first; no PLC_1 or same-name default is used.");
        }

        public static bool ContainerType(string itemType)
        {
            var key = (itemType ?? "").Trim();
            const string prefix = "Siemens.Engineering.HmiUnified.UI.";
            if (key.StartsWith(prefix, StringComparison.Ordinal)) key = key.Substring(prefix.Length);
            return new[] { "HmiFaceplateContainer", "FaceplateContainer", "Controls.HmiFaceplateContainer",
                "HmiCustomWebControlContainer", "CustomWebControlContainer", "Base.HmiCustomWebControlContainer",
                "HmiCustomWidgetContainer", "CustomWidgetContainer", "Base.HmiCustomWidgetContainer" }
                .Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        public static void ContainedType(string itemType, string containedType)
        {
            if (ContainerType(itemType))
            {
                if (string.IsNullOrWhiteSpace(containedType) || containedType.Length > 4096 || containedType.Any(char.IsControl))
                    throw new ArgumentException("containedType is required for " + itemType + ": pass the exact value read from an existing container (1..4096 characters, no control characters). The server does not compose it.");
            }
            else if (!string.IsNullOrEmpty(containedType))
                throw new ArgumentException("containedType is only accepted for HmiFaceplateContainer, HmiCustomWebControlContainer and HmiCustomWidgetContainer.");
        }

        public static void Request(UnifiedHmiRequest request)
        {
            if (request.Operation == "EnsureUnifiedHmiTag") PlcBinding(request.PlcName, request.PlcTag);
            if (request.Operation == "ManageUnifiedScreenItem")
            {
                EngineeringInputValidation.AbsoluteHmiPath(request.ScreenPath);
                if (request.Action == "create") ContainedType(request.ItemType, request.ContainedType);
                else if (!string.IsNullOrEmpty(request.ContainedType)) throw new ArgumentException("containedType is only used by create.");
            }
        }
    }

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
        public string? GlobalDefinitionAreaScriptCode { get; set; }
        public bool? Async { get; set; }
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
