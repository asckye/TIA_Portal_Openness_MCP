using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    internal static class GenerationModelValidation
    {
        private static HashSet<string> Ids(JsonElement items) => new HashSet<string>(items.EnumerateArray().Select(i => i.GetProperty("id").GetString()!), StringComparer.Ordinal);
        internal static void RequireValid(string schema, JsonElement value)
        {
            var errors = new List<GenerationValidationError>();
            void Error(string path, string rule, string message) => errors.Add(new GenerationValidationError(path, rule, message));
            Walk(value, "");
            if (schema == "package")
            {
                var languages = value.GetProperty("languages");
                if (!languages.GetProperty("required").EnumerateArray().Any(l => l.GetString() == languages.GetProperty("default").GetString()))
                    Error("/languages/default", "language", "Default language must be required.");
                foreach (var language in languages.GetProperty("required").EnumerateArray())
                    if (!value.GetProperty("title").TryGetProperty(language.GetString()!, out _)) Error("/title", "language", "Required title language is missing.");
            }
            if (schema == "machine") ValidateMachine(value, errors);
            if (schema == "plan") ValidatePlan(value, errors);
            if (schema == "modes")
            {
                foreach (var model in value.GetProperty("models").EnumerateArray())
                {
                    var states = Ids(model.GetProperty("states"));
                    var modes = Ids(model.GetProperty("modes"));
                    var commands = Ids(model.GetProperty("commands"));
                    foreach (var transition in model.GetProperty("transitions").EnumerateArray())
                    {
                        if (!states.Contains(transition.GetProperty("from").GetString()!) || !states.Contains(transition.GetProperty("to").GetString()!)) Error("/models/transitions", "reference", "Unknown state.");
                        if (transition.TryGetProperty("command", out var command) && !commands.Contains(command.GetString()!)) Error("/models/transitions/command", "reference", "Unknown command.");
                    }
                    foreach (var availability in model.GetProperty("availability").EnumerateArray())
                        if (!modes.Contains(availability.GetProperty("mode").GetString()!) || availability.GetProperty("states").EnumerateArray().Any(s => !states.Contains(s.GetString()!))) Error("/models/availability", "reference", "Unknown mode or state.");
                }
            }
            if (schema == "rule") ValidateParameterSchema(value.GetProperty("params"), "/params", Error);
            if (errors.Count != 0) throw new GenerationValidationException(errors);

            void Walk(JsonElement current, string path)
            {
                if (current.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in current.EnumerateObject()) Walk(property.Value, path + "/" + CanonicalJson.Pointer(property.Name));
                    if (current.TryGetProperty("start", out var start) && current.TryGetProperty("end", out var end)
                        && start.ValueKind == JsonValueKind.Number && end.ValueKind == JsonValueKind.Number && CanonicalJson.CompareNumbers(start.GetRawText(), end.GetRawText()) > 0)
                        Error(path, "range", "Range start exceeds end.");
                }
                if (current.ValueKind == JsonValueKind.Array)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var roles = new HashSet<string>(StringComparer.Ordinal);
                    var numbers = new HashSet<string>(StringComparer.Ordinal);
                    var index = 0;
                    foreach (var item in current.EnumerateArray())
                    {
                        var child = path + "/" + index++;
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("id", out var id) && !ids.Add(id.GetString()!)) Error(child + "/id", "duplicate-id", "Duplicate entry id.");
                            if (schema == "rule" && path == "/signals" && item.TryGetProperty("role", out var role) && !roles.Add(role.GetString()!)) Error(child + "/role", "duplicate-role", "Duplicate signal role.");
                            if (schema == "modes" && item.TryGetProperty("number", out var number) && !numbers.Add(CanonicalJson.Number(number.GetRawText()))) Error(child + "/number", "duplicate-number", "Duplicate mode, state or command number.");
                        }
                        Walk(item, child);
                    }
                }
                if (current.ValueKind == JsonValueKind.String && schema != "machine" && schema != "plan" && schema != "check-result")
                    RestrictedPlaceholders.Validate(current.GetString()!, path, Error);
            }
        }

        private static void ValidateParameterSchema(JsonElement schema, string path, Action<string, string, string> error)
        {
            foreach (var pair in new[] { new[] { "minimum", "maximum" }, new[] { "minLength", "maxLength" }, new[] { "minItems", "maxItems" } })
                if (schema.TryGetProperty(pair[0], out var min) && schema.TryGetProperty(pair[1], out var max) && CanonicalJson.CompareNumbers(min.GetRawText(), max.GetRawText()) > 0)
                    error(path, "range", "Parameter schema bounds are reversed.");
            if (schema.TryGetProperty("pattern", out var pattern)) GenerationSchemas.Matches("", pattern.GetString()!, path + "/pattern");
            if (schema.TryGetProperty("required", out var required))
                foreach (var item in required.EnumerateArray())
                    if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(item.GetString()!, out _)) error(path + "/required", "reference", "Required parameter is not defined.");
            if (schema.TryGetProperty("properties", out var fields)) foreach (var field in fields.EnumerateObject()) ValidateParameterSchema(field.Value, path + "/properties/" + CanonicalJson.Pointer(field.Name), error);
            if (schema.TryGetProperty("items", out var items)) ValidateParameterSchema(items, path + "/items", error);
            if (schema.TryGetProperty("default", out var value))
                foreach (var finding in GenerationSchemas.ValidateSchema(schema, value, "common.schema.json")) error(path + "/default" + finding.Path, finding.Rule, finding.Message);
        }

        private static void ValidateMachine(JsonElement value, IList<GenerationValidationError> errors)
        {
            var stations = Ids(value.GetProperty("stations"));
            var nodes = new HashSet<string>(StringComparer.Ordinal);
            void Topology(JsonElement items, string path)
            {
                var index = 0;
                foreach (var item in items.EnumerateArray())
                {
                    var child = path + "/" + index++;
                    if (!nodes.Add(item.GetProperty("id").GetString()!)) errors.Add(new GenerationValidationError(child + "/id", "duplicate-id", "Topology ids must be globally unique."));
                    if (item.TryGetProperty("children", out var children)) Topology(children, child + "/children");
                }
            }
            Topology(value.GetProperty("topology"), "/topology");
            var deviceIndex = 0;
            foreach (var device in value.GetProperty("devices").EnumerateArray())
            {
                var path = "/devices/" + deviceIndex++;
                if (!stations.Contains(device.GetProperty("station").GetString()!)) errors.Add(new GenerationValidationError(path + "/station", "reference", "Unknown station."));
                if (!nodes.Contains(device.GetProperty("parent").GetString()!)) errors.Add(new GenerationValidationError(path + "/parent", "reference", "Unknown topology parent."));
            }
        }

        private static void ValidatePlan(JsonElement value, IList<GenerationValidationError> errors)
        {
            var phases = new HashSet<string>(value.GetProperty("phases").EnumerateArray().Select(p => p.GetString()!), StringComparer.Ordinal);
            var steps = value.GetProperty("steps").EnumerateArray().ToArray();
            var known = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                var path = "/steps/" + index;
                if (!phases.Contains(step.GetProperty("phase").GetString()!)) errors.Add(new GenerationValidationError(path + "/phase", "reference", "Step phase is not selected."));
                if (!keys.Add(step.GetProperty("key").GetString()!)) errors.Add(new GenerationValidationError(path + "/key", "duplicate-key", "Duplicate idempotency key."));
                foreach (var dependency in step.GetProperty("dependsOn").EnumerateArray())
                    if (!known.Contains(dependency.GetString()!)) errors.Add(new GenerationValidationError(path + "/dependsOn", "dependency", "Dependencies must reference earlier steps; cycles and forward references are prohibited."));
                known.Add(step.GetProperty("id").GetString()!);
                if (CanonicalJson.Hash(step.GetProperty("arguments")) != step.GetProperty("argumentDigest").GetString()) errors.Add(new GenerationValidationError(path + "/argumentDigest", "argument-digest", "Argument digest does not match canonical arguments."));
                if (step.GetProperty("availability").GetProperty("tool").GetString() != "present") errors.Add(new GenerationValidationError(path + "/availability/tool", "availability", "Executable steps must have a present tool; absent phases belong in unavailable."));
            }
            var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in value.GetProperty("artifacts").EnumerateArray())
            {
                var path = artifact.GetProperty("path").GetString()!;
                StandardPackageLoader.ValidatePath(path);
                if (!artifactNames.Add(path)) errors.Add(new GenerationValidationError("/artifacts", "duplicate-name", "Duplicate artifact path."));
            }
        }

        internal static void ValidatePackage(JsonElement manifest, IReadOnlyDictionary<string, JsonElement> documents, IEnumerable<string> fileNames)
        {
            var errors = new List<GenerationValidationError>();
            void Error(string path, string rule, string message) => errors.Add(new GenerationValidationError(path, rule, message));
            var parts = manifest.GetProperty("parts");
            var inherited = manifest.TryGetProperty("extends", out var parent) && parent.ValueKind != JsonValueKind.Null;
            JsonElement Part(string name) => parts.TryGetProperty(name, out var path) ? documents[path.GetString()!] : default;
            JsonElement[] Entries(string part, string entries) => Part(part).ValueKind == JsonValueKind.Object && Part(part).TryGetProperty(entries, out var list) ? list.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            var naming = new HashSet<string>(Entries("naming", "rules").Select(r => r.GetProperty("id").GetString()!), StringComparer.Ordinal);
            var types = new HashSet<string>(Entries("library", "types").Select(r => r.GetProperty("id").GetString()!), StringComparer.Ordinal);
            var libraries = new HashSet<string>(Entries("library", "libraries").Select(r => r.GetProperty("id").GetString()!), StringComparer.Ordinal);
            var rules = parts.TryGetProperty("rules", out var rulePaths) ? rulePaths.EnumerateArray().Select(p => documents[p.GetString()!]).ToArray() : Array.Empty<JsonElement>();
            var deviceTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
                if (!deviceTypes.Add(rule.GetProperty("deviceType").GetString()!)) Error("/parts/rules", "duplicate-id", "Duplicate device type.");
            var files = new HashSet<string>(fileNames, StringComparer.Ordinal);
            var releases = manifest.GetProperty("targets").GetProperty("releases").EnumerateArray().Select(r => r.GetString()!).ToArray();
            foreach (var type in Entries("library", "types"))
            {
                var implementations = type.GetProperty("implementations").EnumerateArray().ToArray();
                foreach (var release in releases)
                    if (!implementations.Any(i => ReleaseMatches(i.GetProperty("releases"), release))) Error("/parts/library/types", "release-coverage", "Type " + type.GetProperty("id").GetString() + " has no implementation for " + release + ".");
                foreach (var implementation in implementations)
                {
                    if (implementation.TryGetProperty("files", out var sources)) foreach (var source in sources.EnumerateArray()) FileReference(source.GetString()!, "/parts/library/types/implementations/files");
                    if (implementation.TryGetProperty("library", out var library) && !inherited && !libraries.Contains(library.GetString()!)) Error("/parts/library/types/implementations/library", "reference", "Unknown library.");
                }
            }
            foreach (var library in Entries("library", "libraries"))
                if (library.GetProperty("provision").GetString() == "bundled") foreach (var file in library.GetProperty("fileNames").EnumerateObject()) FileReference(file.Value.GetString()!, "/parts/library/libraries/fileNames");
            foreach (var model in Entries("modes", "models"))
                foreach (var name in new[] { "implementationType", "dataType" })
                    if (!inherited && !types.Contains(model.GetProperty(name).GetString()!)) Error("/parts/modes/" + name, "reference", "Unknown library type.");
            var alarmClasses = new HashSet<string>(Entries("alarms", "classes").Select(c => c.GetProperty("id").GetString()!), StringComparer.Ordinal);
            foreach (var alarm in Entries("alarms", "templates"))
            {
                if (!inherited && !alarmClasses.Contains(alarm.GetProperty("class").GetString()!)) Error("/parts/alarms/templates/class", "reference", "Unknown alarm class.");
                if (!inherited && !deviceTypes.Contains(alarm.GetProperty("deviceType").GetString()!)) Error("/parts/alarms/templates/deviceType", "reference", "Unknown device type.");
                if (!inherited && (!Part("alarms").GetProperty("texts").TryGetProperty(alarm.GetProperty("textKey").GetString()!, out var texts)
                    || manifest.GetProperty("languages").GetProperty("required").EnumerateArray().Any(l => !texts.TryGetProperty(l.GetString()!, out _)))) Error("/parts/alarms/texts", "language", "Required alarm text or language is missing.");
            }
            foreach (var widget in Entries("hmi", "widgets"))
                if (!inherited && !deviceTypes.Contains(widget.GetProperty("deviceType").GetString()!)) Error("/parts/hmi/widgets/deviceType", "reference", "Unknown device type.");
            foreach (var entry in parts.EnumerateObject())
            {
                var paths = entry.Name == "rules" ? entry.Value.EnumerateArray().Select(v => v.GetString()!) : new[] { entry.Value.GetString()! };
                foreach (var path in paths) Walk(documents[path], path + "#");
            }
            if (errors.Count != 0) throw new GenerationValidationException(errors);

            void FileReference(string path, string location)
            {
                StandardPackageLoader.ValidatePath(path);
                if (!files.Contains(path)) Error(location, "file-reference", "Missing package resource: " + path);
            }
            void Walk(JsonElement value, string path)
            {
                if (value.ValueKind == JsonValueKind.Array) { var i = 0; foreach (var item in value.EnumerateArray()) Walk(item, path + "/" + i++); }
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject())
                    {
                        var child = path + "/" + CanonicalJson.Pointer(property.Name);
                        if (property.Name == "template" && property.Value.ValueKind == JsonValueKind.String
                            && (parts.TryGetProperty("hmi", out var h) && (path.StartsWith(h.GetString()! + "#/screens/", StringComparison.Ordinal) || path.StartsWith(h.GetString()! + "#/widgets/", StringComparison.Ordinal))
                                || parts.TryGetProperty("rules", out var rp) && rp.EnumerateArray().Any(p => path.StartsWith(p.GetString()! + "#", StringComparison.Ordinal)))) FileReference(property.Value.GetString()!, child);
                        Walk(property.Value, child);
                    }
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!;
                    RestrictedPlaceholders.Validate(text, path, Error, key => inherited || naming.Contains(key));
                    if (text.StartsWith("lib:", StringComparison.Ordinal))
                    {
                        var match = Regex.Match(text, @"\Alib:([^/]+)/([^@]+)@(.+)\z", RegexOptions.CultureInvariant);
                        if (!match.Success) Error(path, "reference", "Invalid library type reference.");
                        else if (match.Groups[1].Value == manifest.GetProperty("id").GetString() && !inherited && !types.Contains(match.Groups[2].Value)) Error(path, "reference", "Unknown library type.");
                        else if (match.Groups[1].Value != manifest.GetProperty("id").GetString() && !PackageDependency(match.Groups[1].Value)) Error(path, "reference", "Library package must be declared in extends or dependsOn.");
                    }
                }
            }
            bool PackageDependency(string id) => inherited && parent.GetProperty("package").GetString() == id
                || manifest.TryGetProperty("dependsOn", out var dependencies) && dependencies.EnumerateArray().Any(d => d.GetProperty("package").GetString() == id);
        }

        private static bool ReleaseMatches(JsonElement selection, string release)
        {
            if (selection.ValueKind == JsonValueKind.Array) return selection.EnumerateArray().Any(r => r.GetString() == release);
            return ReleaseMatches(selection.GetString()!, release);
        }

        internal static bool ReleaseMatches(string range, string release)
        {
            if (range == "*") return true;
            var number = decimal.Parse(release == "14sp1" ? "14.1" : release, CultureInfo.InvariantCulture);
            foreach (var term in Regex.Split(range, @"\s+", RegexOptions.CultureInvariant).Where(t => t.Length > 0))
            {
                var match = Regex.Match(term, @"\A(>=|<=|>|<|=|\^)?([0-9]+(?:\.[0-9]+)?)\z", RegexOptions.CultureInvariant);
                if (!match.Success) return false;
                if (!decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var target)) return false;
                var valid = match.Groups[1].Value switch
                {
                    ">=" => number >= target, "<=" => number <= target, ">" => number > target, "<" => number < target,
                    "^" => number >= target && number < decimal.Floor(target) + 1, _ => number == target
                };
                if (!valid) return false;
            }
            return true;
        }
    }
}
