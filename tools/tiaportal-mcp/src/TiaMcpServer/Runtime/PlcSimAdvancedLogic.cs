using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Runtime
{
    // Pure logic for the PLCSIM Advanced channel (no Siemens assembly, no I/O): API path probing order,
    // scenario parsing, value conversion per PLCSIM primitive type and comparison with tolerance.
    // Everything that touches Siemens.Simatic.Simulation.Runtime lives in PlcSimAdvancedChannel.
    public static class PlcSimAdvancedLogic
    {
        public const string ApiFileName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
        public const string ApiEnvironmentVariable = "PLCSIMADV_API_PATH";
        public const int MaxScenarioSteps = 500;
        public const int MaxTagsPerCall = 500;

        public static readonly string[] InstanceActions = { "register", "powerOn", "run", "stop", "powerOff", "memoryReset", "unregister" };
        public static readonly string[] PrimitiveTypes = { "Bool", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64", "Float", "Double", "Char", "WChar" };

        // ------------------------------------------------------------------ API location

        /// <summary>
        /// Candidate DLL paths in probe order: explicit argument, environment variable (file or directory),
        /// then every versioned folder under the given API roots, newest version first. Pure: takes the
        /// directory listing as input so it can be unit-tested without a PLCSIM Advanced installation.
        /// </summary>
        public static List<string> CandidateApiPaths(string? explicitPath, string? environmentValue, IEnumerable<KeyValuePair<string, IEnumerable<string>>> rootsWithVersionFolders)
        {
            var result = new List<string>();
            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                var candidate = p!.Trim().Trim('"');
                if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) candidate = Path.Combine(candidate, ApiFileName);
                if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase)) result.Add(candidate);
            }
            Add(explicitPath);
            Add(environmentValue);
            foreach (var root in rootsWithVersionFolders)
            {
                var folders = root.Value.Select(Path.GetFileName).Where(f => f != null).Select(f => f!)
                    .OrderByDescending(VersionKey).ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var folder in folders) Add(Path.Combine(root.Key, folder));
            }
            return result;
        }

        public static Version VersionKey(string folderName)
        {
            var digits = new string(folderName.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            return Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var v) ? v : new Version(0, 0);
        }

        public static string NormalizeAction(string? action)
        {
            var a = (action ?? "").Trim();
            var hit = InstanceActions.FirstOrDefault(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new ArgumentException("action must be one of: " + string.Join(", ", InstanceActions) + ".");
            return hit;
        }

        public static string RequireInstanceName(string? name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) throw new ArgumentException("instanceName is empty.");
            // Explicit set: Path.GetInvalidFileNameChars() differs per OS and the offline suite also runs on Linux.
            if (n.Length > 64 || n.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0) throw new ArgumentException("instanceName must be 1..64 characters without path separators or wildcard characters.");
            return n;
        }

        // ------------------------------------------------------------------ values

        /// <summary>Parses a JSON object {"\"Tag\"": value, ...} or an array of {name, value}. Names are trimmed; quotes are kept as written.</summary>
        public static List<KeyValuePair<string, JsonNode?>> ParseValueMap(string? json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException(parameterName + " is empty; expected a JSON object of tag name -> value.");
            JsonNode? node;
            try { node = JsonNode.Parse(json!); }
            catch (JsonException ex) { throw new ArgumentException(parameterName + " is not valid JSON: " + ex.Message); }
            var list = new List<KeyValuePair<string, JsonNode?>>();
            if (node is JsonObject obj)
            {
                foreach (var kv in obj) list.Add(new KeyValuePair<string, JsonNode?>(kv.Key.Trim(), kv.Value));
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (!(item is JsonObject o) || o["name"] == null) throw new ArgumentException(parameterName + " array items must be {\"name\":..., \"value\":...}.");
                    list.Add(new KeyValuePair<string, JsonNode?>(o["name"]!.ToString().Trim(), o["value"]));
                }
            }
            else throw new ArgumentException(parameterName + " must be a JSON object or array.");
            if (list.Count == 0) throw new ArgumentException(parameterName + " contains no entries.");
            if (list.Count > MaxTagsPerCall) throw new ArgumentException(parameterName + " has " + list.Count + " entries; limit is " + MaxTagsPerCall + ".");
            foreach (var kv in list) if (kv.Key.Length == 0) throw new ArgumentException(parameterName + " contains an empty tag name.");
            return list;
        }

        public static List<string> ParseNameList(string? json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var trimmed = json!.Trim();
            List<string> names;
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(trimmed); }
                catch (JsonException ex) { throw new ArgumentException(parameterName + " is not valid JSON: " + ex.Message); }
                names = (node as JsonArray ?? throw new ArgumentException(parameterName + " must be a JSON array of names.")).Select(n => n?.ToString().Trim() ?? "").ToList();
            }
            else names = trimmed.Split(',').Select(s => s.Trim()).ToList();
            names = names.Where(n => n.Length > 0).ToList();
            if (names.Count > MaxTagsPerCall) throw new ArgumentException(parameterName + " has " + names.Count + " names; limit is " + MaxTagsPerCall + ".");
            return names;
        }

        /// <summary>Converts a JSON value to the CLR value the PLCSIM primitive type expects (SDataValue member).</summary>
        public static object ConvertValue(JsonNode? value, string primitiveType)
        {
            if (value == null) throw new ArgumentException("Value for a " + primitiveType + " tag is null.");
            string text = value is JsonValue ? value.ToString() : throw new ArgumentException("Value must be a JSON scalar, got " + value.GetType().Name + ".");
            var inv = CultureInfo.InvariantCulture;
            switch (primitiveType)
            {
                case "Bool":
                    if (bool.TryParse(text, out var b)) return b;
                    if (text == "1") return true; if (text == "0") return false;
                    throw new ArgumentException("Bool value must be true/false/1/0, got '" + text + "'.");
                case "Int8": return checked((sbyte)ParseLong(text, "Int8"));
                case "Int16": return checked((short)ParseLong(text, "Int16"));
                case "Int32": return checked((int)ParseLong(text, "Int32"));
                case "Int64": return ParseLong(text, "Int64");
                case "UInt8": return checked((byte)ParseULong(text, "UInt8"));
                case "UInt16": return checked((ushort)ParseULong(text, "UInt16"));
                case "UInt32": return checked((uint)ParseULong(text, "UInt32"));
                case "UInt64": return ParseULong(text, "UInt64");
                case "Float": return float.TryParse(text, NumberStyles.Float, inv, out var f) ? f : throw new ArgumentException("Float value expected, got '" + text + "'.");
                case "Double": return double.TryParse(text, NumberStyles.Float, inv, out var d) ? d : throw new ArgumentException("Double value expected, got '" + text + "'.");
                case "Char": return text.Length == 1 ? (object)(sbyte)text[0] : throw new ArgumentException("Char value must be one character.");
                case "WChar": return text.Length == 1 ? (object)text[0] : throw new ArgumentException("WChar value must be one character.");
                default: throw new ArgumentException("Unsupported PLCSIM primitive type '" + primitiveType + "' (structs/arrays must be written per element).");
            }
        }

        private static long ParseLong(string text, string type)
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) return (long)Math.Round(d);
            if (text.StartsWith("16#", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(text.Substring(3).Replace("_", ""), 16);
            throw new ArgumentException(type + " value expected, got '" + text + "'.");
        }

        private static ulong ParseULong(string text, string type)
        {
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (text.StartsWith("16#", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt64(text.Substring(3).Replace("_", ""), 16);
            throw new ArgumentException(type + " value expected, got '" + text + "'.");
        }

        public static JsonNode? ToJson(object? value)
        {
            switch (value)
            {
                case null: return null;
                case bool b: return b;
                case sbyte i8: return i8;
                case short i16: return i16;
                case int i32: return i32;
                case long i64: return i64;
                case byte u8: return u8;
                case ushort u16: return u16;
                case uint u32: return u32;
                case ulong u64: return u64;
                case float f: return f;
                case double d: return d;
                case char c: return c.ToString();
                default: return value.ToString();
            }
        }

        public static bool ValuesMatch(object? actual, JsonNode? expected, double tolerance)
        {
            if (actual == null || expected == null) return actual == null && expected == null;
            switch (actual)
            {
                case bool b:
                    return bool.TryParse(expected.ToString(), out var eb) ? eb == b : expected.ToString() == (b ? "1" : "0");
                case float _:
                case double _:
                    return double.TryParse(expected.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ed) && Math.Abs(Convert.ToDouble(actual, CultureInfo.InvariantCulture) - ed) <= Math.Max(tolerance, 0);
                case char c:
                    return expected.ToString() == c.ToString();
                default:
                    if (decimal.TryParse(expected.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var em))
                    {
                        try { return Math.Abs(Convert.ToDecimal(actual, CultureInfo.InvariantCulture) - em) <= (decimal)Math.Max(tolerance, 0); }
                        catch (OverflowException) { return actual.ToString() == expected.ToString(); }
                    }
                    return string.Equals(actual.ToString(), expected.ToString(), StringComparison.Ordinal);
            }
        }

        // ------------------------------------------------------------------ scenarios

        public sealed class ScenarioStep
        {
            public int Index;
            public string Kind = "";           // write | cycles | wait | assert | powerOn | run | stop
            public List<KeyValuePair<string, JsonNode?>> Values = new List<KeyValuePair<string, JsonNode?>>();
            public int Count;                  // cycles or milliseconds
            public double Tolerance;
            public string Note = "";
        }

        public sealed class Scenario
        {
            public string Instance = "";
            public string Mode = "singleStep";   // singleStep | default
            public bool StopOnFailure = true;
            public List<ScenarioStep> Steps = new List<ScenarioStep>();
        }

        /// <summary>
        /// {"instance":"PLC_1","mode":"singleStep"|"default","stopOnFailure":true,
        ///  "steps":[{"write":{"\"Start\"":true}}, {"cycles":5}, {"waitMs":200}, {"assert":{"\"Running\"":true},"tolerance":0.01,"note":"..."},
        ///           {"powerOn":true},{"run":true},{"stop":true}]}
        /// </summary>
        public static Scenario ParseScenario(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("scenarioJson is empty.");
            JsonNode? node;
            try { node = JsonNode.Parse(json!); }
            catch (JsonException ex) { throw new ArgumentException("scenarioJson is not valid JSON: " + ex.Message); }
            var obj = node as JsonObject ?? throw new ArgumentException("scenarioJson must be a JSON object.");
            var s = new Scenario { Instance = RequireInstanceName(obj["instance"]?.ToString()) };
            var mode = obj["mode"]?.ToString() ?? "singleStep";
            if (!mode.Equals("singleStep", StringComparison.OrdinalIgnoreCase) && !mode.Equals("default", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("mode must be singleStep or default.");
            s.Mode = mode.Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : "singleStep";
            if (obj["stopOnFailure"] is JsonValue sof) s.StopOnFailure = sof.GetValue<bool>();
            var steps = obj["steps"] as JsonArray ?? throw new ArgumentException("scenarioJson.steps must be an array.");
            if (steps.Count == 0) throw new ArgumentException("scenarioJson.steps is empty.");
            if (steps.Count > MaxScenarioSteps) throw new ArgumentException("scenarioJson has " + steps.Count + " steps; limit is " + MaxScenarioSteps + ".");
            var index = 0;
            foreach (var item in steps)
            {
                index++;
                var o = item as JsonObject ?? throw new ArgumentException("Step " + index + " must be an object.");
                var step = new ScenarioStep { Index = index, Note = o["note"]?.ToString() ?? "" };
                if (o["tolerance"] is JsonValue tol) step.Tolerance = tol.GetValue<double>();
                var kinds = new List<string>();
                if (o["write"] != null) { kinds.Add("write"); step.Values = ParseValueMap(o["write"]!.ToJsonString(), "steps[" + index + "].write"); }
                if (o["assert"] != null) { kinds.Add("assert"); step.Values = ParseValueMap(o["assert"]!.ToJsonString(), "steps[" + index + "].assert"); }
                if (o["cycles"] != null) { kinds.Add("cycles"); step.Count = o["cycles"]!.GetValue<int>(); if (step.Count < 1 || step.Count > 100000) throw new ArgumentException("Step " + index + ": cycles must be 1..100000."); }
                if (o["waitMs"] != null) { kinds.Add("wait"); step.Count = o["waitMs"]!.GetValue<int>(); if (step.Count < 1 || step.Count > 60000) throw new ArgumentException("Step " + index + ": waitMs must be 1..60000."); }
                foreach (var k in new[] { "powerOn", "run", "stop" }) if (o[k] != null) kinds.Add(k);
                if (kinds.Count != 1) throw new ArgumentException("Step " + index + " must contain exactly one of write, assert, cycles, waitMs, powerOn, run, stop (found " + kinds.Count + ").");
                step.Kind = kinds[0];
                s.Steps.Add(step);
            }
            return s;
        }

        public static JsonObject ScenarioPlan(Scenario s) => new JsonObject
        {
            ["instance"] = s.Instance,
            ["mode"] = s.Mode,
            ["stopOnFailure"] = s.StopOnFailure,
            ["stepCount"] = s.Steps.Count,
            ["writes"] = s.Steps.Count(x => x.Kind == "write"),
            ["asserts"] = s.Steps.Count(x => x.Kind == "assert"),
            ["cycles"] = s.Steps.Where(x => x.Kind == "cycles").Sum(x => x.Count),
            ["waitMs"] = s.Steps.Where(x => x.Kind == "wait").Sum(x => x.Count),
            ["modeChanges"] = new JsonArray(s.Steps.Where(x => x.Kind == "powerOn" || x.Kind == "run" || x.Kind == "stop").Select(x => (JsonNode)(x.Index + ":" + x.Kind)).ToArray())
        };
    }
}
