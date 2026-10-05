using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.XPath;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainValidation
    {
        public static void ValidateParameter<T>(T value)
        {
            string json = V4Json.Serialize(value);
            var input = V4Json.ParseInput(json);
            Budget(typeof(T)).Check(input);
            DomainSchemas.For(typeof(T)).Check(input);
            switch (value)
            {
                case Artifact[] artifacts: ArtifactOrder(artifacts); break;
                case LibrarySelection[] selections: LibraryDeepLogic.ParseSelection(json); break;
                case TestScope[] scopes: TestSuiteLogic.ParseScopeEntries(json); break;
                case TemplateRow[] rows:
                    Unique(rows.Select(r => r.FileName), StringComparer.OrdinalIgnoreCase); break;
                case PlcAliasRow[] aliases:
                    Unique(aliases.Select(r => V4Json.Serialize(r.Destination)), StringComparer.Ordinal); break;
                case XPathRule[] rules: Unique(rules.Select(r => r.Id), StringComparer.Ordinal); break;
                case GraphicSelectionPage[] pages: ValidatePages(pages); break;
                case Dictionary<string, bool> devices:
                    foreach (var key in devices.Keys) Text(key); break;
            }
        }
        internal static void ValidateValue(Type type, JsonElement json)
        {
            type = DomainSchemas.Concrete(type, json);
            string raw = json.GetRawText();
            if (type == typeof(OpenPipeRequest))
            {
                // Reuse the expert parser's exact blank-message and subscription rules
                // without generating a cookie or touching the Runtime channel.
                RuntimeChannelsLogic.PrepareRawRequest(new JsonObject
                { ["Message"] = json.GetProperty("message").GetString(), ["ClientCookie"] = "validation" }.ToJsonString());
            }
            else if (type == typeof(Artifact))
            {
                Text(json.GetProperty("id").GetString()!);
                if (json.TryGetProperty("dependencies", out var dependencies)) foreach (var entry in dependencies.EnumerateArray()) Text(entry.GetString()!);
            }
            else if (type == typeof(NetworkPlan))
            {
                var result = HardwareNetworkPlanValidator.Validate(raw);
                Require(result["ok"]!.GetValue<bool>());
            }
            else if (typeof(NetworkOperation).IsAssignableFrom(type))
            {
                var result = HardwareNetworkPlanValidator.Validate("{\"operations\":[" + raw + "]}");
                Require(result["ok"]!.GetValue<bool>());
            }
            else if (type == typeof(TemplateRow))
            {
                string name = json.GetProperty("fileName").GetString()!;
                Require(name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                    && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            }
            else if (type == typeof(PlcAliasRow))
            {
                foreach (string field in new[] { "source", "destination", "acknowledge" })
                    if (json.TryGetProperty(field, out var parts)) foreach (var part in parts.EnumerateArray())
                    { string text = part.GetString()!; Text(text); Require(!text.Contains("[") && !text.Contains("]")); }
            }
            else if (type == typeof(PlcSimScenario)) PlcSimAdvancedLogic.ParseScenario(raw);
            else if (type == typeof(PlcSimWriteStep) || type == typeof(PlcSimAssertStep))
            {
                var map = json.GetProperty(type == typeof(PlcSimWriteStep) ? "write" : "assert");
                foreach (var entry in map.EnumerateObject()) Text(entry.Name);
            }
            else if (typeof(DccPartnerSpec).IsAssignableFrom(type))
                DccLogic.ValidatePinRequest("Chart", "Block", "Pin", "connect", "{}", raw, false, -1, -1, true);
            else if (type == typeof(MotionTarget))
            {
                Budget(typeof(MotionTarget)).Check(json);
                foreach (var field in json.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.String) Text(field.Value.GetString()!);
                    if (field.Value.ValueKind == JsonValueKind.Array) foreach (var part in field.Value.EnumerateArray()) Text(part.GetString()!);
                }
            }
            else if (type == typeof(TestScope)) TestSuiteLogic.ParseScopeEntries("[" + raw + "]");
            else if (type == typeof(TeamcenterItemSpec)) TeamcenterLogic.ParseItemDetails(raw);
            else if (type == typeof(TeamcenterRevisionSpec)) TeamcenterLogic.ParseRevisionDetails(raw);
            else if (type == typeof(SivarcReference)) SivarcLogic.ParseReference(JsonNode.Parse(raw)!, "reference");
            else if (type == typeof(LibrarySelection)) LibraryDeepLogic.ParseSelection("[" + raw + "]");
            else if (type == typeof(XPathRule))
            {
                XPathExpression.Compile(json.GetProperty("xpath").GetString()!);
                foreach (var key in new[] { "files", "valuePattern" })
                    if (json.TryGetProperty(key, out var pattern)) _ = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                int min = json.TryGetProperty("minCount", out var a) ? a.GetInt32() : 0;
                int max = json.TryGetProperty("maxCount", out var b) ? b.GetInt32() : int.MaxValue;
                Require(min <= max);
            }
        }

        public static IReadOnlyList<string> ArtifactOrder(IReadOnlyList<Artifact> artifacts)
        {
            Require(artifacts != null && artifacts.Count >= 1);
            InputGuard.Limit(artifacts!.Count, 256);
            Budget(typeof(Artifact[])).Check(V4Json.ParseInput(V4Json.Serialize(artifacts)));
            var byId = new Dictionary<string, Artifact>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in artifacts!)
            { Require(item != null && !byId.ContainsKey(item.Id)); byId.Add(item!.Id, item); }
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            void Visit(Artifact item)
            {
                if (done.Contains(item.Id)) return;
                Require(active.Add(item.Id));
                var dependencies = (item.Dependencies ?? System.Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string id in dependencies) Require(byId.ContainsKey(id));
                foreach (string id in dependencies.OrderBy(id => byId[id].Priority ?? 0).ThenBy(id => id, StringComparer.OrdinalIgnoreCase)) Visit(byId[id]);
                active.Remove(item.Id); done.Add(item.Id); ordered.Add(item.Id);
            }
            foreach (var item in artifacts.OrderBy(a => a.Priority ?? 0).ThenBy(a => a.Target, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)) Visit(item);
            return ordered.AsReadOnly();
        }

        // Document-dependent selectors, expected values and read-only/library checks use
        // the existing offline patch validator. This returns a candidate; it performs no write.
        public static string BlockEdits(IReadOnlyList<BlockEdit> edits, string xml, string expectedFingerprint) =>
            PlcDocumentEditing.Patch(xml, V4Json.Serialize(Read<BlockEdit[]>(V4Json.Serialize(edits))), expectedFingerprint);
        public static IReadOnlyList<PlcTemplateExpansion.Document> TemplateRows(IReadOnlyList<TemplateRow> rows, string templatePath) =>
            PlcTemplateExpansion.Expand(templatePath, V4Json.Serialize(Read<TemplateRow[]>(V4Json.Serialize(rows))));

        internal static string MotionMode(JsonElement json) => json.TryGetProperty("devicePath", out _)
            ? json.TryGetProperty("secondItemPath", out _) ? "deviceItems" : json.TryGetProperty("channelIndex", out _) ? "deviceItemChannel"
                : json.TryGetProperty("channelType", out _) ? "channel" : "deviceItem"
            : json.TryGetProperty("dbMemberPath", out _) ? "dbMember" : json.TryGetProperty("plcTagPath", out _) ? "plcTag"
                : json.TryGetProperty("inputBitAddress", out _) ? "addresses" : "address";

        public static void Motion(MotionTarget target, string aspect, string action, string releaseKey, bool packageAvailable, bool readOnly)
        {
            Capability(releaseKey, packageAvailable, "Motion", readOnly, action != "read");
            if (action == "connectIdent")
            {
                Require(aspect == "" && target.Mode == "deviceItem");
                return;
            }
            Require(action == "connect");
            string[] modes = aspect switch
            {
                "actor" or "sensor" => new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "channel" },
                "torque" => new[] { "deviceItem", "deviceItems", "dbMember" },
                "encoder" => new[] { "deviceItem", "deviceItems", "dbMember", "plcTag", "addresses", "channel" },
                "measuringInput" => new[] { "address", "deviceItemChannel", "channel" },
                "outputCam" => new[] { "address", "plcTag", "channel" },
                _ => throw new ArgumentException("Unknown connection aspect.")
            };
            Require(modes.Contains(target.Mode));
        }
        public static void DccPartner(DccPartnerSpec partner, string action, string releaseKey, bool packageAvailable, bool readOnly)
        {
            Capability(releaseKey, packageAvailable, "DCC", readOnly, true);
            Require(action == "connect" || action == "disconnect");
            ValidateValue(typeof(DccPartnerSpec), partner.Json);
        }
        public static void TestScopes(TestScope[] scope, string category, string action, string kind, string releaseKey, bool packageAvailable, bool readOnly)
        {
            Capability(releaseKey, packageAvailable, "TestSuite", readOnly, action != "read");
            ValidateParameter(scope);
            Require(new[] { "styleGuide", "application", "system" }.Contains(category) && TestSuiteLogic.ManageActions.Contains(action));
            TestSuiteLogic.RequireKind(category, kind);
            if (action == "setScope" && category == "styleGuide") Require(kind == "case" && scope.Length > 0);
            else Require(scope.Length == 0);
        }
        public static void Teamcenter(TeamcenterItemSpec? item, TeamcenterRevisionSpec? revision, string action, string releaseKey, bool packageAvailable, bool readOnly)
        {
            Capability(releaseKey, packageAvailable, "Teamcenter", readOnly, action != "readCustomAttributes");
            Require(TeamcenterLogic.WorkflowActions.Contains(action));
            bool newItem = action == "saveAsNewItem" || action == "saveAsNewItemWithProxyObject";
            bool newRevision = action == "saveAsNewRevision" || action == "saveAsNewRevisionWithProxyObject";
            Require(newItem == (item != null) && (!newRevision ? revision == null : true));
        }
        public static void Sivarc(Dictionary<string, SivarcReference?> references, Dictionary<string, bool> deviceSelection,
            string category, string kind, string action, string releaseKey, bool packageAvailable, bool readOnly)
        {
            Capability(releaseKey, packageAvailable, "SiVArc", readOnly, action != "read");
            ValidateParameter(references); ValidateParameter(deviceSelection);
            Require(SivarcLogic.Categories.Contains(category) && SivarcLogic.RuleKinds.Contains(kind) && SivarcLogic.RuleActions.Contains(action));
            if (action != "create" && action != "update") Require(references.Count == 0 && deviceSelection.Count == 0);
            foreach (var key in references.Keys) Require(SivarcLogic.ReferenceNames(category, kind).Contains(key, StringComparer.Ordinal));
            Require(deviceSelection.Count == 0 || category != "tags" && category != "textLists");
        }
        public static void Library(LibrarySelection[] selection, string[] harmonizeOptions, string action,
            string releaseKey, bool packageAvailable, bool readOnly, string libraryName, string targetLibraryName, string[] softwarePaths)
        {
            Capability(releaseKey, packageAvailable, "Library", readOnly, true);
            ValidateParameter(selection);
            if (action == "harmonizeProject") LibraryDeepLogic.JoinHarmonizeOptions(harmonizeOptions);
            LibraryDeepLogic.ValidateSyncRequest(action, libraryName, targetLibraryName, softwarePaths, LibraryDeepLogic.ParseSelection(V4Json.Serialize(selection)),
                "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "UpdateStructure", "PreserveDefaultVersionOfUnusedTypes");
        }
        public static void Monitoring(MonitoringOptions options, string provider, bool readOnly)
        {
            Require(readOnly);
            Require(new[] { "opcua", "s7-readonly" }.Contains(provider.Trim().ToLowerInvariant()));
        }
        public static void Template(TemplateIntent intent, bool readOnly) => Require(readOnly);
        internal static void Capability(string releaseKey, bool available, string name, bool readOnly, bool writes)
        {
            if ((releaseKey != "20" && releaseKey != "21") || !available) throw new NotSupportedException(name + " capability unavailable for release " + releaseKey);
            if (readOnly && writes) throw new NotSupportedException(name + " action is not read-only.");
        }
        private static void Unique(IEnumerable<string> values, StringComparer comparer)
        { var seen = new HashSet<string>(comparer); foreach (string value in values) Require(seen.Add(value)); }
        internal static void Text(string text) => Require(!string.IsNullOrWhiteSpace(text));
        internal static void Require(bool condition)
        {
            try { InputGuard.Require(condition); }
            catch (InputRejection rejection) { throw new ArgumentException(V4Json.Serialize(rejection.ToError("input"))); }
        }
    }
}
