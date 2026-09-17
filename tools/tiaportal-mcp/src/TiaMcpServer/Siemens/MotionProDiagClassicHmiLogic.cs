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
            ["actor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag" },
            ["sensor"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag" },
            ["torque"] = new[] { "deviceItem", "deviceItems", "dbMember" },
            ["encoder"] = new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "addresses" },
            ["measuringInput"] = new[] { "address", "deviceItemChannel" },
            ["outputCam"] = new[] { "address", "plcTag" },
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
        }
        internal static ConnectionTarget ParseConnectionTarget(string json)
        {
            if (json == null || json.Length > 16384) throw new ArgumentException("targetJson exceeds 16 KiB.");
            var obj = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("targetJson must be a JSON object.");
            var allowed = new[] { "devicePath", "itemPath", "secondItemPath", "dbMemberPath", "plcTagPath", "connectOption", "inputBitAddress", "outputBitAddress", "address", "channelIndex" };
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
            };
            if (target.HasConnectOption) target.ConnectOption = Str("connectOption");
            var modes = new List<string>();
            if (target.DevicePath != null || target.ItemPath != null)
            {
                if (target.DevicePath == null || target.ItemPath == null) throw new ArgumentException("devicePath and itemPath are both required for a device item target.");
                if (target.SecondItemPath != null && target.ChannelIndex >= 0) throw new ArgumentException("secondItemPath and channelIndex are mutually exclusive.");
                modes.Add(target.SecondItemPath != null ? "deviceItems" : target.ChannelIndex >= 0 ? "deviceItemChannel" : "deviceItem");
            }
            else if (target.SecondItemPath != null || target.ChannelIndex >= 0) throw new ArgumentException("secondItemPath/channelIndex require devicePath and itemPath.");
            if (target.DbMemberPath != "") modes.Add("dbMember");
            if (target.PlcTagPath != "") modes.Add("plcTag");
            if (target.InputBitAddress >= 0 || target.OutputBitAddress >= 0)
            {
                if (target.InputBitAddress < 0 || target.OutputBitAddress < 0) throw new ArgumentException("inputBitAddress and outputBitAddress are both required; addresses are BITS.");
                modes.Add("addresses");
            }
            if (target.Address >= 0) modes.Add("address");
            if (modes.Count != 1) throw new ArgumentException("targetJson must describe exactly one target: deviceItem, deviceItems, deviceItemChannel, dbMember, plcTag, addresses or address.");
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
            var parts = EngineeringGroupOperations.Parts(path);
            return (parts.Take(parts.Length - 1).ToArray(), parts[parts.Length - 1]);
        }
        internal static string ShortTypeName(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1);
    }

    // Official IEngineeringObject metadata access (GetAttributeInfos/GetAttribute/SetAttribute/GetCompositionInfos/
    // GetComposition/GetCreationInfos/Create/GetInvocationInfos/Invoke), resolved by reflection so it also runs offline.
    internal static class EngineeringDynamicAccess
    {
        private static readonly string[] Interfaces = { "Siemens.Engineering.IEngineeringObject", "Siemens.Engineering.IEngineeringComposition" };
        internal static MethodInfo? Method(object target, string name, params Type[] signature)
            => target.GetType().GetMethod(name, signature)
               ?? target.GetType().GetInterfaces().Where(i => Interfaces.Contains(i.FullName)).Select(i => i.GetMethod(name, signature)).FirstOrDefault(m => m != null);
        private static MethodInfo Require(object target, string name, params Type[] signature)
            => Method(target, name, signature) ?? throw new NotSupportedException("Official " + name + " is unavailable on " + target.GetType().FullName + ".");
        private static object? Invoke(object target, MethodInfo method, params object?[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        private static object? Prop(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
        private static IEnumerable<object> Descriptors(object target, string method, int bound)
        {
            var result = Invoke(target, Require(target, method)) as IEnumerable ?? throw new NotSupportedException(method + " returned no descriptors.");
            int count = 0;
            foreach (var info in result)
            {
                if (++count > bound) throw new NotSupportedException(method + " exceeds " + bound + " descriptors.");
                if (info != null) yield return info;
            }
        }
        internal static List<(string Name, string Access)> Infos(object target)
            => Descriptors(target, "GetAttributeInfos", 512).Select(i => (Prop(i, "Name")?.ToString() ?? "", Prop(i, "AccessMode")?.ToString() ?? "")).Where(x => x.Item1 != "").ToList();
        internal static JsonObject Read(object target)
        {
            var values = new JsonObject(); var excluded = new JsonArray(); var failures = new JsonArray(); var schema = new JsonArray();
            var get = Method(target, "GetAttribute", typeof(string));
            foreach (var (name, access) in Infos(target))
            {
                schema.Add(new JsonObject { ["name"] = name, ["accessMode"] = access });
                if (get == null || access.IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0) { excluded.Add(name); continue; }
                try
                {
                    var value = Invoke(target, get, name);
                    if (value != null && !EngineeringScalarProperties.Scalar(value.GetType())) { excluded.Add(name); continue; }
                    values[name] = EngineeringScalarProperties.Json(value);
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["attribute"] = name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return new JsonObject { ["type"] = target.GetType().FullName, ["scope"] = "IEngineeringObject.GetAttributeInfos/GetAttribute scalar values only",
                ["values"] = values, ["schema"] = schema, ["excludedComplexAttributes"] = excluded, ["failures"] = failures, ["dataComplete"] = failures.Count == 0 };
        }
        internal static List<(string Name, object? Value)> Prepare(object target, JsonObject changes)
        {
            var infos = Infos(target); var get = Require(target, "GetAttribute", typeof(string)); Require(target, "SetAttribute", typeof(string), typeof(object));
            var prepared = new List<(string, object?)>();
            foreach (var change in changes)
            {
                var info = infos.FirstOrDefault(i => string.Equals(i.Name, change.Key, StringComparison.Ordinal));
                if (info.Name == null) throw new NotSupportedException("Attribute not advertised by GetAttributeInfos: " + change.Key);
                if (info.Access.IndexOf("Write", StringComparison.OrdinalIgnoreCase) < 0) throw new NotSupportedException("Attribute is not writable: " + change.Key);
                var existing = Invoke(target, get, change.Key);
                var type = existing != null && EngineeringScalarProperties.Scalar(existing.GetType()) ? existing.GetType() : typeof(object);
                prepared.Add((change.Key, EngineeringScalarProperties.ConvertValue(change.Value, type)));
            }
            return prepared;
        }
        internal static void Apply(object target, List<(string Name, object? Value)> changes, JsonObject meta)
        {
            var set = Require(target, "SetAttribute", typeof(string), typeof(object)); var get = Require(target, "GetAttribute", typeof(string));
            var applied = new JsonArray(); meta["appliedAttributes"] = applied;
            foreach (var (name, value) in changes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = name;
                Invoke(target, set, name, value);
                applied.Add(name);
                var readback = Invoke(target, get, name);
                if (!Equals(readback, value) && !string.Equals(Convert.ToString(readback, CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    throw new InvalidOperationException("Attribute readback differs: " + name + ". Changes are not rolled back.");
            }
        }
        internal static List<string> CompositionNames(object target)
            => Descriptors(target, "GetCompositionInfos", 128).Select(i => Prop(i, "Name")?.ToString() ?? "").Where(n => n != "").ToList();
        internal static object Composition(object target, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact compositionName required.");
            if (!CompositionNames(target).Contains(name, StringComparer.Ordinal)) throw new NotSupportedException("Composition not advertised by GetCompositionInfos: " + name);
            return Invoke(target, Require(target, "GetComposition", typeof(string)), name) ?? throw new NotSupportedException("GetComposition returned null: " + name);
        }
        internal static List<Type> CreationTypes(object composition)
            => Descriptors(composition, "GetCreationInfos", 128).Select(i => Prop(i, "Type") as Type).Where(t => t != null).Select(t => t!).ToList();
        internal static Type CreationType(object composition, string typeName)
        {
            var types = CreationTypes(composition);
            var matches = types.Where(t => t.FullName == typeName || t.Name == typeName).ToList();
            if (matches.Count != 1) throw new NotSupportedException("typeName must be exactly one type advertised by GetCreationInfos: " + string.Join(", ", types.Select(t => t.FullName)));
            return matches[0];
        }
        internal static object Create(object composition, Type type, JsonObject attributes)
        {
            var method = Require(composition, "Create", typeof(Type), typeof(IEnumerable<KeyValuePair<string, object>>));
            var list = attributes.Select(kv => new KeyValuePair<string, object>(kv.Key, EngineeringScalarProperties.ConvertValue(kv.Value, typeof(object))!)).ToList();
            return Invoke(composition, method, type, list) ?? throw new InvalidOperationException("Native Create returned null.");
        }
        internal static string? Name(object item)
        {
            var property = item.GetType().GetProperty("Name");
            if (property?.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0) return property.GetValue(item)?.ToString();
            var get = Method(item, "GetAttribute", typeof(string));
            if (get == null || !Infos(item).Any(i => i.Name == "Name")) return null;
            return Invoke(item, get, "Name")?.ToString();
        }
        internal static object FindByName(object composition, string name)
        {
            var matches = EngineeringGroupOperations.Items(composition).Where(x => string.Equals(Name(x), name, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("Ambiguous exact entry name: " + name);
            return matches.SingleOrDefault() ?? throw new InvalidOperationException("Exact entry not found: " + name);
        }
        internal static bool CanDelete(object item)
        {
            if (item.GetType().GetMethod("Delete", Type.EmptyTypes) != null) return true;
            var infos = Method(item, "GetInvocationInfos");
            return infos != null && Descriptors(item, "GetInvocationInfos", 256).Any(i => Prop(i, "Name")?.ToString() == "Delete" && (Prop(i, "ParameterInfos") as IEnumerable)?.Cast<object>().Any() != true);
        }
        internal static void Delete(object item)
        {
            if (!CanDelete(item)) throw new NotSupportedException("Native Delete is neither a public method nor an advertised invocation on " + item.GetType().FullName + ".");
            var direct = item.GetType().GetMethod("Delete", Type.EmptyTypes);
            if (direct != null) { Invoke(item, direct); return; }
            Invoke(item, Require(item, "Invoke", typeof(string), typeof(IEnumerable<KeyValuePair<Type, object>>)), "Delete", new List<KeyValuePair<Type, object>>());
        }
    }
}
