using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    // 2.7.57: PreflightToolCall (pure analysis + the bridge wiring on the linked tools), the example table, and the
    // update check's version / release-JSON logic. None of it touches TIA.
    internal static class PreflightAndUpdateTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

        private static PreflightLogic.ParameterSpec S(string name, string kind, bool required, string? def = null, string desc = "")
            => new PreflightLogic.ParameterSpec(name, kind, required, def, desc);

        internal static void Run(Action<bool, string> check)
        {
            // ---- PreflightLogic.Analyze: the mistakes an AI caller actually makes ----
            var specs = new List<PreflightLogic.ParameterSpec>
            {
                S("softwarePath", "string", true, null, "softwarePath: PLC software path"),
                S("action", "string", false, "\"read\"", "action: read | create | delete"),
                S("kind", "string", true, null, "kind: udt|tagtable|globaldb|fc|fb"),
                S("importOption", "string", false, "\"Override\"", "importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures)"),
                S("limit", "integer", false, "200"),
                S("ratio", "number", false, "1"),
                S("dryRun", "boolean", false, "true"),
                S("confirmDelete", "boolean", false, "false"),
                S("propertiesJson", "string", false, "\"{}\""),
            };
            var ok = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\"}"));
            check(ok.Ok && ok.Missing.Count == 0 && ok.Unknown.Count == 0 && ok.DryRunSupported && ok.DryRunGiven == null && !ok.Effective, "preflight: clean call binds, dryRun default = preview");

            var missing = PreflightLogic.Analyze(specs, O("{\"kind\":\"fc\"}"));
            check(!missing.Ok && missing.Missing.SequenceEqual(new[] { "softwarePath" }), "preflight: missing required parameter named");

            var cased = PreflightLogic.Analyze(specs, O("{\"SoftwarePath\":\"PLC_1\",\"Kind\":\"fc\"}"));
            check(cased.Ok && cased.CaseFixes.Count == 2 && cased.CaseFixes[0] == "SoftwarePath -> softwarePath" && cased.Missing.Count == 0, "preflight: PascalCase names map to the parameters (case fix, not missing)");

            var unknown = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"softwarepath2\":1,\"blockPath\":\"x\"}"));
            check(!unknown.Ok && unknown.Unknown.Count == 2 && unknown.Unknown[0].StartsWith("softwarepath2 (did you mean 'softwarePath'?)"), "preflight: unknown parameter with nearest suggestion");

            var types = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"limit\":\"12\",\"dryRun\":\"false\",\"ratio\":\"abc\"}"));
            check(!types.Ok && types.TypeProblems.Count == 1 && types.TypeProblems[0].StartsWith("ratio must be a number") && types.Coercions.Count(c => c.StartsWith("limit:")) == 1 && types.Coercions.Count(c => c.StartsWith("dryRun:")) == 1, "preflight: string numbers/booleans are coercions, garbage is a type problem");
            check(types.DryRunGiven == false && types.Effective, "preflight: dryRun given as 'false' string is read as executing");

            var fraction = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"limit\":2.5}"));
            check(fraction.TypeProblems.Count == 1 && fraction.TypeProblems[0].StartsWith("limit must be an integer"), "preflight: fractional value for an integer refused");

            var enums = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"FC\",\"action\":\"Create\",\"importOption\":\"Overwrite\"}"));
            check(enums.Ok && enums.Coercions.Count(c => c.StartsWith("kind: 'FC' -> 'fc'")) == 1 && enums.Coercions.Count(c => c.StartsWith("action: 'Create' -> 'create'")) == 1, "preflight: case-only enum mismatch reported as coercion");
            check(enums.Warnings.Count(w => w.StartsWith("importOption: 'Overwrite' is not among the documented values None | Override | SkipInactiveCultures")) == 1, "preflight: value outside the documented alternatives warned");

            var confirm = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"dryRun\":false}"));
            check(confirm.Effective && confirm.ConfirmFlags.SequenceEqual(new[] { "confirmDelete" }) && confirm.Warnings.Count(w => w.StartsWith("dryRun=false without confirmDelete=true")) == 1, "preflight: executing without the confirm flag warned");
            var confirmed = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"dryRun\":false,\"confirmDelete\":true}"));
            check(confirmed.Effective && confirmed.ConfirmFlagsSet.SequenceEqual(new[] { "confirmDelete" }) && confirmed.Warnings.Count == 0, "preflight: confirm flag set, no warning");

            var asObject = PreflightLogic.Analyze(specs, O("{\"softwarePath\":\"PLC_1\",\"kind\":\"fc\",\"propertiesJson\":{\"Comment\":\"x\"},\"action\":\"\"}"));
            check(asObject.Ok && asObject.Coercions.Count(c => c.StartsWith("propertiesJson: object/array given for a string parameter")) == 1 && asObject.Coercions.Count(c => c.StartsWith("action: empty string means the default \"read\"")) == 1, "preflight: object for *Json string and '' for keyword default explained");

            var nullValue = PreflightLogic.Analyze(specs, O("{\"softwarePath\":null,\"kind\":\"fc\"}"));
            check(!nullValue.Ok && nullValue.Missing.SequenceEqual(new[] { "softwarePath" }), "preflight: null for a required parameter counts as missing");

            // ---- alternatives parser ----
            check(PreflightLogic.Alternatives("action: read | create | delete").SequenceEqual(new[] { "read", "create", "delete" }), "preflight: pipe list parsed");
            check(PreflightLogic.Alternatives("trigger: when to apply the write — Permanent | PermanentAtStart | OnceOnlyAtStart (default: Permanent)").SequenceEqual(new[] { "Permanent", "PermanentAtStart", "OnceOnlyAtStart" }), "preflight: pipe list after prose parsed");
            check(PreflightLogic.Alternatives("kind: udt|tagtable|globaldb|fc|fb").Count == 5, "preflight: unspaced pipe list parsed");
            check(PreflightLogic.Alternatives("action: register/powerOn/run").SequenceEqual(new[] { "register", "powerOn", "run" }), "preflight: slash list after the colon parsed");
            check(PreflightLogic.Alternatives("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)").Count == 4, "preflight: parenthesised comma list parsed");
            check(PreflightLogic.Alternatives("softwarePath: defines the path in the project structure to the plc software").Count == 0, "preflight: prose yields no alternatives");
            check(PreflightLogic.Alternatives("name, IP, MAC, device series on one interface (name, IP, MAC, device series)").Count == 0, "preflight: comma prose in parentheses with a two-word item is not a list");
            check(PreflightLogic.Alternatives("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)").Count == 0, "preflight: e.g.-lists are examples, not alternatives");
            check(PreflightLogic.Alternatives("pgPcInterface: e.g. 'PLCSIM' or 'Realtek'").Count == 0, "preflight: quoted examples are not alternatives");
            check(PreflightLogic.Alternatives("pgPcInterface: optional PG/PC interface name (substring, case-insensitive), e.g. 'PLCSIM' or 'Realtek'").Count == 0, "preflight: hyphenated prose in parentheses is not a list (real-machine false positive)");
            check(PreflightLogic.Alternatives("action: read (default) or delete").Count == 0, "preflight: '(default)' is not a list");

            // ---- nearest name ----
            check(PreflightLogic.Nearest("softwarepath", new[] { "softwarePath", "blockPath" }) == "softwarePath", "preflight: nearest by containment");
            check(PreflightLogic.Nearest("blockName", new[] { "softwarePath", "blockPath" }) == "blockPath", "preflight: nearest by prefix");
            check(PreflightLogic.Nearest("tableName", new[] { "softwarePath", "watchTableName" }) == "watchTableName", "preflight: nearest by containment of the given name");
            check(PreflightLogic.Nearest("zzz", new[] { "softwarePath" }) == null, "preflight: nothing near -> null");

            // ---- precautions / prerequisites ----
            check(PreflightLogic.Precautions("WRITE", true).Count == 2 && PreflightLogic.Precautions("ONLINE-WRITE", false).Count == 1 && PreflightLogic.Precautions("READ", false).Count == 1 && PreflightLogic.Precautions("", false).Count == 0, "preflight: precaution text per operation class");
            check(PreflightLogic.NeedsProject("WRITE", "ImportBlock") && !PreflightLogic.NeedsProject("SESSION", "Connect") && !PreflightLogic.NeedsProject("OFFLINE", "BuildPlcGlobalDbJson") && !PreflightLogic.NeedsProject("READ", "SearchHardwareCatalog") && !PreflightLogic.NeedsProject("ONLINE", "ReadPlcSimAdvancedTags"), "preflight: project prerequisite only for project-bound operations");

            // ---- the bridge wiring on the linked tools (no portal in this suite -> prerequisites unknown) ----
            var probe = McpServer.PreflightToolCall("updateunifiedruntimesettings", "{\"SoftwarePath\":\"HMI\",\"changesJson\":{\"BitSelection\":true},\"dryRun\":\"false\"}");
            check(probe.Meta!["toolFound"]!.GetValue<bool>() && probe.Meta["tool"]!.GetValue<string>() == "UpdateUnifiedRuntimeSettings" && probe.Meta["ok"]!.GetValue<bool>() == false, "preflight tool: case-insensitive name resolved, missing expectedProject makes it not ok");
            check(probe.Meta["missing"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "expectedProject" }) && probe.Meta["caseFixes"]!.AsArray().Count == 1 && probe.Meta["coercions"]!.AsArray().Count == 2, "preflight tool: missing / case fix / coercions reported");
            check(probe.Meta["dryRun"]!["supported"]!.GetValue<bool>() && probe.Meta["dryRun"]!["effective"]!.GetValue<bool>() && probe.Meta["operation"]!.GetValue<string>() == "WRITE" && probe.Meta["domain"]!.GetValue<string>() == "HMI-Unified", "preflight tool: dryRun=false marked as executing, taxonomy read from the description");
            check(probe.Message!.StartsWith("NOT READY") && probe.Items!.Any(i => i.StartsWith("Signature: UpdateUnifiedRuntimeSettings(softwarePath: string, expectedProject: string, changesJson: string, dryRun?: boolean = true")), "preflight tool: verdict and signature line");
            check(probe.Meta["prerequisites"]!["needsProject"]!.GetValue<bool>() && probe.Meta["prerequisites"]!["satisfied"] == null, "preflight tool: session state unknown offline -> prerequisite undecided, not failed");

            var ready = McpServer.PreflightToolCall("UpdateUnifiedRuntimeSettings", "{\"softwarePath\":\"HMI\",\"expectedProject\":\"P\",\"changesJson\":\"{}\"}");
            check(ready.Meta!["ok"]!.GetValue<bool>() && ready.Meta["success"]!.GetValue<bool>() && ready.Message!.StartsWith("READY") && ready.Message.Contains("preview"), "preflight tool: clean preview call is READY");

            var typo = McpServer.PreflightToolCall("UpdateUnifiedRuntimeSetting", "{}");
            check(!typo.Meta!["toolFound"]!.GetValue<bool>() && typo.Message!.Contains("UpdateUnifiedRuntimeSettings"), "preflight tool: near miss suggests the real name");
            var noArgs = McpServer.PreflightToolCall("ProbeResult", "");
            check(noArgs.Meta!["toolFound"]!.GetValue<bool>() && noArgs.Meta["missing"]!.AsArray().Count == 1, "preflight tool: empty arguments -> the required parameter is listed as missing");
            var badJson = McpServer.PreflightToolCall("ProbeResult", "[1,2]");
            check(!badJson.Meta!["ok"]!.GetValue<bool>() && badJson.Message!.StartsWith("argumentsJson must be a JSON object"), "preflight tool: non-object arguments refused with the signature");
            check(!McpServer.PreflightToolCall("", "{}").Meta!["success"]!.GetValue<bool>(), "preflight tool: empty name refused");

            // ---- example table ----
            check(ToolExamples.All.Count >= 70 && ToolExamples.Find("downloadtoplc") != null && ToolExamples.Find("NoSuchTool") == null, "examples: table populated, lookup case-insensitive");
            foreach (var e in ToolExamples.All)
                check(JsonNode.Parse(e.ArgumentsJson) is JsonObject, "examples: '" + e.Tool + "' is a JSON object");
            var decorated = ToolExamples.Decorate("GetBlocks", "[L1][PLC-Software][READ] List blocks.");
            check(decorated.StartsWith("[L1][PLC-Software][READ] List blocks. Example: {\"softwarePath\":\"PLC_1\",\"regexName\":\"FB_.*\"} (") && decorated.EndsWith(")."), "examples: description decorated with the example sentence");
            check(ReferenceEquals(ToolExamples.Decorate("ProbeResult", "x"), "x") || ToolExamples.Decorate("ProbeResult", "x") == "x", "examples: tools without an example keep their description");
            // Validate every example against the tools this suite links (the build gate validates all of them against the engine).
            IReadOnlyList<KeyValuePair<string, bool>>? Specs(string tool)
            {
                var method = typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => string.Equals(m.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>()?.Name ?? m.Name, tool, StringComparison.Ordinal)
                                         && m.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>() != null);
                if (method == null) return new List<KeyValuePair<string, bool>>();   // not linked here: treat as "any parameters" (the gate checks the real one)
                return method.GetParameters().Where(p => !McpServer.IsInfrastructureParameter(p.ParameterType)).Select(p => new KeyValuePair<string, bool>(p.Name!, !p.HasDefaultValue)).ToList();
            }
            var linkedProblems = ToolExamples.ValidateAgainst(tool => Specs(tool) is { Count: > 0 } s ? s : null).Where(p => !p.EndsWith("no tool of that name (example is stale)")).ToList();
            check(linkedProblems.Count == 0, "examples: linked tools' examples fit their signatures" + (linkedProblems.Count > 0 ? ": " + string.Join("; ", linkedProblems) : ""));
            var gate = ToolExamples.ValidateAgainst(tool => tool == "GetBlocks" ? new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>("softwarePath", true), new KeyValuePair<string, bool>("regex", false) } : null);
            check(gate.Any(p => p == "GetBlocks: example uses 'regexName' which is not a parameter (exact spelling required)") && gate.Any(p => p.EndsWith("no tool of that name (example is stale)")), "examples: validation flags wrong parameter names and stale tools (sentinel)");

            // ---- CallTool / FindTools carry the example ----
            var refusal = McpServer.CallTool("UpdateUnifiedRuntimeSettings", "{\"softwarePath\":\"HMI\"}");
            check(refusal.Message!.Contains("missing required argument(s): expectedProject, changesJson") && refusal.Message.Contains("PreflightToolCall"), "bridge: missing-argument refusal points at PreflightToolCall");

            // ---- UpdateLogic ----
            check(UpdateLogic.ParseVersion("v2.7.57")!.SequenceEqual(new[] { 2, 7, 57 }) && UpdateLogic.ParseVersion("2.7.57.0")!.SequenceEqual(new[] { 2, 7, 57 }) && UpdateLogic.ParseVersion("latest") == null && UpdateLogic.ParseVersion("") == null, "update: version tags parsed");
            check(UpdateLogic.Compare("2.7.56", "v2.7.57") < 0 && UpdateLogic.Compare("2.7.57", "v2.7.57") == 0 && UpdateLogic.Compare("2.8.0", "v2.7.99") > 0 && UpdateLogic.Compare("2.7.57", "beta") == null, "update: numeric comparison, not string comparison");
            check(UpdateLogic.IsValidRepository("asckye/TIA_Portal_Openness_MCP") && !UpdateLogic.IsValidRepository("asckye") && !UpdateLogic.IsValidRepository("a/b/c") && !UpdateLogic.IsValidRepository("../x"), "update: repository must be owner/name");
            check(UpdateLogic.LatestReleaseUrl("o/r") == "https://api.github.com/repos/o/r/releases/latest", "update: API url");
            var release = UpdateLogic.ParseRelease("{\"tag_name\":\"v2.7.57\",\"name\":\"v2.7.57\",\"html_url\":\"https://github.com/o/r/releases/tag/v2.7.57\",\"published_at\":\"2026-09-22T01:00:00Z\",\"prerelease\":false,\"draft\":false,\"assets\":[{\"name\":\"TIA_MCP_Delivery_v2.7.57_20260922.sha256\",\"browser_download_url\":\"https://x/s\",\"size\":99},{\"name\":\"TIA_MCP_Delivery_v2.7.57_20260922.zip\",\"browser_download_url\":\"https://x/z\",\"size\":16700000,\"digest\":\"sha256:ab\"}]}");
            check(release.Version == "2.7.57" && release.Zip!.Name.EndsWith(".zip") && release.Zip.Size == 16700000 && release.Zip.Digest == "sha256:ab" && release.Sha256!.Url == "https://x/s", "update: release JSON parsed, delivery assets picked");
            check(Fails<ArgumentException>(() => UpdateLogic.ParseRelease("{\"message\":\"API rate limit exceeded\"}")), "update: rate-limit answer has no tag -> refused");
            check(Fails<ArgumentException>(() => UpdateLogic.ParseRelease("[]")), "update: non-object refused");
            check(UpdateLogic.Summary("2.7.56", release, -1).StartsWith("Update available: engine 2.7.56 -> 2.7.57") && UpdateLogic.Summary("2.7.57", release, 0).Contains("is the latest") && UpdateLogic.Summary("2.7.58", release, 1).Contains("newer than"), "update: summary per comparison");
            check(UpdateLogic.TagFromReleaseUrl("https://github.com/o/r/releases/tag/v2.7.56") == "v2.7.56" && UpdateLogic.TagFromReleaseUrl("https://github.com/o/r/releases") == null && UpdateLogic.TagFromReleaseUrl(null) == null, "update: tag read from the /releases/latest redirect target");
            var page = UpdateLogic.ParseReleasePage("o/r", "v2.7.56", "<a href=\"/o/r/releases/download/v2.7.56/TIA_MCP_Delivery_v2.7.56_20260921.sha256\">x</a><span>105 Bytes</span><a href=\"/o/r/releases/download/v2.7.56/TIA_MCP_Delivery_v2.7.56_20260921.zip\">y</a><span class=\"x\">15.2 MB</span><a href=\"/o/r/releases/download/v2.7.56/TIA_MCP_Delivery_v2.7.56_20260921.zip\">dup</a>");
            check(page.Version == "2.7.56" && page.Assets.Count == 2 && page.Zip!.Size == 15938355 && page.Zip.Url == "https://github.com/o/r/releases/download/v2.7.56/TIA_MCP_Delivery_v2.7.56_20260921.zip" && page.Sha256!.Size == 105, "update: release page fragment parsed (links de-duplicated, sizes from the text)");
            check(UpdateLogic.ParseReleasePage("o/r", "v1.0.0", "").Assets.Count == 0 && UpdateLogic.ParseReleasePage("o/r", "v1.0.0", "").Zip == null, "update: empty fragment -> no assets");
            var steps = UpdateLogic.HowToUpdate(null, false);
            check(steps.Count == 4 && steps[0].Contains("Stop every TiaMcpServer.exe") && steps[1].Contains(UpdateLogic.UpdaterRelativePath) && steps[2].Contains("-ZipPath") && steps[3].Contains("-Rollback"), "update: steps name stop / script / offline / rollback");
        }
    }
}
