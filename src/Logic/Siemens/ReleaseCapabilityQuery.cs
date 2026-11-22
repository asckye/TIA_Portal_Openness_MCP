using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcp.Versioning;

namespace TiaOpenness.Shared;

// Project a served call onto the same native capability tables used at admission.
public static class ReleaseCapabilityQuery
{
    private static readonly string[] Selectors = { "action", "kind", "source" };

    public static JsonObject WithSelectorDefaults(JsonObject input, JsonObject properties)
    {
        var result = input;
        foreach (string key in Selectors)
            if (!input.ContainsKey(key) && properties[key]?["default"] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (ReferenceEquals(result, input)) result = input.DeepClone().AsObject();
                result[key] = text;
            }
        return result;
    }

    private static string? Selector(JsonObject args)
    {
        foreach (string key in Selectors)
            if (args[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)) return text;
        return null;
    }

    public static string? Problem(string name, string release, JsonObject args)
    {
        var reason = Reason(name, release, args);
        return reason == null ? null : Error(name, release, args, reason).Message;
    }

    // A named recipe has no fixed selector. For a discriminated action every schema
    // alternative must be refused. Never use recipe arguments or their defaults here.
    internal static bool RefusesOperation(string name, string release, string selector, string operation, string discriminator, JsonArray? contexts)
    {
        if (selector.Length == 0) return false;
        var fixedInputs = new JsonObject { [selector] = operation };
        bool Refused() => Reason(name, release, fixedInputs, operationOnly: true) != null
            || TiaMcpServer.Siemens.ToolVersionPolicy.CallProblem(release, name, key => (string?)fixedInputs[key]).Length != 0;
        if (discriminator.Length == 0) return Refused();
        if (contexts == null || contexts.Count == 0) return false;
        foreach (var context in contexts)
        {
            fixedInputs[discriminator] = context?.DeepClone();
            if (!Refused()) return false;
        }
        return true;
    }

    public static Error Error(string name, string release, JsonObject args, string reason)
        => UnsupportedCapabilityDetails.ErrorFor(release, name, Selector(args), Explain(release, reason, key => Reason(name, key, args) == null));

    public static string? OptionalProblem(string release, OptionalEngineeringRequest request)
    {
        string? reason = OptionalEngineeringCapabilities.Unsupported(release, request);
        if (reason == null) return null;
        string name = request.Operation;
        return Explain(release, reason, key => PortedFamilies.Available(key, name)
            && (request.Action.Length == 0 || PortedFamilies.ForTool(name).SupportsAction(key, request.Action))
            && OptionalProductCapabilities.Unsupported(key, name, request.Category, request.Kind, request.Action, "", "") == null
            && OptionalEngineeringCapabilities.Unsupported(key, request) == null);
    }

    private static string Explain(string release, string reason, Func<string, bool> supported)
    {
        var releases = ReleaseRange.All;
        int first = Array.FindIndex(releases, key => supported(key));
        if (first < 0) return reason + " Unavailable on all supported releases.";
        if (first < Array.IndexOf(releases, release))
            return reason + " Supported releases: " + string.Join(", ", releases.Skip(first).Where(supported)
                .Select(key => TiaVersionCatalog.Get(key).DisplayName)) + ".";
        return reason + " Minimum release for this request is " + TiaVersionCatalog.Get(releases[first]).DisplayName + ".";
    }

    private static string? Reason(string name, string release, JsonObject args, bool operationOnly = false)
    {
        string Text(string key, string fallback = "") => args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
        int Number(string key, int fallback = 0) => args[key] is JsonValue value && value.TryGetValue<int>(out int number) ? number : fallback;
        bool Flag(string key, bool fallback = false) => args[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) ? flag : fallback;
        string Path(string key) => args[key] is JsonArray path ? string.Join("/", path.Select(value => value?.ToString())) : Text(key);
        string Raw(string key) => args[key]?.ToJsonString() ?? "";
        string action = Text("action"), kind = Text("kind"), family = Text("family", kind);
        var properties = (args["properties"] as JsonObject)?.Select(pair => pair.Key);
        if (!PortedFamilies.Contains(name)) return null;
        var port = PortedFamilies.ForTool(name);
        if (!PortedFamilies.Available(release, name) || args.ContainsKey("action") && !port.SupportsAction(release, action))
            return "The release capability table excludes this operation.";
        string? reason = HardwareCapabilities.Unsupported(release, name, action, family, Number("positionNumber", -1),
            Number("extendedPositionNumber", -1), Text("password").Length != 0, Text("type"))
            ?? PlcSoftwareCapabilities.Unsupported(release, name, action, family, Text("unitName"), Text("unitKind", "unit"),
                Text("targetKind"), Text("copyMode"), Text("generateOption", "None"));
        if (reason != null) return reason;
        if (port.Name == "F26")
        {
            reason = UnifiedHmiCapabilities.Unsupported(release, name, action, Text("itemType"), Text("dynamizationType"));
            foreach (var field in new[] { "design", "layout" })
                if (args[field] is JsonObject design && design["items"] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                        reason ??= UnifiedHmiCapabilities.Unsupported(release, name, action, (string?)item["type"] ?? "");
        }
        if (port.Name == "F22") return OnlineCapabilities.Unsupported(release, new OnlineRequest {
            Operation = name, Action = action, RhTarget = Text("rhTarget"), IpAddress = Text("ipAddress"), TargetIpAddress = Text("targetIpAddress"),
            Password = Text("password"), UserName = Text("userName"), UserType = Text("userType"), DownloadScope = Text("downloadScope"),
            ObjectKind = Text("objectKind") });
        if (port.Name is "F27" or "F28")
        {
            var unified = new UnifiedRequest { Operation = name, Action = action, Category = Text("category"), DryRun = Flag("dryRun", true),
                DynamizationKind = Text("dynamizationKind"), FieldsJson = Raw("fields"), ChangesJson = Raw("changes"), ObjectPathJson = Raw("objectPath") };
            foreach (var field in args.Where(field => field.Value is JsonArray or JsonObject))
                unified.Inputs[Raw(field.Key)] = CapabilityInput.Unified(JsonSerializer.SerializeToElement(field.Value));
            return UnifiedCapabilities.Unsupported(release, unified);
        }
        if (port.Name == "F07") reason ??= operationOnly
            ? SecurityCapabilities.UnsupportedOperation(TiaVersionCatalog.Get(release).MajorVersion, name, action)
            : SecurityCapabilities.Unsupported(release, name, action,
                name == "ManageSyslogServers" ? Text("scope", "project") : family, Text("category"), Text("filePath"));
        if (port.Name is "F10" or "F14" or "F15") reason ??= PlcB5Capabilities.Unsupported(release, new PlcB5Request {
            Operation = name, Action = action, UnitName = Text("unitName"), UnitKind = Text("unitKind"), InterfaceType = Text("interfaceType"),
            ObjectKind = Text("objectKind", "document"), CopyMode = Text("copyMode") });
        var b4 = new PlcSoftwareRequest { Operation = name, Action = action, TypeIdentifier = Text("typeIdentifier"), Version = Text("version"),
            Aspect = Text(name == "ConfigureMotionHardwareConnection" ? "interfaceKind" : "aspect"), Path = port.Name == "F13" ? Path("objectPath") : "",
            GroupPath = Path(name.StartsWith("ImportTechnologyObject", StringComparison.Ordinal) ? "folderPath" : "groupPath") };
        if (name == "ManagePlcSoftwareUnit" && args["properties"] is JsonObject fields && fields.ContainsKey("NamespacePreset"))
            b4.Properties["NamespacePreset"] = new HardwareScalar();
        reason ??= HmiCapabilities.Unsupported(release, name) ?? (operationOnly
            ? PlcB4Capabilities.UnsupportedOperation(TiaVersionCatalog.Get(release).MajorVersion, name, action) : PlcB4Capabilities.Unsupported(release, b4))
            ?? OptionalProductCapabilities.Unsupported(release, name, Text("category"), kind, action, Text("executionMode"), Text("password"))
            ?? EngineeringServicesCapabilities.Unsupported(release, name, family, Text("customIdentityKey"), properties, Text("view"))
            ?? LibraryCapabilities.Unsupported(release, name, action, Text("sourceKind"), properties,
                Text("forceUpdateMode", "SetOnlyHigherUpdatedVersionAsDefault"),
                Text("deleteUnusedVersionsMode", "DoNotDelete"), Text("structureConflictResolutionMode", "RetainStructure"),
                Text("copyMode"), Text("updatePathsMode"));
        if (reason == null && name == "ExchangeCfcCharts" && action.Length != 0 && action != "import")
            reason = CfcSafetyPolicy.ChartInventoryProblem(TiaVersionCatalog.Get(release).MajorVersion, "CFC " + action);
        if (reason == null && port.Name is ("F29" or "F31"))
        {
            var optional = new OptionalEngineeringRequest {
                Operation = name, Action = action, Source = Text("source", "read"), Kind = kind, Category = Text("category"),
                TelegramType = Text("telegramType"), InterfaceKind = Text("interfaceKind"), ChartName = Text("chartName"),
                DriveObjectNumber = (ushort)Number("driveObjectNumber"), DriveObjectIndex = Number("driveObjectIndex"),
                IncludeLibraries = Flag("includeLibraries"), IncludeBlocks = Flag("includeBlocks"), IncludePins = Flag("includePins"),
                IncludeParameters = Flag("includeParameters"), TablePath = Path("tablePath"), GenerationOptions = Text("generationOptions"),
                ArrayIndex = Number("arrayIndex", -1), SequenceIndex = Number("sequenceIndex", -1),
                AdditionalHmiDeviceNamesJson = Raw("additionalHmiDeviceNames"), ObjectPathJson = Raw("objectPath"), CollectionPathJson = Raw("collectionPath"),
                ValueJson = Raw("value"), PropertiesJson = Raw("properties"), ReferencesJson = Raw("references") };
            foreach (var field in args.Where(field => field.Value is JsonArray or JsonObject))
                optional.Inputs[Raw(field.Key)] = CapabilityInput.Optional(JsonSerializer.SerializeToElement(field.Value));
            reason = OptionalEngineeringCapabilities.Unsupported(release, optional);
        }
        return reason;
    }
}
