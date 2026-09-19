using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-② (2.7.39): pure logic (no Siemens dependency) for the SINAMICS DCC option package - drive control charts and
    // subcharts, DCB libraries and block types, DCC blocks, pins, published parameters, connections, chart interfaces and partitions.
    internal static class DccLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        internal static JsonObject ParseObject(string json, string parameter)
            => (string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject) ?? throw new ArgumentException(parameter + " must be a JSON object.");
        // Chart paths are Root/Sub/Sub relative to DriveControlChartContainer.Charts (subcharts through DriveControlChart.Subcharts).
        internal static string[] ChartParts(string chartPath) { RequireText(chartPath, "chartPath", 1024); return EngineeringGroupOperations.Parts(chartPath); }

        internal static readonly string[] ImportOptions = { "None", "RenameOnConflict" };
        // Writable scalars per DCC object class (official attribute tables); Name of a chart / block is renamed through propertiesJson.Name.
        internal static readonly string[] ChartProperties = { "Name", "Comment", "HorizontalSheets", "VerticalSheets", "PositionX", "PositionY", "Partition" };
        internal static readonly string[] BlockProperties = { "Name", "Comment", "PositionX", "PositionY", "GenericInputsNumber", "Partition" };
        internal static readonly string[] PinProperties = { "Comment", "Value", "Unit", "Invisible", "ForTest" };
        internal static readonly string[] InterfaceProperties = { "Comment", "Value", "Unit", "Invisible", "ForTest" };
        internal static readonly string[] ParameterProperties = { "Number", "ArrayIndex", "ParameterText", "IsSignal" };
        internal static readonly string[] PartitionProperties = { "Name", "Comment" };
        internal static JsonObject ValidateProperties(string propertiesJson, string[] allowed, string what)
        {
            var o = ParseObject(propertiesJson, "propertiesJson");
            foreach (var pair in o)
            {
                if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new ArgumentException("propertiesJson." + pair.Key + " is not a writable " + what + " property (" + string.Join(", ", allowed) + ").");
                if (pair.Value is JsonObject || pair.Value is JsonArray) throw new ArgumentException("propertiesJson." + pair.Key + " must be a scalar.");
            }
            return o;
        }

        // ---- charts ----------------------------------------------------------------------------------------------------------------
        internal static readonly string[] ChartActions = { "read", "readSequence", "create", "update", "delete", "export", "exportAll", "import", "optimizeSequence", "showEditor" };
        internal sealed class ChartRequest { public string Action = "", ImportOption = "None"; public string[] Path = Array.Empty<string>(); public bool AutoName, Writes, WritesFiles; public JsonObject Properties = new JsonObject(); }
        internal static ChartRequest ValidateChartRequest(string chartPath, string action, string filePath, string importOptions, string propertiesJson, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, ChartActions, "action");
            var r = new ChartRequest { Action = action };
            bool containerLevel = action == "exportAll" || action == "import" || (action == "readSequence" && string.IsNullOrEmpty(chartPath)) || (action == "read" && string.IsNullOrEmpty(chartPath));
            if (action == "create" && string.IsNullOrEmpty(chartPath)) r.AutoName = true;                 // DriveControlChartComposition.Create() picks the next DCC_n
            else if (!containerLevel) r.Path = ChartParts(chartPath);
            else if (!string.IsNullOrEmpty(chartPath)) r.Path = ChartParts(chartPath);
            if (action == "export" || action == "exportAll" || action == "import")
            {
                RequireText(filePath, "filePath", 1024);
                if (!string.Equals(Path.GetExtension(filePath), ".dcc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must be a .dcc chart exchange file.");
            }
            else Refuse(filePath, "filePath", "applies to export / exportAll / import only.");
            if (action == "import") r.ImportOption = RequireOneOf(string.IsNullOrEmpty(importOptions) ? "None" : importOptions, ImportOptions, "importOptions");
            else Refuse(importOptions, "importOptions", "applies to action import only.");
            if (action == "create" || action == "update") { r.Properties = ValidateProperties(propertiesJson, ChartProperties, "chart"); if (action == "create" && r.Properties.ContainsKey("Name")) throw new ArgumentException("Name on create is the chartPath (last segment)."); if (action == "update" && r.Properties.Count == 0) throw new ArgumentException("update needs at least one property."); }
            else if (ParseObject(propertiesJson, "propertiesJson").Count > 0) throw new ArgumentException("propertiesJson applies to create / update only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.WritesFiles = (action == "export" || action == "exportAll") && !dryRun;
            r.Writes = action != "read" && action != "readSequence" && action != "export" && action != "exportAll" && !dryRun;
            return r;
        }

        // ---- blocks ----------------------------------------------------------------------------------------------------------------
        internal static readonly string[] BlockActions = { "read", "create", "update", "delete", "setAsPredecessor" };
        internal sealed class BlockRequest { public string Action = "", BlockType = "", LibraryName = ""; public string[] ChartPath = Array.Empty<string>(); public string Name = ""; public bool Writes; public JsonObject Properties = new JsonObject(); }
        internal static BlockRequest ValidateBlockRequest(string chartPath, string blockName, string action, string blockType, string libraryName, string propertiesJson, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, BlockActions, "action");
            var r = new BlockRequest { Action = action, ChartPath = ChartParts(chartPath) };
            if ((action != "create" && action != "read") || !string.IsNullOrEmpty(blockName)) RequireText(blockName, "blockName", 128);   // read lists all blocks, create may auto-name
            r.Name = blockName;
            if (action == "create") { RequireText(blockType, "blockType", 128); r.BlockType = blockType; r.LibraryName = libraryName ?? ""; if (!string.IsNullOrEmpty(libraryName)) RequireText(libraryName, "libraryName", 128); }
            else { Refuse(blockType, "blockType", "applies to action create only."); Refuse(libraryName, "libraryName", "applies to action create only."); }
            if (action == "create" || action == "update") { r.Properties = ValidateProperties(propertiesJson, BlockProperties, "block"); if (action == "create" && r.Properties.ContainsKey("Name")) throw new ArgumentException("Name on create is blockName."); if (action == "update" && r.Properties.Count == 0) throw new ArgumentException("update needs at least one property."); }
            else if (ParseObject(propertiesJson, "propertiesJson").Count > 0) throw new ArgumentException("propertiesJson applies to create / update only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.Writes = action != "read" && !dryRun;
            return r;
        }

        // ---- pins ------------------------------------------------------------------------------------------------------------------
        internal static readonly string[] PinActions = { "read", "update", "connect", "disconnect", "publish", "unpublish", "updateParameter" };
        internal sealed class PinRequest
        {
            public string Action = "", PartnerBlock = "", PartnerPin = "", PartnerInterface = "", SinkName = "";
            public string[] ChartPath = Array.Empty<string>(); public string Block = "", Pin = "";
            public bool SetAsSignal, HasParameterNumber, HasArrayIndex, Writes; public uint ParameterNumber, ArrayIndex;
            public JsonObject Properties = new JsonObject();
        }
        // partnerJson: {"block":"add_1","pin":"Y"} (pin-to-pin) or {"chartInterface":"In_1"} (pin to a chart interface); disconnect names the sink.
        internal static PinRequest ValidatePinRequest(string chartPath, string blockName, string pinName, string action, string propertiesJson, string partnerJson, bool setAsSignal, int parameterNumber, int arrayIndex, bool dryRun)
        {
            RequireOneOf(action, PinActions, "action");
            RequireText(blockName, "blockName", 128); RequireText(pinName, "pinName", 128);
            var r = new PinRequest { Action = action, ChartPath = ChartParts(chartPath), Block = blockName, Pin = pinName, SetAsSignal = setAsSignal };
            var partner = ParseObject(partnerJson, "partnerJson");
            if (action == "connect" || action == "disconnect")
            {
                foreach (var pair in partner) if (pair.Key != "block" && pair.Key != "pin" && pair.Key != "chartInterface") throw new ArgumentException("partnerJson accepts block + pin or chartInterface.");
                string Str(string k) => partner[k]?.GetValue<string>() ?? "";
                if (partner.ContainsKey("chartInterface")) { if (partner.ContainsKey("block") || partner.ContainsKey("pin")) throw new ArgumentException("partnerJson: give chartInterface or block + pin, not both."); r.PartnerInterface = Str("chartInterface"); RequireText(r.PartnerInterface, "partnerJson.chartInterface", 128); }
                else { r.PartnerBlock = Str("block"); r.PartnerPin = Str("pin"); RequireText(r.PartnerBlock, "partnerJson.block", 128); RequireText(r.PartnerPin, "partnerJson.pin", 128); }
            }
            else if (partner.Count > 0) throw new ArgumentException("partnerJson applies to connect / disconnect only.");
            if (action == "publish")
            {
                r.HasParameterNumber = parameterNumber >= 0; r.HasArrayIndex = arrayIndex >= 0;
                if (r.HasParameterNumber) r.ParameterNumber = (uint)parameterNumber; if (r.HasArrayIndex) r.ArrayIndex = (uint)arrayIndex;
                if (r.HasArrayIndex && !r.HasParameterNumber) throw new ArgumentException("arrayIndex needs parameterNumber (Publish(number, index, setAsSignal), SINAMICS FW V6.1+).");
            }
            else if (parameterNumber >= 0 || arrayIndex >= 0) throw new ArgumentException("parameterNumber / arrayIndex apply to action publish only.");
            if (action == "update") { r.Properties = ValidateProperties(propertiesJson, PinProperties, "pin"); if (r.Properties.Count == 0) throw new ArgumentException("update needs at least one property."); }
            else if (action == "updateParameter") { r.Properties = ValidateProperties(propertiesJson, ParameterProperties, "published parameter"); if (r.Properties.Count == 0) throw new ArgumentException("updateParameter needs at least one property."); }
            else if (ParseObject(propertiesJson, "propertiesJson").Count > 0) throw new ArgumentException("propertiesJson applies to update / updateParameter only.");
            r.Writes = action != "read" && !dryRun;
            return r;
        }

        // ---- chart interfaces / partitions ---------------------------------------------------------------------------------------
        internal static readonly string[] InterfaceActions = { "read", "create", "update", "delete" };
        internal sealed class InterfaceRequest { public string Action = "", Name = "", SourceBlock = "", SourcePin = ""; public string[] ChartPath = Array.Empty<string>(); public bool Writes; public JsonObject Properties = new JsonObject(); }
        internal static InterfaceRequest ValidateInterfaceRequest(string chartPath, string interfaceName, string action, string sourceBlock, string sourcePin, string propertiesJson, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, InterfaceActions, "action");
            var r = new InterfaceRequest { Action = action, ChartPath = ChartParts(chartPath), Name = interfaceName ?? "" };
            if (action != "read" && action != "create") RequireText(interfaceName, "interfaceName", 128);
            if (action == "create") { RequireText(sourceBlock, "sourceBlock", 128); RequireText(sourcePin, "sourcePin", 128); r.SourceBlock = sourceBlock; r.SourcePin = sourcePin; Refuse(interfaceName, "interfaceName", "is not accepted on create: DccChartInterfaceComposition.Create(DccPin) names the interface after the pin."); }
            else { Refuse(sourceBlock, "sourceBlock", "applies to action create only."); Refuse(sourcePin, "sourcePin", "applies to action create only."); }
            if (action == "update") { r.Properties = ValidateProperties(propertiesJson, InterfaceProperties, "chart interface"); if (r.Properties.Count == 0) throw new ArgumentException("update needs at least one property."); }
            else if (ParseObject(propertiesJson, "propertiesJson").Count > 0) throw new ArgumentException("propertiesJson applies to action update only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.Writes = action != "read" && !dryRun;
            return r;
        }
        internal static readonly string[] PartitionActions = { "read", "create", "update", "delete" };
        internal sealed class PartitionRequest { public string Action = "", Name = ""; public string[] ChartPath = Array.Empty<string>(); public bool Writes; public JsonObject Properties = new JsonObject(); }
        internal static PartitionRequest ValidatePartitionRequest(string chartPath, string partitionName, string action, string propertiesJson, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, PartitionActions, "action");
            var r = new PartitionRequest { Action = action, ChartPath = ChartParts(chartPath), Name = partitionName ?? "" };
            if (action != "read") RequireText(partitionName, "partitionName", 128);
            if (action == "update") { r.Properties = ValidateProperties(propertiesJson, PartitionProperties, "partition"); if (r.Properties.Count == 0) throw new ArgumentException("update needs at least one property."); }
            else if (ParseObject(propertiesJson, "propertiesJson").Count > 0) throw new ArgumentException("propertiesJson applies to action update only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.Writes = action != "read" && !dryRun;
            return r;
        }

        // ---- DCB libraries -------------------------------------------------------------------------------------------------------
        internal static readonly string[] LibraryActions = { "read", "import" };
        internal static bool ValidateLibraryRequest(string action, string filePath, bool dryRun)
        {
            RequireOneOf(action, LibraryActions, "action");
            if (action == "import")
            {
                RequireText(filePath, "filePath", 1024);
                if (!string.Equals(Path.GetExtension(filePath), ".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must be the DCB extension library .zip (e.g. GMCV5_1_sinamics5_1_(5.1.15).zip).");
            }
            else Refuse(filePath, "filePath", "applies to action import only.");
            return action == "import" && !dryRun;
        }
    }
}
