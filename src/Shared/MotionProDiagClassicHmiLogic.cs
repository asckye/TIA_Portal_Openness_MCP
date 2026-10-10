using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/parsing for the Motion/ProDiag/classic HMI family; no Siemens.Engineering dependency.
    internal static class MotionProDiagClassicHmiLogic
    {
        internal const string MotionNs = "Siemens.Engineering.SW.TechnologicalObjects.Motion.";
        internal const string IdentProvider = "Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider";
        internal const string SupervisionProvider = "Siemens.Engineering.SW.Supervision.SupervisionProvider";
        internal const string SupervisionSettingsProvider = "Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider";
        internal const string GraphicsProvider = "Siemens.Engineering.Hmi.Globalization.GraphicsProvider";
        internal static readonly string[] MotionActions = { "read", "addMasterValue", "removeMasterValue", "createMapping", "updateMapping", "deleteMapping", "connect", "disconnect", "connectIdent" };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> MasterValueAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["synchronousSetPoint"] = (MotionNs + "SynchronousAxisMasterValues", "SetPointCoupling"),
            ["synchronousActualValue"] = (MotionNs + "SynchronousAxisMasterValues", "ActualValueCoupling"),
            ["synchronousDelayed"] = (MotionNs + "SynchronousAxisMasterValues", "DelayedCoupling"),
            ["conveyorSetPoint"] = (MotionNs + "ConveyorTrackingLeadingValues", "SetPointCoupling"),
            ["conveyorActualValue"] = (MotionNs + "ConveyorTrackingLeadingValues", "ActualValueCoupling"),
            ["conveyorDelayed"] = (MotionNs + "ConveyorTrackingLeadingValues", "DelayedCoupling"),
            ["superimposingSetPoint"] = (MotionNs + "SuperimposingAxes", "SetPointCoupling"),
        };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> MappingAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["toMapping"] = (MotionNs + "InterpreterMappings", "TechnologicalObjectMapping"),
            ["dbMemberMapping"] = (MotionNs + "InterpreterMappings", "DBMemberMapping"),
        };
        internal static readonly IReadOnlyDictionary<string, (string Service, string Property)> ConnectionAspects = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["actor"] = (MotionNs + "AxisHardwareConnectionProvider", "ActorInterface"),
            ["sensor"] = (MotionNs + "AxisHardwareConnectionProvider", "SensorInterface"),
            ["torque"] = (MotionNs + "AxisHardwareConnectionProvider", "TorqueInterface"),
            ["encoder"] = (MotionNs + "EncoderHardwareConnectionProvider", "SensorInterface"),
            ["measuringInput"] = (MotionNs + "MeasuringInputHardwareConnectionProvider", ""),
            ["outputCam"] = (MotionNs + "OutputCamHardwareConnectionProvider", ""),
        };
        // Overloads present in the V21 XML per interface type; bit addresses for actor/sensor/torque stay with ConfigureMotionHardwareConnection.
        internal static readonly IReadOnlyDictionary<string, string[]> ConnectionModes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["actor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "channel" },
            ["sensor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "channel" },
            ["torque"] = new[] { "deviceItem", "deviceItems", "dbMember" },
            ["encoder"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "addresses", "channel" },
            ["measuringInput"] = new[] { "address", "deviceItemChannel", "channel" },
            ["outputCam"] = new[] { "address", "plcTag", "channel" },
        };
        internal static readonly string[] ReadableMotionServices =
        {
            MotionNs + "AxisHardwareConnectionProvider", MotionNs + "EncoderHardwareConnectionProvider",
            MotionNs + "MeasuringInputHardwareConnectionProvider", MotionNs + "OutputCamHardwareConnectionProvider",
            MotionNs + "OutputCamMeasuringInputContainer", MotionNs + "SynchronousAxisMasterValues",
            MotionNs + "ConveyorTrackingLeadingValues", MotionNs + "SuperimposingAxes", MotionNs + "InterpreterMappings",
            MotionNs + "CamDataSupport", MotionNs + "InterpreterProgramSupport", IdentProvider,
        };
        internal static readonly string[] SupervisionActions = { "read", "readComposition", "createEntry", "deleteEntry", "setAttributes", "exportSettings", "importSettings" };
        internal static readonly string[] ScriptActions = { "read", "export", "import", "delete", "createFolder", "deleteFolder", "setAttributes" };
        internal static readonly string[] CycleActions = { "read", "export", "import", "delete", "setAttributes" };
        internal static readonly string[] ListActions = { "read", "readEntries", "createEntry", "deleteEntry", "export", "import", "delete", "setAttributes" };
        internal static readonly IReadOnlyDictionary<string, (string Property, string Type)> ListKinds = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["text"] = ("TextLists", "Siemens.Engineering.Hmi.TextGraphicList.TextList"),
            ["graphic"] = ("GraphicLists", "Siemens.Engineering.Hmi.TextGraphicList.GraphicList"),
        };
        internal static readonly IReadOnlyDictionary<string, string> LibraryTypeKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["faceplate"] = "Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType",
            ["vbScript"] = "Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryType",
            ["cScript"] = "Siemens.Engineering.Hmi.RuntimeScripting.CScriptLibraryType",
        };

        internal static string MotionCategory(string action, string aspect)
        {
            if (!MotionActions.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", MotionActions) + ".");
            aspect ??= "";
            if (action == "read" || action == "connectIdent")
            {
                if (aspect != "") throw new ArgumentException("aspect must be empty for " + action + ".");
                return action == "read" ? "read" : "ident";
            }
            if (action.EndsWith("MasterValue", StringComparison.Ordinal))
            {
                if (!MasterValueAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", MasterValueAspects.Keys) + ".");
                return "masterValue";
            }
            if (action.EndsWith("Mapping", StringComparison.Ordinal))
            {
                if (!MappingAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", MappingAspects.Keys) + ".");
                return "mapping";
            }
            if (!ConnectionAspects.ContainsKey(aspect)) throw new ArgumentException("aspect must be one of: " + string.Join("/", ConnectionAspects.Keys) + ".");
            return "connection";
        }

        internal sealed class ConnectionTarget
        {
            public string Mode = "";
            public string[]? DevicePath, ItemPath, SecondItemPath;
            public string DbMemberPath = "", PlcTagPath = "", ConnectOption = "Default";
            public bool HasConnectOption;
            public int InputBitAddress = -1, OutputBitAddress = -1, Address = -1, ChannelIndex = -1;
            // Connect(Channel) target - the exact channel (ChannelComposition.Find(type, ioType, number)) of the device item.
            public string ChannelType = "", ChannelIoType = ""; public int ChannelNumber = -1;
        }
        internal static ConnectionTarget ParseConnectionTarget(string json)
        {
            if (json == null || json.Length > 16384) throw new ArgumentException("targetJson exceeds 16 KiB.");
            var obj = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("targetJson must be a JSON object.");
            var allowed = new[] { "devicePath", "itemPath", "secondItemPath", "dbMemberPath", "plcTagPath", "connectOption", "inputBitAddress", "outputBitAddress", "address", "channelIndex", "channelType", "channelIoType", "channelNumber" };
            foreach (var pair in obj) if (!allowed.Contains(pair.Key)) throw new ArgumentException("Unknown targetJson key: " + pair.Key);
            string[]? Names(string key)
            {
                if (!obj.ContainsKey(key)) return null;
                var array = obj[key] as JsonArray ?? throw new ArgumentException(key + " must be a JSON string array.");
                var names = array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToArray();
                if (names.Length == 0 || names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(key + " must contain 1-64 nonempty exact names.");
                return names!;
            }
            int Int(string key)
            {
                if (!obj.ContainsKey(key)) return -1;
                if (obj[key] is not JsonValue v || !v.TryGetValue<int>(out var i) || i < 0) throw new ArgumentException(key + " must be a nonnegative integer.");
                return i;
            }
            string Str(string key)
            {
                if (!obj.ContainsKey(key)) return "";
                if (obj[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) throw new ArgumentException(key + " must be a nonempty string.");
                return s;
            }
            var target = new ConnectionTarget
            {
                DevicePath = Names("devicePath"), ItemPath = Names("itemPath"), SecondItemPath = Names("secondItemPath"),
                DbMemberPath = Str("dbMemberPath"), PlcTagPath = Str("plcTagPath"), HasConnectOption = obj.ContainsKey("connectOption"),
                InputBitAddress = Int("inputBitAddress"), OutputBitAddress = Int("outputBitAddress"), Address = Int("address"), ChannelIndex = Int("channelIndex"),
                ChannelType = Str("channelType"), ChannelIoType = Str("channelIoType"), ChannelNumber = Int("channelNumber"),
            };
            if (target.HasConnectOption) target.ConnectOption = Str("connectOption");
            var modes = new List<string>();
            bool channel = target.ChannelType != "" || target.ChannelIoType != "" || target.ChannelNumber >= 0;
            if (channel && (target.ChannelType == "" || target.ChannelIoType == "" || target.ChannelNumber < 0)) throw new ArgumentException("A channel target needs channelType, channelIoType and channelNumber together (plus devicePath and itemPath).");
            if (target.DevicePath != null || target.ItemPath != null)
            {
                if (target.DevicePath == null || target.ItemPath == null) throw new ArgumentException("devicePath and itemPath are both required for a device item target.");
                if ((target.SecondItemPath != null ? 1 : 0) + (target.ChannelIndex >= 0 ? 1 : 0) + (channel ? 1 : 0) > 1) throw new ArgumentException("secondItemPath, channelIndex and channelType/channelIoType/channelNumber are mutually exclusive.");
                modes.Add(target.SecondItemPath != null ? "deviceItems" : target.ChannelIndex >= 0 ? "deviceItemChannel" : channel ? "channel" : "deviceItem");
            }
            else if (target.SecondItemPath != null || target.ChannelIndex >= 0 || channel) throw new ArgumentException("secondItemPath/channelIndex/channel targets require devicePath and itemPath.");
            if (target.DbMemberPath != "") modes.Add("dbMember");
            if (target.PlcTagPath != "") modes.Add("plcTag");
            if (target.InputBitAddress >= 0 || target.OutputBitAddress >= 0)
            {
                if (target.InputBitAddress < 0 || target.OutputBitAddress < 0) throw new ArgumentException("inputBitAddress and outputBitAddress are both required; addresses are BITS.");
                modes.Add("addresses");
            }
            if (target.Address >= 0) modes.Add("address");
            if (modes.Count != 1) throw new ArgumentException("targetJson must describe exactly one target: deviceItem, deviceItems, deviceItemChannel, channel, dbMember, plcTag, addresses or address.");
            if (target.HasConnectOption && modes[0] != "deviceItems" && modes[0] != "addresses") throw new ArgumentException("connectOption applies only to deviceItems or addresses targets.");
            target.Mode = modes[0];
            return target;
        }
        internal static void RequireConnectionMode(string aspect, string mode)
        {
            if (!ConnectionModes[aspect].Contains(mode))
                throw new ArgumentException("Target " + mode + " is not a native Connect overload for aspect " + aspect + " (allowed: " + string.Join("/", ConnectionModes[aspect]) + "). Bit addresses for actor/sensor/torque are handled by ConfigureMotionHardwareConnection.");
        }
        internal static string RequireAction(string action, string[] allowed)
        {
            if (!allowed.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", allowed) + ".");
            return action;
        }
        internal static string RequireName(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact " + what + " (single nonempty segment) required.");
            return value;
        }
        internal static void RequirePagination(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
        }
        internal static JsonObject ParseAttributes(string json, bool required)
        {
            if (json == null || json.Length > 65536) throw new ArgumentException("attributesJson exceeds 64 KiB.");
            var obj = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("attributesJson must be a JSON object.");
            if (obj.Count > 50) throw new ArgumentException("At most 50 attributes per request.");
            foreach (var pair in obj)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Attribute names must be nonempty.");
                if (pair.Value is JsonObject || pair.Value is JsonArray) throw new ArgumentException("Only scalar attribute values are supported: " + pair.Key);
            }
            if (required && obj.Count == 0) throw new ArgumentException("Nonempty attributesJson required.");
            if (!required && obj.Count != 0) throw new ArgumentException("attributesJson is not accepted by this action.");
            return obj;
        }
        internal static void RequireImportConfirmation(string importOptions, bool confirmDelete)
        {
            if (importOptions == "Override" && !confirmDelete) throw new ArgumentException("importOptions=Override replaces existing objects; confirmDelete=true is required.");
        }
        internal static (string[] Folder, string Name) SplitObjectPath(string path)
        {
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 64 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x == "." || x == ".."))
                throw new ArgumentException("Use a relative exact path with 1-64 nonempty segments, without '.' or '..'.");
            return (parts.Take(parts.Length - 1).ToArray(), parts[parts.Length - 1]);
        }
        internal static string ShortTypeName(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1);
    }

}
