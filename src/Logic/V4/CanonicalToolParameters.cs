using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    // Public parameter names are adapted once, before the existing implementation's admission and native dispatch.
    public static class CanonicalToolParameters
    {
        public sealed class Alias
        {
            public string Legacy { get; }
            public string Canonical { get; }
            internal Alias(string legacy, string canonical) { Legacy = legacy; Canonical = canonical; }
        }

        private static readonly IReadOnlyDictionary<string, Alias[]> Table = BuildTable();
        private static readonly HashSet<string> StringDevices = new HashSet<string>(StringComparer.Ordinal) {
            "ExportDeviceAml", "GetDeviceAttributes", "GetDeviceInfo", "GetDeviceIpAddress", "GetPlcLiveValuesS7",
            "GetPlcPutGetAccess", "SetPlcPutGetAccess"
        };
        public static IReadOnlyList<Alias> Aliases(string tool) => Table.TryGetValue(tool, out var aliases) ? aliases : Array.Empty<Alias>();
        public static bool HasStringDevice(string tool) => StringDevices.Contains(tool);

        private static IReadOnlyDictionary<string, Alias[]> BuildTable()
        {
            var table = new Dictionary<string, List<Alias>>(StringComparer.Ordinal);
            void Add(string tools, string legacy, string canonical)
            {
                foreach (string tool in tools.Split(' '))
                {
                    if (!table.TryGetValue(tool, out var aliases)) table[tool] = aliases = new List<Alias>();
                    aliases.Add(new Alias(legacy, canonical));
                }
            }
            Add("ExportPlcBlock ExportPlcBlocks ExportPlcType ExportPlcTypes ExportPlcBlockDocuments ExportPlcBlocksDocuments",
                "exportPath", "exportDirectory");
            Add("ExportPlcTagTable ExportPlcWatchTable ExportTechnologyObject ExportAlarmClasses ExportAlarmTextLists " +
                "ExportAlarmInstanceTexts ExportHmiScreen ExportHmiTagTable ExportHmiConnection ExportOpcUaInterface ExportDeviceAml",
                "exportPath", "exportFile");
            Add("ExportDeviceAml", "exportPath", "exportDirectory");
            Add("BuildDeviceAmlDocument GenerateOpcUaModelledInterface PatchPlcBlockDocument WritePlcSclSourceFile " +
                "RenderPlcBlock RenderPlcProgramAtlas RenderPlcVisualDiff SaveExportContent ManageStandardPackage " +
                "ManageMachineDescription RenderPlcBlockDocument GeneratePlcDocumentation ManageUnifiedCwcPackage", "outputPath", "exportFile");
            Add("ManageMachineDescription", "outputPath", "exportDirectory");
            Add("GenerateAcceptanceReport GenerateErrorReport WriteClassicHmiMinimalPackageFiles BuildReleaseHandoffArtifacts InstantiatePlcTemplates",
                "outputDirectory", "exportDirectory");
            Add("ExportHmiProgram ExportTechnologyObjectsToDirectory", "exportDir", "exportDirectory");
            Add("ExportPlcWatchTablesToDirectory", "dir", "exportDirectory");
            Add("ImportPlcBlocksFromDirectory ImportHmiScreensFromDirectory ImportHmiTagTablesFromDirectory " +
                "ImportPlcTagTablesFromDirectory ImportTechnologyObjectsFromDirectory", "dir", "sourceDirectory");
            Add("AnalyzePlcReferences ScanPlcSourceAnnotations GeneratePlcDocumentation ManageUnifiedCwcPackage", "directory", "sourceDirectory");
            Add("ExchangeUnifiedTags ExchangeUnifiedScriptModules", "directory", "exportDirectory");
            Add("ExchangeUnifiedTags ExchangeUnifiedScriptModules", "directory", "sourceDirectory");
            return table.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        }

        public static JsonElement Schema(string tool, JsonElement original)
        {
            if (Aliases(tool).Count == 0 && !HasStringDevice(tool)) return original;
            var schema = JsonNode.Parse(original.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            if (Aliases(tool).All(alias => !properties.ContainsKey(alias.Legacy) || properties.ContainsKey(alias.Canonical))
                && (!HasStringDevice(tool) || properties["devicePath"] is JsonObject { } existing && existing["anyOf"] != null))
                return original;
            var required = schema["required"] as JsonArray;
            foreach (var group in Aliases(tool).GroupBy(alias => alias.Legacy))
            {
                if (properties[group.Key] is not JsonObject legacy) continue;
                bool alreadyCanonical = group.All(alias => properties.ContainsKey(alias.Canonical));
                if (alreadyCanonical) continue;
                bool wasRequired = required?.Any(value => (string?)value == group.Key) == true;
                bool output = group.Any(alias => alias.Canonical.StartsWith("export", StringComparison.Ordinal));
                string rule = (wasRequired && !output ? " Supply exactly one of " : " Supply at most one of ")
                    + string.Join(" or ", new[] { group.Key }.Concat(group.Select(alias => alias.Canonical)))
                    + "; these parameters are mutually exclusive."
                    + (output ? " Omit the target to use the session output directory when available; otherwise supply it explicitly." : "");
                string description = (string?)legacy["description"] ?? "";
                legacy["description"] = description + " Deprecated alias; use " + string.Join(" or ", group.Select(alias => alias.Canonical))
                    + ". Removed in 5.0; its existing file/directory meaning is unchanged." + rule;
                foreach (var alias in group)
                {
                    var property = legacy.DeepClone().AsObject();
                    property.Remove("default");
                    property["minLength"] = 1;
                    property["pattern"] = @"^(?:[A-Za-z]:[\\/]|/|\\\\)";
                    property["description"] = alias.Canonical == "exportFile"
                        ? "Absolute output file path on the TIA host. Supply a file, not a directory. " + description
                        : alias.Canonical == "exportDirectory"
                            ? "Absolute output directory on the TIA host. " + description
                            : "Absolute input directory on the TIA host. " + description;
                    property["description"] = (string?)property["description"] + rule;
                    properties[alias.Canonical] = property;
                }
                if (wasRequired) required!.Remove(required.First(value => (string?)value == group.Key));
            }
            if (HasStringDevice(tool) && properties["devicePath"] is JsonObject device && device["anyOf"] == null)
            {
                properties["devicePath"] = new JsonObject {
                    ["description"] = "Exact device path segments: [group, ..., station], or [station] at the project root. " +
                        "The legacy string form is deprecated and will be removed in 5.0.",
                    ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["minItems"] = 1,
                        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } }, device.DeepClone())
                };
            }
            return JsonSerializer.SerializeToElement(schema);
        }

        private static IEnumerable<Alias> DefaultTargets(string tool, JsonObject input, IEnumerable<string> required)
        {
            foreach (var group in Aliases(tool).GroupBy(alias => alias.Legacy))
            {
                var aliases = group.ToArray();
                if (!aliases.Any(alias => alias.Canonical.StartsWith("export", StringComparison.Ordinal))
                    || input.ContainsKey(group.Key) || aliases.Any(alias => input.ContainsKey(alias.Canonical))) continue;
                if (tool is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" && (string?)input["action"] != "export") continue;
                if (!required.Contains(group.Key) && !(group.Any(alias => alias.Canonical == "exportDirectory")
                    && group.Key == "outputDirectory")) continue;
                yield return aliases.FirstOrDefault(alias => alias.Canonical == "exportDirectory") ?? aliases[0];
            }
        }

        public static bool NeedsDefault(string tool, JsonObject input, IEnumerable<string> required)
            => DefaultTargets(tool, input, required).Any();

        public static JsonObject WithDefaults(string tool, JsonObject input, IEnumerable<string> required, string directory)
        {
            if (Aliases(tool).Count == 0) return input;
            var result = input.DeepClone().AsObject();
            foreach (var alias in DefaultTargets(tool, input, required))
                result[alias.Canonical] = alias.Canonical == "exportDirectory"
                    ? tool is "ExportPlcBlocksDocuments" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules"
                        ? Path.Combine(directory, tool) : directory
                    : Path.Combine(directory, tool + Extension(tool));
            return result;
        }

        private static string Extension(string tool) => tool switch {
            "ExportAlarmClasses" => ".dat",
            "ExportAlarmTextLists" or "ExportAlarmInstanceTexts" => ".xlsx",
            "BuildDeviceAmlDocument" => ".aml",
            "GeneratePlcDocumentation" => ".md",
            "RenderPlcBlock" or "RenderPlcProgramAtlas" or "RenderPlcVisualDiff" => ".html",
            "SaveExportContent" => ".txt",
            _ => ".xml"
        };

        public static Error? ValidateAliases(string tool, JsonObject input, IEnumerable<string>? required = null, bool requireOutput = false)
        {
            foreach (var group in Aliases(tool).GroupBy(alias => alias.Legacy))
            {
                var supplied = new[] { group.Key }.Concat(group.Select(alias => alias.Canonical)).Where(input.ContainsKey).ToArray();
                bool output = group.Any(alias => alias.Canonical.StartsWith("export", StringComparison.Ordinal));
                bool mandatory = required?.Contains(group.Key) == true || !output
                    || tool is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" && (string?)input["action"] == "import";
                bool importing = tool is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" && (string?)input["action"] == "import";
                if (supplied.Length == 0 && mandatory && (!output || importing || requireOutput))
                    return new Error("Supply " + string.Join(" or ", group.Select(alias => alias.Canonical).Append(group.Key))
                        + (output ? "; no default export directory is available." : "."),
                        new InvalidArgumentDetails(group.Key, Array.Empty<string>()));
                if (supplied.Length > 1)
                    return new Error("Supply exactly one of " + string.Join(", ", supplied) + ". Aliases cannot be combined.",
                        new InvalidArgumentDetails("/" + supplied[1], Array.Empty<string>()));
            }
            foreach (var alias in Aliases(tool))
            {
                if (input[alias.Canonical] is not JsonValue value || !value.TryGetValue<string>(out var path)) continue;
                bool file = alias.Canonical == "exportFile";
                bool directory = alias.Canonical is "exportDirectory" or "sourceDirectory";
                if (file && (path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal)
                    || Directory.Exists(path)) || directory && File.Exists(path))
                    return new Error(alias.Canonical + (file ? " requires a file path, not a directory." : " requires a directory, not a file."),
                        new InvalidArgumentDetails("/" + alias.Canonical, Array.Empty<string>()));
                if (tool == "ExportDeviceAml" && alias.Canonical == "exportDirectory" && !Directory.Exists(path))
                    return new Error("exportDirectory requires an existing directory for AML output.",
                        new InvalidArgumentDetails("/exportDirectory", Array.Empty<string>()));
                if (tool == "WritePlcSclSourceFile" && file && !Path.HasExtension(path))
                    return new Error("exportFile requires an explicit SCL filename and extension.",
                        new InvalidArgumentDetails("/exportFile", Array.Empty<string>()));
                if (tool == "ManageMachineDescription"
                    && file == ((string?)input["format"] == "csv" && (string?)input["action"] != "import"))
                    return new Error("CSV exports and templates use exportDirectory; imports and JSON exports use exportFile.",
                        new InvalidArgumentDetails("/" + alias.Canonical, Array.Empty<string>()));
                if (tool is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules"
                    && (alias.Canonical == "exportDirectory") != ((string?)input["action"] == "export"))
                    return new Error("Export uses exportDirectory; import uses sourceDirectory.",
                        new InvalidArgumentDetails("/" + alias.Canonical, Array.Empty<string>()));
            }
            return null;
        }

        public static JsonObject LegacyArguments(string tool, JsonObject input)
        {
            if (Aliases(tool).Count == 0 && !HasStringDevice(tool)) return input;
            var arguments = input.DeepClone().AsObject();
            foreach (var alias in Aliases(tool))
                if (arguments.TryGetPropertyValue(alias.Canonical, out var value))
                {
                    arguments[alias.Legacy] = value?.DeepClone();
                    arguments.Remove(alias.Canonical);
                }
            if (HasStringDevice(tool) && arguments["devicePath"] is JsonArray segments)
                arguments["devicePath"] = string.Join("/", segments.Select(segment => segment!.GetValue<string>()));
            return arguments;
        }

        public static JsonNode? Warn(string tool, JsonObject input, JsonNode? result)
        {
            if (Aliases(tool).Count == 0 && !HasStringDevice(tool)) return result;
            if (result?["meta"]?["warnings"] is not JsonArray) return result;
            var warnings = Aliases(tool).Where(alias => input.ContainsKey(alias.Legacy)).GroupBy(alias => alias.Legacy)
                .Select(group => (Parameter: group.Key, Replacement: string.Join(" or ", group.Select(alias => alias.Canonical)))).ToList();
            if (HasStringDevice(tool) && input["devicePath"] is JsonValue)
                warnings.Add(("devicePath", "devicePath as an array"));
            if (warnings.Count == 0) return result;
            var body = result.DeepClone();
            var output = body["meta"]!["warnings"]!.AsArray();
            foreach (var warning in warnings)
                output.Add(new JsonObject { ["code"] = "DEPRECATED_PARAMETER",
                    ["message"] = "Use " + warning.Replacement + "; the legacy parameter form will be removed in 5.0.",
                    ["details"] = new JsonObject { ["parameter"] = "/" + warning.Parameter,
                        ["replacement"] = warning.Replacement, ["removalVersion"] = "5.0" } });
            return body;
        }
    }
}
