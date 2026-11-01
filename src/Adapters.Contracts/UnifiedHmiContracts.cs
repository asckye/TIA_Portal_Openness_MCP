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

        public static bool LegacyWrite(string operation) => new[] { "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag",
            "EnsureUnifiedHmiConnection", "EnsureUnifiedHmiScreenItem", "ApplyUnifiedHmiScreenDesign", "BindUnifiedHmiButtonPressedTag",
            "EnsureUnifiedHmiButtonEventHandler", "EnsureUnifiedHmiDynamization", "BindUnifiedHmiTagDynamization" }.Contains(operation, StringComparer.Ordinal);

        public static Dictionary<string, object?> Preview(UnifiedHmiRequest request)
        {
            var plan = typeof(UnifiedHmiRequest).GetProperties().Where(p => p.Name != "Inputs" && p.Name != "IsReadOnly")
                .ToDictionary(p => p.Name, p => p.GetValue(request), StringComparer.Ordinal);
            return new Dictionary<string, object?> {
                ["Message"] = "Unified HMI preview; planned changes only. No native call or write was issued.",
                ["Meta"] = new Dictionary<string, object?> { ["operationSuccess"] = true, ["dryRun"] = true,
                    ["mayHaveChanged"] = false, ["plannedChanges"] = plan,
                    ["scope"] = "Managed argument validation only. Existence, native API shape and TIA semantics are checked during execution." } };
        }

        private static void Design(UnifiedHmiInput value, bool item = false)
        {
            if (value.Kind == "error") throw new ArgumentException("Invalid design JSON: " + value.Text);
            if (value.Kind != "object") throw new ArgumentException("Design must be an object.");
            foreach (var field in value.Properties)
            {
                if (field.Key.Equals("Name", StringComparison.OrdinalIgnoreCase) && !(item && field.Key == "name"))
                    throw new ArgumentException("Renaming through screen design is refused; use an explicit rename operation.");
                if (field.Value.Kind == "object") Design(field.Value);
                if (field.Value.Kind == "array") foreach (var child in field.Value.Items)
                    if (child.Kind == "object") Design(child, field.Key == "items");
            }
        }

        public static void Request(UnifiedHmiRequest request)
        {
            if (!string.IsNullOrEmpty(request.ScreenName)) EngineeringInputValidation.NameOrAbsolutePath(request.ScreenName);
            if (!string.IsNullOrEmpty(request.TagTableName)) EngineeringInputValidation.NameOrAbsolutePath(request.TagTableName);
            if (request.Operation == "ManageUnifiedHmiGroup")
            {
                EngineeringInputValidation.HmiGroupPath(request.GroupPath);
                EngineeringInputValidation.Action(request.Action, "create", "rename", "deleteEmpty");
                if (request.Family != "screens" && request.Family != "tags") throw new ArgumentException("family must be screens or tags.");
                if (request.Action == "rename" && (string.IsNullOrWhiteSpace(request.NewName) || request.NewName == "." || request.NewName == ".." || request.NewName.IndexOfAny(new[] { '/', '\\' }) >= 0)) throw new ArgumentException("newName must be one nonempty segment.");
            }
            if (request.Operation == "ApplyUnifiedHmiScreenDesign" && request.Inputs.TryGetValue(request.DesignJson, out var design))
            {
                Design(design);
                if (!design.Properties.TryGetValue("items", out var items) || items.Kind != "array") throw new ArgumentException("Design items must be an array.");
            }
            if (request.Operation == "EnsureUnifiedHmiConnection" && (string.IsNullOrWhiteSpace(request.HmiSoftwarePath) || string.IsNullOrWhiteSpace(request.ConnectionName) || string.IsNullOrWhiteSpace(request.PlcName)))
                throw new ArgumentException("Explicit nonempty hmiSoftwarePath, connectionName and plcName required.");
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
