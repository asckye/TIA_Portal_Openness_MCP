using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaMcp.Logic.Generation.Ordering;

namespace TiaMcp.Logic.Generation
{
    public static partial class GenerationPlanner
    {
        private sealed partial class Expansion
        {

            private (LibraryPartTypesItem Definition, Entry Entry) Type(string station, string reference, string group)
            {
                var match = Regex.Match(reference, @"\Alib:([^/]+)/([^@]+)@(.+)\z", RegexOptions.CultureInvariant);
                if (!match.Success || match.Groups[1].Value != manifest.Id) throw CanonicalJson
                    .Failure("/emit/type", "reference", "Type reference must resolve in the effective local package.");
                var id = match.Groups[2].Value;
                var type = library.Types.SingleOrDefault(t => t.Id == id) ?? throw CanonicalJson.Failure("/library/" + id, "reference", "Unknown type.");
                if (!VersionMatches(match.Groups[3].Value, type.Version)) throw CanonicalJson
                    .Failure("/library/" + id, "version", "Type version does not satisfy the reference.");
                var key = station + "/" + id;
                if (types.TryGetValue(key, out var found))
                {
                    if (found.Entry.Object is ProjectBlock block && block.Group != group || found.Entry.Object is ProjectType udt && udt.Group != group)
                        throw CanonicalJson.Failure("/library/" + id, "collision", "One type is requested in different groups.");
                    return found;
                }
                var implementation = type.Implementations.FirstOrDefault(i => i.Releases.ValueKind == JsonValueKind.Array
                    ? i.Releases.EnumerateArray().Any(r => r.GetString() == machine.Target.Release) : GenerationModelValidation
                        .ReleaseMatches(i.Releases.GetString()!, machine.Target.Release))
                    ?? throw CanonicalJson.Failure("/library/" + id, "release-coverage", "No implementation for this release.");
                Entry primary;
                if (implementation.Kind == "sclSource")
                {
                    var declarations = implementation.Files!.SelectMany(file => GenerationScl.Declarations(package.ReadFile(file))).ToArray();
                    var candidates = declarations.Where(d => d.Kind == type.Kind).ToArray();
                    if (candidates.Length != 1) throw CanonicalJson
                        .Failure("/library/" + id, "ambiguous", "Implementation must have exactly one primary declaration of kind " + type.Kind + ".");
                    foreach (var declaration in declarations.OrderBy(d => d.Name, StringComparer.Ordinal))
                    {
                        ProjectObject model = declaration.Kind == "UDT" ? new ProjectType {
                            Station = station, Name = declaration.Name, Group = group, InterfaceFingerprint = HashText(declaration.Content), Members =
                                GenerationScl.Members(declaration.Content)
                        }
                            : new ProjectBlock { Station = station, Name = declaration.Name, Group = group, Kind = declaration.Kind,
                                InterfaceFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(type.Interface)), CodeFingerprint =
                                    HashText(declaration.Content), Members = GenerationScl.Members(declaration.Content) };
                        if (entries.TryGetValue(Key(model), out var existing))
                        {
                            if (ProjectModelComparison.Fingerprint(existing.Object) != ProjectModelComparison.Fingerprint(model)) throw CanonicalJson
                                .Failure("/library", "duplicate-key", "Different sources declare the same object.");
                        }
                        else
                        {
                            var entry = Source(model, declaration.Kind == "UDT" ? "plcTypes" : "plcLibrary", declaration.Kind, declaration.Content);
                            sourceReferences.Add(entry.Key, declaration.References);
                        }
                    }
                    primary = entries[ProjectModelComparison.Key(station, type.Kind == "UDT" ? "type" : "block", candidates[0].Name)];
                    var declaredMembers = primary.Object is ProjectBlock declaredBlock ? declaredBlock.Members : ((ProjectType)primary.Object).Members;
                    var contract = (type.Interface.In ?? new List<InterfaceMember>()).Concat(type.Interface.Out ?? new List<InterfaceMember>())
                        .Concat(type.Interface.InOut ?? new List<InterfaceMember>()).Concat(type.Interface.Static ?? new List<InterfaceMember>()).ToArray();
                    if (contract.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != contract.Length || contract.Select(m => m.Role)
                            .Distinct(StringComparer.Ordinal).Count() != contract.Length)
                        throw CanonicalJson.Failure("/library/interface", "duplicate-key", "Interface names and binding roles must be unique.");
                    foreach (var member in contract)
                        if (!declaredMembers.TryGetValue(member.Name, out var declaredType) || !string
                                .Equals(declaredType.Trim('"'), member.Type.Trim('"'), StringComparison.OrdinalIgnoreCase))
                            throw CanonicalJson
                                .Failure("/library/interface/" + member.Name, "reference", "Logical interface does not match the primary SCL declaration.");
                }
                else
                {
                    var path = implementation.TypePath ?? implementation.MasterCopyPath!;
                    var name = path.Split('/').Last();
                    ProjectObject model = type.Kind == "UDT" ? new ProjectType {
                        Station = station, Name = name, Group = group, InterfaceFingerprint = CanonicalJson
                            .Hash(JsonSerializer.SerializeToElement(type.Interface))
                    }
                        : new ProjectBlock {
                            Station = station, Name = name, Kind = type.Kind, Group = group, LibraryTypeFingerprint = CanonicalJson
                                .Hash(JsonSerializer.SerializeToElement(implementation))
                        };
                    primary =
                        Add(model, "plcLibrary", type.Kind, implementation.Kind == "globalLibrary" ? "PlaceLibraryType" : "CreateFromMasterCopy", Args(), type
                            .Kind == "UDT" ? "ListPlcTypes" : "ListPlcBlocks");
                    primary.Unavailable =
                        "Library placement requires P8-31f and an observed external library/type version; no tool or exact version is inferred.";
                }
                found = (type, primary); types.Add(key, found); return found;
            }

            private Entry Source(ProjectObject model, string phase, string namingKind, string content)
            {
                var group = model is ProjectBlock block ? block.Group : ((ProjectType)model).Group;
                var key = Key(model);
                var sourceName = "source_" + HashText(key).Substring(7, 16) + "_" + HashText(content).Substring(7, 16) + ".scl";
                var path = "staging/" + plan.PlanId + "/" + sourceName;
                var artifact = new GenerationArtifact(path, content);
                if (!artifacts.Any(a => a.Path == path)) artifacts.Add(artifact);
                var source = new ProjectExternalSource {
                    Station = model.Station, Name = sourceName, Group = "", ContentFingerprint = "sha256:" + artifact.Sha256 };
                var sourceEntry =
                    Add(source, phase, "externalSource", "ImportPlcExternalSource", Args(("softwarePath", Software(model.Station)), ("groupPath", ""),
                    ("filePath", options.ArtifactRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/" + path), ("dryRun", false), ("confirm", true)),
                        "ListPlcExternalSources");
                var entry =
                    Add(model, phase, namingKind, "GenerateBlocksFromExternalSource",
                        Args(("softwarePath", Software(model.Station)), ("externalSourceName", sourceName), ("dryRun", false), ("confirm", true)),
                        model is ProjectType ? "ListPlcTypes" : "ListPlcBlocks");
                entry.Dependencies.Add(sourceEntry.Key);
                if (group.Length != 0)
                {
                    var groupEntry = Group(model.Station, model is ProjectType ? "types" : "blocks", group);
                    entry.Dependencies.Add(groupEntry.Key);
                    if (model is ProjectBlock)
                        entry.FollowUps
                            .Add(("MovePlcBlockToGroup",
                                    Args(("softwarePath", Software(model.Station)), ("blockName", model.Name), ("groupPath", group),
                                        ("autoCreateGroup", false))));
                    else entry.Unavailable = "External-source generation creates types at the root; no current tool can move them to the declared type group.";
                }
                return entry;
            }

            private static string ObjectPath(string group, string name) => group.Length == 0 ? name : group + "/" + name;

            private void Reviews()
            {
                foreach (var step in plan.Steps)
                {
                    if (step.Tool == "ImportPlcExternalSource")
                    {
                        string file = step.Arguments["filePath"].GetString()!;
                        var artifact = artifacts.Single(a => file.EndsWith("/" + a.Path, StringComparison.Ordinal));
                        step.Review = new GenerationStepReview { Rule = StepReviewRules.Import,
                            Expect = Args(("softwarePath", step.Arguments["softwarePath"]), ("groupPath", ""), ("filePath", file),
                                ("inputSha256", artifact.Sha256), ("byteCount", Encoding.UTF8.GetByteCount(artifact.Content)),
                                ("sourceName", step.Expect.Contains!), ("sourcesBefore", "observed+run")),
                            Witness = new Dictionary<string, string> { ["planHash"] = "sha256" },
                            Forbid = new Dictionary<string, List<string>> { ["sourcesBefore"] = new List<string> { step.Expect.Contains! } },
                            Effect = Args(("sourcesAdded", new[] { step.Expect.Contains! })) };
                    }
                    else if (step.Tool == "GenerateBlocksFromExternalSource")
                    {
                        string software = step.Arguments["softwarePath"].GetString()!;
                        string source = step.Arguments["externalSourceName"].GetString()!;
                        var import = plan.Steps
                            .SingleOrDefault(s => s.Tool == "ImportPlcExternalSource" && s.Expect.Contains == source && s.Arguments["softwarePath"]
                                .GetString() == software);
                        // An existing source cannot establish this run's custody chain.
                        if (import == null) {
                            plan.Unavailable
                                .Add(new GenerationPlanUnavailableItem {
                                    Phase = step.Phase, Reason = "Source " + source
                                        + " must be imported in this run; replan after inspecting the existing source."
                                });
                            continue;
                        }
                        var generated = entries[step.Key].Object;
                        string objectKey = (generated is ProjectType ? "type:" : "block:") + generated.Name;
                        step.Review = new GenerationStepReview { Rule = StepReviewRules.Generate,
                            Expect = Args(("softwarePath", software), ("groupPath", ""), ("sourceName", new { fact = import.Id + ".sourceName" }),
                                ("generationOption", machine.Target.Release == "14sp1" ? "parameterless" : "None"),
                                ("resultBasis", machine.Target.Release == "14sp1" ? "inventory-observation-only; native API returns void"
                                        : "native-returned-objects-with-path-readback"),
                                    ("objectKeysBefore", "observed+run")),
                            Witness = new Dictionary<string, string> {
                                ["planHash"] = "sha256", ["sourceIdentity"] = "capture-as-fact", ["objectsBefore.modifiedDate"] = "ignore",
                                ["objectsBefore.isConsistent"] = "ignore", ["objectsBefore.typeName"] = "ignore", ["objectsBefore.programmingLanguage"] =
                                    "ignore" },
                            Forbid = new Dictionary<string, List<string>> { ["objectKeysBefore"] = new List<string> { objectKey } },
                            Effect = Args(("generated", new[] { objectKey }), ("others", "forbidden")) };
                    }
                    else if (step.Tool == "DeletePlcExternalSource" && step.Key.EndsWith("/replace", StringComparison.Ordinal))
                    {
                        string key = step.Key.Substring(0, step.Key.Length - "/replace".Length), source = step.Arguments["externalSourceName"].GetString()!;
                        var owned = plan.Objects.Single(o => o.Key == key);
                        step.Review = new GenerationStepReview { Rule = StepReviewRules.Delete,
                            Expect =
                                Args(("softwarePath", step.Arguments["softwarePath"]), ("groupPath", ""), ("sourceName", source),
                                    ("sourcesBefore", "observed+run"), ("ownedKey", key)),
                            Witness = new Dictionary<string, string> {
                                ["planHash"] = "sha256", ["targetIdentity"] = "capture-owned-source", ["ownership"] = CanonicalJson
                                    .Hash(JsonSerializer.SerializeToElement(owned))
                            },
                            Effect = Args(("sourcesRemoved", new[] { source })) };
                    }
                    else if (step.Tool == "DeletePlcExternalSource" && step.Phase == "cleanup")
                    {
                        string software = step.Arguments["softwarePath"].GetString()!, source = step.Arguments["externalSourceName"].GetString()!;
                        var import = plan.Steps
                            .Single(s => s.Tool == "ImportPlcExternalSource" && s.Expect.Contains == source && s.Arguments["softwarePath"]
                                .GetString() == software);
                        var generate = plan.Steps
                            .Single(s => s.Tool == "GenerateBlocksFromExternalSource" && s.Arguments["externalSourceName"].GetString() == source && s
                                .Arguments["softwarePath"].GetString() == software);
                        step.Review = new GenerationStepReview { Rule = StepReviewRules.Delete,
                            Expect =
                                Args(("softwarePath", software), ("groupPath", ""), ("sourceName", new { fact = import.Id + ".sourceName" }),
                                    ("sourcesBefore", "observed+run")),
                            Witness = new Dictionary<string, string> { ["planHash"] = "sha256", ["targetIdentity"] = "fact:" + generate.Id + ".sourceIdentity"
                            },
                            Effect = Args(("sourcesRemoved", new[] { source })) };
                    }
                }
                var unavailable = plan.Unavailable.Select(u => u.Phase).ToHashSet(StringComparer.Ordinal);
                bool changed;
                do {
                    changed = false;
                    foreach (var step in plan.Steps.Where(s => !unavailable.Contains(s.Phase)))
                        if (step.DependsOn.Any(id => plan.Steps.Any(s => s.Id == id && unavailable.Contains(s.Phase))))
                        {
                            unavailable.Add(step.Phase); plan.Unavailable
                                .Add(new GenerationPlanUnavailableItem { Phase = step.Phase, Reason = "A source custody prerequisite is unavailable." });
                            changed = true;
                        }
                } while (changed);
                plan.Steps.RemoveAll(s => unavailable.Contains(s.Phase));
            }

            private void CallBlocks()
            {
                foreach (var pair in calls.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var station = pair.Key.Substring(0, pair.Key.IndexOf('/'));
                    var name = pair.Value[0].Target;
                    var content = GenerationScl.Calls(name, pair.Value);
                    var block = new ProjectBlock {
                        Station = station, Name = name, Kind = "FC", Group = callGroups[pair.Key], CodeFingerprint = HashText(content), InterfaceFingerprint =
                            HashText("Void")
                    };
                    var entry = Source(block, "plcProgram", "FC", content);
                    foreach (var call in pair.Value)
                    {
                        entry.Dependencies.Add(ProjectModelComparison.Key(station, "block", call.Instance));
                        foreach (var bindingPair in call.Bindings)
                        {
                            var binding = bindingPair.Value;
                            var parameter = (call.Type.Interface.In ?? new List<InterfaceMember>())
                                .Concat(call.Type.Interface.Out ?? new List<InterfaceMember>()).Concat(call.Type.Interface.InOut ?? new List<InterfaceMember>())
                                .Single(m => m.Role == bindingPair.Key);
                            var root = GenerationScl.BindingRoot(binding);
                            if (root.Length == 0) { GenerationScl.CheckLiteralType(binding, parameter.Type); continue; }
                            var candidates = entries.Values
                                .Where(e => e.Object.Station == station && e.Object.Name == root && (e.Object is ProjectTag || e.Object is ProjectBlock))
                                .ToArray();
                            if (candidates.Length != 1) throw CanonicalJson
                                .Failure("/bind/" + root, "reference", "Binding does not resolve to one generated tag/block.");
                            entry.Dependencies.Add(candidates[0].Key);
                            var bindingPath = GenerationScl.BindingPath(binding);
                            var referenced = candidates[0].Object;
                            foreach (var member in bindingPath.Skip(1))
                            {
                                var members = referenced is ProjectBlock referencedBlock ? referencedBlock.Members : referenced is ProjectType referencedType
                                    ? referencedType.Members : new Dictionary<string, string>();
                                if (!members.TryGetValue(member, out var memberType)) throw CanonicalJson
                                    .Failure("/bind/" + binding, "reference", "Symbolic member does not exist in the generated interface.");
                                referenced = expected.Types.FirstOrDefault(t => t.Station == station && t.Name == memberType) ?? (ProjectObject)new ProjectTag {
                                    DataType = memberType };
                            }
                            var boundType = referenced is ProjectTag boundTag ? boundTag.DataType : referenced is ProjectType boundUdt ? boundUdt.Name
                                : ((ProjectBlock)referenced).InstanceType;
                            if (boundType == null || !string.Equals(boundType.Trim('"'), parameter.Type.Trim('"'), StringComparison.OrdinalIgnoreCase))
                                throw CanonicalJson.Failure("/bind/" + binding, "type", "Binding data type differs from its interface parameter.");
                        }
                    }
                }
            }

            private void Alarm(MachineDescriptionDevicesItem device, DeviceTypeRuleEmitItem emit, Func<string?, string, string> text)
            {
                var classId = text(emit.Class, "class"); var textKey = text(emit.TextKey, "textKey");
                var alarmClass = alarms.Classes.SingleOrDefault(c => c.Id == classId) ?? throw CanonicalJson
                    .Failure("/alarms/class", "reference", "Unknown alarm class.");
                if (!alarms.Texts.TryGetValue(textKey, out var texts) || manifest.Languages.Required.Any(l => !texts.ContainsKey(l))) throw CanonicalJson
                    .Failure("/alarms/text", "language", "Alarm text lacks a required language.");
                var template = alarms.Templates.SingleOrDefault(t => t.DeviceType == device.Type && t.TextKey == textKey && t.Class == classId);
                if (template == null) throw CanonicalJson.Failure("/alarms/template", "reference", "Alarm emit does not resolve to one template.");
                if (alarms.Numbering == null) throw CanonicalJson.Failure("/alarms/numbering", "required", "Alarms require a number range.");
                if (!int
                        .TryParse(text(alarms.Numbering.Template ?? "{{seq('alarms')}}", "number"), NumberStyles.None, CultureInfo.InvariantCulture,
                            out var number)
                        || number < alarms.Numbering.Start || number > alarms.Numbering.End)
                    throw CanonicalJson.Failure("/alarms/numbering", "range", "Alarm numbering template must yield an integer inside the declared range.");
                var name = emit.Name == null ? device.Id + "_" + textKey.Replace('.', '_') : text(emit.Name, "name");
                var model = new ProjectAlarm {
                    Station = device.Station, Name = name, Number = number, Class = classId, Priority = alarmClass.Priority, Acknowledgement = alarmClass
                        .Acknowledgement,
                    Texts = texts.ToDictionary(p => p.Key, p => text(p.Value, "text"), StringComparer.Ordinal), Backend = template.Backend };
                var entry =
                    Add(model, "alarms", "alarm", template.Backend == "hmiDiscrete" ? "ManageUnifiedEngineeringObject" : "ImportPlcAlarmInstanceTexts", Args(),
                        "ExportPlcAlarmInstanceTexts");
                entry.Unavailable = "Alarm template " + template.Id
                    + " needs a concrete Program_Alarm binding or Unified tag/class mapping and import resource; metadata alone is not an executable alarm.";
            }

            private void Screen(MachineDescriptionDevicesItem device, DeviceTypeRuleEmitItem emit, Func<string?, string, string> text)
            {
                var name = text(emit.Screen ?? emit.Name, "screen");
                var station = machine.Stations.SingleOrDefault(s => s.Role == "hmi.panel")?.Id ?? device.Station;
                var template = text(emit.Template, "template");
                var content = package.Documents.TryGetValue(template, out var document) ? document : CanonicalJson.Parse(package.ReadFile(template));
                content = ExpandDocument(content, value => text(value, "template"));
                var model = new ProjectScreen {
                    Station = station, Name = name, TemplateFingerprint = CanonicalJson.Hash(content), Design = content.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
                };
                if (emit.Kind == "hmi.widget")
                {
                    var key = Key(model);
                    Entry widgetScreen;
                    if (entries.TryGetValue(key, out var existingScreen)) widgetScreen = existingScreen;
                    else widgetScreen = Add(model, "hmi", "screen", "ApplyUnifiedHmiScreenDesign", Args(), "ListUnifiedHmiScreens");
                    var screen = (ProjectScreen)widgetScreen.Object;
                    var widgetName = emit.Name == null ? device.Id : text(emit.Name, "name");
                    naming.Check("widget", widgetName);
                    if (screen.Widgets.Any(w => string.Equals(w.Name, widgetName, StringComparison.OrdinalIgnoreCase))) throw CanonicalJson
                        .Failure("/hmi/widgets", "duplicate-key", "Duplicate widget name on a screen.");
                    var slot = emit.Slot?.ValueKind == JsonValueKind.Number ? emit.Slot.Value.GetInt32() : 0;
                    if (emit.Slot?.ValueKind != JsonValueKind.Number) while (screen.Widgets.Any(w => w.Slot == slot)) slot++;
                    if (screen.Widgets.Any(w => w.Slot == slot)) throw CanonicalJson.Failure("/hmi/widgets", "collision", "Widget slot is occupied.");
                    screen.Widgets
                        .Add(new ProjectScreenWidget { Name = widgetName, Device = device.Id, Slot = slot, TemplateFingerprint = CanonicalJson.Hash(content) });
                    widgetScreen.Unavailable =
                        "Widget layout/interface binding needs a complete screen/items design before Apply; every emitted widget is retained in the expected " +
                        "model.";
                    return;
                }
                if (entries.TryGetValue(Key(model), out var existing))
                {
                    if (ProjectModelComparison.Fingerprint(existing.Object) != ProjectModelComparison.Fingerprint(model)) throw CanonicalJson
                        .Failure("/hmi", "duplicate-key", "Screen definitions differ.");
                    return;
                }
                var entry =
                    Add(model, "hmi", "screen", "EnsureUnifiedHmiScreen", Args(("hmiSoftwarePath", Software(station)), ("screenName", name)),
                        "ListUnifiedHmiScreens");
                if (emit.Kind == "hmi.screen" && content.TryGetProperty("screen", out _) && content.TryGetProperty("items", out var items) && items
                        .ValueKind == JsonValueKind.Array)
                {
                    if (!content.GetProperty("screen").TryGetProperty("name", out var screenName) || screenName.GetString() != name)
                        throw CanonicalJson.Failure("/hmi/screen/name", "reference", "Screen design name must equal the generated screen name.");
                    var itemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var itemName) || itemName.ValueKind != JsonValueKind.String) throw CanonicalJson
                            .Failure("/hmi/items/name", "required", "Every generated screen item needs an explicit name.");
                        naming.Check("screenItem", itemName.GetString()!);
                        if (!itemNames.Add(itemName.GetString()!)) throw CanonicalJson
                            .Failure("/hmi/items/name", "duplicate-key", "Duplicate screen item name.");
                    }
                    entry.FollowUps
                        .Add(("ApplyUnifiedHmiScreenDesign", Args(("hmiSoftwarePath", Software(station)), ("screenName", name), ("design", content))));
                }
                else entry.Unavailable = "Widget/screen template must provide a complete screen/items design and resolved interface bindings before Apply.";
            }

            private static JsonElement ExpandDocument(JsonElement input, Func<string, string> expand)
            {
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream)) Write(input, writer);
                return CanonicalJson.Parse(stream.ToArray());
                void Write(JsonElement value, Utf8JsonWriter writer)
                {
                    if (value.ValueKind == JsonValueKind.String) writer.WriteStringValue(expand(value.GetString()!));
                    else if (value.ValueKind == JsonValueKind.Array) {
                        writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
                    else if (value.ValueKind == JsonValueKind.Object) {
                        writer.WriteStartObject(); foreach (var property in value.EnumerateObject()) {
                            writer.WritePropertyName(property.Name); Write(property.Value, writer); } writer.WriteEndObject();
                    }
                    else value.WriteTo(writer);
                }
            }

            private void ResolveReferences()
            {
                ProjectModelComparison.Index(expected);
                foreach (var reference in sourceReferences)
                {
                    var entry = entries[reference.Key];
                    foreach (var name in reference.Value.Where(n => n != entry.Object.Name))
                    {
                        var candidates = entries.Values
                            .Where(e => e.Object.Station == entry.Object.Station && e.Object.Name == name
                                && (e.Object is ProjectType || e.Object is ProjectBlock || e.Object is ProjectTag))
                            .ToArray();
                        if (candidates.Length == 1) entry.Dependencies.Add(candidates[0].Key);
                        else throw CanonicalJson.Failure("/sources/" + name, "reference", "Quoted SCL reference does not resolve to one expected object.");
                    }
                }
                foreach (var entry in entries.Values)
                    foreach (var dependency in entry.Dependencies)
                        if (!entries.ContainsKey(dependency)) throw CanonicalJson
                            .Failure("/dependsOn/" + dependency, "reference", "Missing expected prerequisite.");
                if (entries.Count > 0)
                {
                    var graph = ImportDependencyPlanner.BuildGeneration(entries.Values.Select(e => new ImportOrderItem
                    {
                        Id = e.Key, Target = e.Phase, Priority = Phases.ToList().IndexOf(e.Phase), Dependencies = e.Dependencies
                            .OrderBy(d => d, StringComparer.Ordinal).ToArray()
                    }).ToArray());
                    if (!graph.Valid)
                        throw new GenerationValidationException(graph.Issues
                            .Select(i => new GenerationValidationError("/expected/" + i.Id, "dependency", i.Message)));
                }
                var devices = expected.Devices.ToArray();
                if (devices.Where(d => d.Slot.HasValue).GroupBy(d => d.Station + "/" + d.ParentPath + "/" + d.Slot).Any(g => g.Count() > 1))
                    throw CanonicalJson.Failure("/hardware", "collision", "Duplicate hardware slots.");
                var blocks = expected.Blocks.Where(b => b.Number.HasValue).ToArray();
                if (blocks.GroupBy(b => b.Station + "/" + b.Kind + "/" + b.Number).Any(g => g.Count() > 1)) throw CanonicalJson
                    .Failure("/blocks", "collision", "Duplicate block numbers.");
                if (expected.Alarms.GroupBy(a => a.Station + "/" + a.Number).Any(g => g.Count() > 1)) throw CanonicalJson
                    .Failure("/alarms", "collision", "Duplicate alarm numbers.");
            }

            private Entry Add(ProjectObject model, string phase, string? namingKind, string tool, Dictionary<string, JsonElement> arguments, string readback)
            {
                if (namingKind != null) naming.Check(namingKind, model.Name);
                var key = Key(model);
                if (entries.ContainsKey(key)) throw CanonicalJson.Failure("/expected/" + key, "duplicate-key", "Duplicate generated object key.");
                if (entries.Count >= options.MaximumObjects) throw CanonicalJson.Failure("/expected", "limit", "Expanded model exceeds its object budget.");
                var entry = new Entry { Key = key, Phase = phase, Object = model, Tool = tool, Arguments = arguments, Readback = readback };
                entries.Add(key, entry);
                if (!(model is ProjectDevice) && entries.ContainsKey(ProjectModelComparison.Key(model.Station, "device", model.Station)))
                    entry.Dependencies.Add(ProjectModelComparison.Key(model.Station, "device", model.Station));
                switch (model)
                {
                    case ProjectDevice device: expected.Devices.Add(device); break; case ProjectBlock block: expected.Blocks.Add(block); break;
                    case ProjectType type: expected.Types.Add(type); break; case ProjectTagTable table: expected.TagTables.Add(table); break;
                    case ProjectTag tag: expected.Tags.Add(tag); break; case ProjectGroup group: expected.Groups.Add(group); break;
                    case ProjectNetwork network: expected.Networks.Add(network); break; case ProjectAlarm alarm: expected.Alarms.Add(alarm); break;
                    case ProjectScreen screen: expected.Screens.Add(screen); break; case ProjectExternalSource source: expected.ExternalSources.Add(source);
                    break;
                }
                return entry;
            }

            private static string Key(ProjectObject model) => ProjectModelComparison.Key(model.Station, model switch
            {
                ProjectDevice => "device", ProjectBlock => "block", ProjectType => "type", ProjectTagTable => "tagTable", ProjectTag => "tag", ProjectGroup =>
                    "group",
                ProjectNetwork => "network", ProjectAlarm => "alarm", ProjectScreen => "screen", _ => "externalSource" }, model is ProjectGroup group ? group
                    .Kind + "/" + model.Name : model.Name);
            private static Dictionary<string, JsonElement> Args(params (string Name, object Value)[] values) => values
                .ToDictionary(v => v.Name, v => JsonSerializer.SerializeToElement(v.Value), StringComparer.Ordinal);
            private static string HashText(string text) => "sha256:" + CanonicalJson.HashBytes(Encoding.UTF8.GetBytes(text));

            private GenerationPlanStepsItem Step(string key, string phase, string op, string tool, Dictionary<string, JsonElement> arguments, string readback,
                    string? contains = null, string? fingerprint = null)
                => new GenerationPlanStepsItem { Key = key, Phase = phase, Op = op, Tool = tool, Arguments = arguments,
                ArgumentDigest = CanonicalJson.ArgumentHash(JsonSerializer.SerializeToElement(arguments)), Availability = GenerationAvailability
                    .Get(machine.Target.Release, tool),
                    Expect = new GenerationPlanStepsItemExpect { Readback = readback, Contains = contains, Fingerprint = fingerprint } };

            private void ProjectAndFinalActions(Dictionary<string, GenerationPlanStepsItem> steps)
            {
                if (machine.Target.Project.Mode == "new")
                {
                    naming.Check("project", machine.Target.Project.Name!);
                    var key = "project/create";
                    steps
                        .Add(key,
                            Step(key, "project", "create", "CreateProject",
                                Args(("directoryPath", machine.Target.Project.Directory!), ("projectName", machine.Target.Project.Name!), ("dryRun", false),
                                    ("confirm", true)),
                                "GetSessionState"));
                    steps[key].Review = new GenerationStepReview { Mode = "transition", Rule = StepReviewRules.CreateProject,
                        LateArguments = new List<string>(), Expect = Args(("projectPath", plan.Target.ProjectIdentity), ("emptyProject", true)),
                        Effect = Args(("createdProject", plan.Target.ProjectIdentity)) };
                    foreach (var step in steps.Values.Where(s => s.Key != key)) step.DependsOn.Add(key);
                }
                else if (steps.Count > 0 && GenerationAvailability.Get(machine.Target.Release, "ArchiveSavedProject").Tool == "present")
                {
                    var key = "project/backup";
                    var path = options.BackupPath ?? options.ArtifactRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/backups/" + plan.PlanId + ".zap"
                        + (machine.Target.Release == "15.1" ? "15_1" : machine.Target.Release);
                    steps.Add(key, Step(key, "project", "execute", "ArchiveSavedProject", Args(("archivePath", path), ("dryRun", false)), "GetSessionState"));
                    foreach (var step in steps.Values.Where(s => s.Key != key)) step.DependsOn.Add(key);
                }
                if (steps.Count == 0 && plan.Adopt.Count == 0) return;
                var prerequisites = steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var station in machine.Devices.Select(d => d.Station).Distinct().OrderBy(s => s, StringComparer.Ordinal))
                {
                    var key = station + "/compile";
                    var step =
                        Step(key, "compile", "execute", "CompilePlcDiagnostics",
                            Args(("softwarePath", Software(station)), ("dryRun", false), ("confirm", true)), "CompilePlcDiagnostics");
                    step.DependsOn = prerequisites.ToList(); steps.Add(key, step);
                }
                foreach (var import in steps.Values.Where(s => s.Tool == "ImportPlcExternalSource").ToArray())
                {
                    string software = import.Arguments["softwarePath"].GetString()!;
                    var cleanup = Step(import.Key + "/cleanup", "cleanup", "execute", "DeletePlcExternalSource",
                        Args(("softwarePath", software), ("groupPath", ""), ("externalSourceName", import.Expect.Contains!), ("dryRun", false),
                                ("confirm", true)),
                            "ListPlcExternalSources");
                    cleanup.DependsOn = prerequisites.Concat(steps.Values.Where(s => s.Phase == "compile").Select(s => s.Key)).Distinct().ToList();
                    steps.Add(cleanup.Key, cleanup);
                }
                var save = Step("project/save", "save", "execute", "SaveProject", Args(("dryRun", false), ("confirm", true)), "GetSessionState");
                save.DependsOn = steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); steps.Add(save.Key, save);
                if (options.RecordPath != null)
                {
                    var record =
                        Step("project/record", "record", "execute", "PublishGenerationRecord",
                            Args(("planId", plan.PlanId), ("recordPath", options.RecordPath), ("dryRun", false)), "GetGenerationRun");
                    record.DependsOn = steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    record.Review = new GenerationStepReview { Rule = StepReviewRules.Record,
                        Expect = Args(("recordPath", options.RecordPath), ("previousRecordHash", options.PreviousRecordHash)),
                        Witness = new Dictionary<string, string> { ["planHash"] = "sha256" }, Effect = Args(("recordPath", options.RecordPath)) };
                    steps.Add(record.Key, record);
                }
            }
        }
    }
}
