using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.57: the pure part of PreflightToolCall. Given a tool's parameter specs (name, kind, required, default,
    // description) and the arguments an AI caller intends to send, it says what CallTool / the SDK binder would
    // object to - before anything reaches TIA. Zero dependencies so the offline suite can feed it real inputs.
    public static class PreflightLogic
    {
        public sealed class ParameterSpec
        {
            public string Name { get; }
            /// <summary>string | boolean | integer | number | &lt;other type name&gt; (the FriendlyTypeName vocabulary of the bridge).</summary>
            public string Kind { get; }
            public bool Required { get; }
            public string? DefaultText { get; }
            public string Description { get; }
            public ParameterSpec(string name, string kind, bool required, string? defaultText, string description)
            { Name = name; Kind = kind; Required = required; DefaultText = defaultText; Description = description ?? ""; }
        }

        public sealed class Report
        {
            public List<string> Missing { get; } = new List<string>();
            public List<string> Unknown { get; } = new List<string>();
            public List<string> CaseFixes { get; } = new List<string>();
            public List<string> TypeProblems { get; } = new List<string>();
            public List<string> Coercions { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public bool DryRunSupported { get; set; }
            public bool? DryRunGiven { get; set; }
            public bool DryRunDefault { get; set; } = true;
            public List<string> ConfirmFlags { get; } = new List<string>();
            public List<string> ConfirmFlagsSet { get; } = new List<string>();
            /// <summary>The call would bind (possibly after the bridge's coercions and case fixes).</summary>
            public bool Ok => Missing.Count == 0 && Unknown.Count == 0 && TypeProblems.Count == 0;
            /// <summary>True when the call, as given, would actually change something (dryRun=false or no dryRun at all on a writing tool).</summary>
            public bool Effective => !DryRunSupported || DryRunGiven == false || (DryRunGiven == null && !DryRunDefault);
        }

        public static Report Analyze(IReadOnlyList<ParameterSpec> specs, JsonObject args)
        {
            var report = new Report();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in args)
            {
                var spec = specs.FirstOrDefault(s => string.Equals(s.Name, kv.Key, StringComparison.Ordinal));
                if (spec == null)
                {
                    var byCase = specs.FirstOrDefault(s => string.Equals(s.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (byCase != null) { report.CaseFixes.Add(kv.Key + " -> " + byCase.Name); spec = byCase; }
                }
                if (spec == null)
                {
                    var near = Nearest(kv.Key, specs.Select(s => s.Name).ToList());
                    report.Unknown.Add(kv.Key + (near != null ? " (did you mean '" + near + "'?)" : ""));
                    continue;
                }
                seen.Add(spec.Name);
                CheckValue(spec, kv.Value, report);
            }
            foreach (var s in specs)
            {
                if (s.Required && !seen.Contains(s.Name)) report.Missing.Add(s.Name);
                if (string.Equals(s.Name, "dryRun", StringComparison.Ordinal))
                {
                    report.DryRunSupported = true;
                    report.DryRunDefault = !string.Equals(s.DefaultText, "false", StringComparison.OrdinalIgnoreCase);
                    if (seen.Contains("dryRun")) report.DryRunGiven = ReadBool(args["dryRun"]);
                }
                if (s.Kind == "boolean" && s.Name.StartsWith("confirm", StringComparison.Ordinal))
                {
                    report.ConfirmFlags.Add(s.Name);
                    if (seen.Contains(s.Name) && ReadBool(args[s.Name]) == true) report.ConfirmFlagsSet.Add(s.Name);
                }
            }
            if (report.DryRunSupported && report.Effective && report.ConfirmFlags.Count > 0 && report.ConfirmFlagsSet.Count == 0)
                report.Warnings.Add("dryRun=false without " + string.Join(" / ", report.ConfirmFlags) + "=true: the tool will refuse to execute (set the confirm flag only after the user agreed).");
            return report;
        }

        private static void CheckValue(ParameterSpec spec, JsonNode? value, Report report)
        {
            if (value == null) { report.Warnings.Add(spec.Name + " is null - treated as not given" + (spec.Required ? " (required)" : "") + "."); if (spec.Required) report.Missing.Add(spec.Name); return; }
            switch (spec.Kind)
            {
                case "string":
                    if (value is JsonObject || value is JsonArray)
                        report.Coercions.Add(spec.Name + ": object/array given for a string parameter - CallTool sends it as JSON text; a direct call needs the JSON string.");
                    else if (value is JsonValue sv && !sv.TryGetValue<string>(out _))
                        report.Coercions.Add(spec.Name + ": " + value.ToJsonString() + " given for a string parameter - sent as its text.");
                    else
                    {
                        var text = value.GetValue<string>();
                        if (text.Length == 0 && spec.DefaultText != null && spec.DefaultText.Length > 0 && spec.DefaultText != "\"\"")
                            report.Coercions.Add(spec.Name + ": empty string means the default " + spec.DefaultText + ".");
                        CheckAlternatives(spec, text, report);
                    }
                    break;
                case "boolean":
                    if (value is JsonValue bv)
                    {
                        if (bv.TryGetValue<bool>(out _)) break;
                        if (bv.TryGetValue<string>(out var bs) && (bool.TryParse(bs.Trim(), out _) || bs.Trim() == "0" || bs.Trim() == "1")) { report.Coercions.Add(spec.Name + ": '" + bs + "' given for a boolean - CallTool converts it; a direct call needs true/false."); break; }
                        if (bv.TryGetValue<int>(out var bi) && (bi == 0 || bi == 1)) { report.Coercions.Add(spec.Name + ": " + bi + " given for a boolean - CallTool converts it; a direct call needs true/false."); break; }
                    }
                    report.TypeProblems.Add(spec.Name + " must be a boolean (true/false), got " + value.ToJsonString() + ".");
                    break;
                case "integer":
                case "number":
                    if (value is JsonValue nv)
                    {
                        if (nv.TryGetValue<double>(out var d))
                        {
                            if (spec.Kind == "integer" && Math.Abs(d - Math.Round(d)) > 1e-9) report.TypeProblems.Add(spec.Name + " must be an integer, got " + value.ToJsonString() + ".");
                            break;
                        }
                        if (nv.TryGetValue<string>(out var ns) && double.TryParse(ns.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { report.Coercions.Add(spec.Name + ": '" + ns + "' given for a " + spec.Kind + " - CallTool converts it; a direct call needs a number."); break; }
                    }
                    report.TypeProblems.Add(spec.Name + " must be a " + spec.Kind + ", got " + value.ToJsonString() + ".");
                    break;
                default:
                    break; // arrays / JsonElement / other: left to the binder
            }
        }

        private const string Word = @"[A-Za-z][A-Za-z0-9_+-]*";
        // "a | b | c" anywhere (pipes are not prose); "a/b/c" only right after "one of" or the parameter's colon (slashes are);
        // "(a, b, c)" as a bare parenthesised list without "e.g.".
        private static readonly Regex PipeList = new Regex(@"(?<list>" + Word + @"(?:\s*\|\s*" + Word + @")+)", RegexOptions.Compiled);
        private static readonly Regex SlashList = new Regex(@"(?:one of:?|:)\s*\(?\s*(?<list>" + Word + @"(?:\s*/\s*" + Word + @")+)", RegexOptions.Compiled);
        // Comma lists only with plain identifiers: "(substring, case-insensitive)" is prose, "(None, Override, SkipInactiveCultures)" is a list.
        private const string Identifier = @"[A-Za-z][A-Za-z0-9_]*";
        private static readonly Regex CommaList = new Regex(@"\(\s*(?<list>" + Identifier + @"(?:\s*,\s*" + Identifier + @")+)\s*\)", RegexOptions.Compiled);

        /// <summary>The documented alternatives of an enum-like parameter ("action: read | create | delete", "kind: udt|tagtable|fc", "(None, Override)"), or empty.</summary>
        public static IReadOnlyList<string> Alternatives(string? description)
        {
            if (string.IsNullOrEmpty(description)) return Array.Empty<string>();
            foreach (var pair in new[] { (PipeList, '|'), (SlashList, '/'), (CommaList, ',') })
            {
                var m = pair.Item1.Match(description!);
                if (!m.Success) continue;
                var list = m.Groups["list"].Value.Split(pair.Item2).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (list.Count >= 2 && list.All(x => x.Length <= 40)) return list;
            }
            return Array.Empty<string>();
        }

        private static void CheckAlternatives(ParameterSpec spec, string text, Report report)
        {
            if (text.Length == 0) return;
            var alternatives = Alternatives(spec.Description);
            if (alternatives.Count == 0) return;
            if (alternatives.Any(a => string.Equals(a, text, StringComparison.Ordinal))) return;
            var ci = alternatives.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
            if (ci != null) { report.Coercions.Add(spec.Name + ": '" + text + "' -> '" + ci + "' (CallTool retries with the documented spelling; a direct call needs it exactly)."); return; }
            report.Warnings.Add(spec.Name + ": '" + text + "' is not among the documented values " + string.Join(" | ", alternatives) + ".");
        }

        private static bool? ReadBool(JsonNode? node)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s)) { if (bool.TryParse(s.Trim(), out b)) return b; if (s.Trim() == "1") return true; if (s.Trim() == "0") return false; }
                if (v.TryGetValue<int>(out var i)) { if (i == 1) return true; if (i == 0) return false; }
            }
            return null;
        }

        /// <summary>Closest parameter name by shared prefix / containment; null when nothing is close.</summary>
        public static string? Nearest(string given, IReadOnlyList<string> names)
        {
            if (string.IsNullOrEmpty(given) || names.Count == 0) return null;
            string? best = null; int bestScore = 0;
            foreach (var n in names)
            {
                int score = 0;
                if (n.IndexOf(given, StringComparison.OrdinalIgnoreCase) >= 0 || given.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) score = 100 + Math.Min(n.Length, given.Length);
                else
                {
                    int p = 0, max = Math.Min(n.Length, given.Length);
                    while (p < max && char.ToLowerInvariant(n[p]) == char.ToLowerInvariant(given[p])) p++;
                    if (p >= 4) score = p;
                    var tail = TailWord(n); var givenTail = TailWord(given);
                    if (tail.Length >= 4 && string.Equals(tail, givenTail, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 50);
                }
                if (score > bestScore) { bestScore = score; best = n; }
            }
            return best;
        }

        private static string TailWord(string s)
        {
            int i = s.Length - 1;
            while (i > 0 && !char.IsUpper(s[i])) i--;
            return s.Substring(i);
        }

        /// <summary>What a caller must keep in mind for one operation class (the third tag of a tool description).</summary>
        public static IReadOnlyList<string> Precautions(string operation, bool dryRunSupported)
        {
            var list = new List<string>();
            switch ((operation ?? "").ToUpperInvariant())
            {
                case "WRITE":
                    list.Add("Modifies the open project offline; nothing is saved, compiled or downloaded by itself - CompileSoftware then SaveProject afterwards.");
                    if (dryRunSupported) list.Add("dryRun=true (default) previews; run the preview first, then repeat with dryRun=false once it is clean.");
                    break;
                case "ONLINE-WRITE":
                    list.Add("Changes a real device or runtime (download / upload / operating mode / value write). In the maintainer's environment this is allowed against PLCSIM Advanced only, never a real CPU.");
                    if (dryRunSupported) list.Add("dryRun=true (default) previews; execution also needs the confirm flag.");
                    break;
                case "ONLINE":
                    list.Add("Contacts a PLC / device / runtime read-only (scan, online state, compare). Needs the route (pgPcInterface / IP) to be right; ReadTransferRoutes shows the choices.");
                    break;
                case "EXECUTE":
                    list.Add("Runs a compile / test / self-test and returns its messages; read ErrorCount before continuing.");
                    break;
                case "FILE":
                    list.Add("Reads or writes files on the engine's machine (the TIA host, not the client); paths are host paths.");
                    break;
                case "OFFLINE":
                    list.Add("Pure offline computation; no TIA session needed.");
                    break;
                case "SESSION":
                    list.Add("Session / discovery call; does not change the project.");
                    break;
                case "READ":
                    list.Add("Reads the open project; no change.");
                    break;
                default:
                    break;
            }
            return list;
        }

        /// <summary>Whether the operation class needs a bound project (session and offline calls do not).</summary>
        public static bool NeedsProject(string operation, string toolName)
        {
            var op = (operation ?? "").ToUpperInvariant();
            if (op == "SESSION" || op == "OFFLINE") return false;
            // Tools that read the environment rather than a project.
            var exempt = new[] { "Bootstrap", "Doctor", "GetState", "FindTools", "CallTool", "ListToolCategories", "GetAuthoringGuide", "PreflightToolCall", "CheckForUpdate", "ReadPortalInfo", "ListPortalProcessProjects", "SearchHardwareCatalog", "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports", "WritePlcSclSourceFile", "GenerateErrorReport", "GenerateAcceptanceReport", "RunCapabilitySelfTest", "RunOnlineMonitoringSafetySelfTest", "ReadPlcSimAdvancedInstances", "ManagePlcSimAdvancedInstance", "ReadPlcSimAdvancedTags", "WritePlcSimAdvancedTags", "RunPlcSimAdvancedTestScenario", "EnsureOpennessUserGroup" };
            return !exempt.Contains(toolName, StringComparer.OrdinalIgnoreCase);
        }
    }
}
