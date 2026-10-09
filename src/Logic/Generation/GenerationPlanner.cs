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
    public static class GenerationPlanner
    {
        public static IReadOnlyList<string> Phases { get; } = Array.AsReadOnly(new[] { "project", "hardware", "network", "plcStructure", "plcTypes",
            "plcLibrary", "plcProgram", "plcTags", "alarms", "hmi", "compile", "save" });

        public static GenerationPlanningResult Build(StandardPackage package, string machineJson, ProjectModel? observed, GenerationPlanningOptions options,
            IEnumerable<StandardPackage>? inheritedPackages = null)
            => new Expansion(EffectiveGenerationPackage.Resolve(package, inheritedPackages ?? Array.Empty<StandardPackage>()), machineJson, observed, options).Build();

        private sealed class Entry
        {
            internal string Key = "";
            internal string Phase = "";
            internal ProjectObject Object = null!;
            internal string Tool = "";
            internal string Readback = "";
            internal Dictionary<string, JsonElement> Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            internal HashSet<string> Dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal string? Unavailable;
            internal List<(string Tool, Dictionary<string, JsonElement> Arguments)> FollowUps = new List<(string, Dictionary<string, JsonElement>)>();
        }

        private sealed class Expansion
        {
            private readonly StandardPackage package;
            private readonly StandardPackageManifest manifest;
            private readonly MachineDescription machine;
            private readonly ProjectModel observed;
            private readonly GenerationPlanningOptions options;
            private readonly ProjectModel expected = new ProjectModel();
            private readonly NamingPart namingPart;
            private readonly StructurePart structure;
            private readonly HardwarePart hardware;
            private readonly LibraryPart library;
            private readonly AlarmsPart alarms;
            private readonly HmiPart hmi;
            private readonly GenerationNamingEngine naming;
            private readonly GenerationAllocator allocator;
            private readonly Dictionary<string, DeviceTypeRule> rules;
            private readonly Dictionary<string, (MachineDescriptionTopology Node, MachineDescriptionTopology Unit)> topology = new Dictionary<string, (MachineDescriptionTopology, MachineDescriptionTopology)>(StringComparer.Ordinal);
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, (LibraryPartTypesItem Definition, Entry Entry)> types = new Dictionary<string, (LibraryPartTypesItem, Entry)>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<SclCall>> calls = new Dictionary<string, List<SclCall>>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> callGroups = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string[]> sourceReferences = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            private readonly List<GenerationArtifact> artifacts = new List<GenerationArtifact>();
            private readonly GenerationPlan plan;

            internal Expansion(StandardPackage package, string machineJson, ProjectModel? observed, GenerationPlanningOptions options)
            {
                this.package = package;
                manifest = package.Manifest;
                if (manifest.Extends != null) throw CanonicalJson.Failure("/extends", "effective-package", "Resolve and validate the effective inherited package before planning.");
                machine = package.ValidateMachine(machineJson);
                if (!VersionMatches(machine.Standard.Version, manifest.Version)) throw CanonicalJson.Failure("/standard/version", "version", "Selected package does not satisfy the machine version range.");
                if (options.MaximumObjects < 1 || options.MaximumObjects > 10000) throw CanonicalJson.Failure("/options/maximumObjects", "limit", "Object limit must be 1..10000.");
                if (string.IsNullOrEmpty(options.ArtifactRoot) || !Regex.IsMatch(options.ArtifactRoot, @"\A(?:[A-Za-z]:[/\\]|/)", RegexOptions.CultureInvariant))
                    throw CanonicalJson.Failure("/options/artifactRoot", "path", "Planning requires an absolute, caller-selected staging root.");
                this.options = options;
                this.observed = observed ?? new ProjectModel();
                if (!this.observed.Complete) throw CanonicalJson.Failure("/observed", "readback-incomplete", "Planning requires complete readback.");
                if (machine.Target.Project.Mode == "existing" && (observed == null || observed.ProjectIdentity != machine.Target.Project.ProjectIdentity))
                    throw CanonicalJson.Failure("/observed/projectIdentity", "identity", "Readback identity does not match the bound target.");
                namingPart = Part(manifest.Parts.Naming, new NamingPart()); structure = Part(manifest.Parts.Structure, new StructurePart());
                hardware = Part(manifest.Parts.Hardware, new HardwarePart()); library = Part(manifest.Parts.Library, new LibraryPart());
                alarms = Part(manifest.Parts.Alarms, new AlarmsPart()); hmi = Part(manifest.Parts.Hmi, new HmiPart());
                naming = new GenerationNamingEngine(namingPart, manifest.Languages.Default);
                allocator = new GenerationAllocator(hardware, structure, alarms.Numbering);
                rules = (manifest.Parts.Rules ?? new List<string>()).Select(package.GetPart<DeviceTypeRule>).ToDictionary(r => r.DeviceType, StringComparer.Ordinal);
                allocator.Prepare(machine, rules);
                Walk(machine.Topology, null);
                var machineHash = GenerationDocuments.MachineHash(machineJson);
                var seed = CanonicalJson.Hash(JsonSerializer.SerializeToElement(new { package = package.ContentHash, machine = machineHash,
                    observed = ProjectModelComparison.Index(this.observed).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => ProjectModelComparison.Properties(p.Value), StringComparer.Ordinal),
                    identity = this.observed.ProjectIdentity, options }));
                plan = new GenerationPlan { Schema = "tiamcp.plan/1", PlanId = "gen-" + seed.Substring(7), PlanHash = "sha256:" + new string('0', 64),
                    Package = new GenerationPlanPackage { Id = manifest.Id, Version = manifest.Version, Hash = package.ContentHash }, MachineHash = machineHash,
                    Target = new GenerationPlanTarget { Release = machine.Target.Release, Mode = machine.Target.Project.Mode,
                        ProjectIdentity = machine.Target.Project.ProjectIdentity ?? "new:" + machine.Target.Project.Directory + "/" + machine.Target.Project.Name,
                        SoftwarePath = machine.Target.Project.SoftwarePath }, Phases = Phases.ToList() };
                expected.ProjectIdentity = plan.Target.ProjectIdentity;

                T Part<T>(string? path, T empty) where T : class => path == null ? empty : package.GetPart<T>(path);
                void Walk(IEnumerable<MachineDescriptionTopology> nodes, MachineDescriptionTopology? unit)
                {
                    foreach (var node in nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
                    {
                        var parent = node.Kind == "unit" ? node : unit ?? node;
                        topology.Add(node.Id, (node, parent));
                        Walk(node.Children ?? new List<MachineDescriptionTopology>(), parent);
                    }
                }
            }

            internal GenerationPlanningResult Build()
            {
                Stations();
                Structure();
                // Tags are expanded before instances, so tag(signal.role) is independent of emit order.
                foreach (var device in OrderedDevices()) Expand(device, tagsOnly: true);
                foreach (var device in OrderedDevices()) Expand(device, tagsOnly: false);
                CallBlocks();
                ResolveReferences();
                var differences = ProjectModelComparison.Compare(expected, observed).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var difference in differences.Values.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    if (difference.Operation == "skip") plan.Skipped.Add(new GenerationPlanSkippedItem { Key = difference.Key, Reason = "exists-identical" });
                    if (difference.Operation == "conflict") plan.Conflicts.Add(new GenerationPlanConflictsItem { Key = difference.Key, Reason = "exists-different", Detail = difference.Detail });
                }
                var unavailable = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (var entry in entries.Values.Where(e => differences[e.Key].Operation == "create"))
                {
                    if (entry.Unavailable != null) Unavailable(entry.Phase, entry.Unavailable);
                    var availability = GenerationAvailability.Get(machine.Target.Release, entry.Tool);
                    if (availability.Tool == "absent") Unavailable(entry.Phase, "Release " + machine.Target.Release + " has no " + entry.Tool + " tool.");
                    foreach (var followUp in entry.FollowUps)
                        if (GenerationAvailability.Get(machine.Target.Release, followUp.Tool).Tool == "absent") Unavailable(entry.Phase, "Release " + machine.Target.Release + " has no " + followUp.Tool + " tool.");
                }
                bool changed;
                do
                {
                    changed = false;
                    foreach (var entry in entries.Values.Where(e => differences[e.Key].Operation == "create" && !unavailable.ContainsKey(e.Phase)))
                        foreach (var dependency in entry.Dependencies)
                            if (differences[dependency].Operation == "conflict" || differences[dependency].Operation == "create" && unavailable.ContainsKey(entries[dependency].Phase))
                            { Unavailable(entry.Phase, "Prerequisite " + dependency + " is conflicting or unavailable."); changed = true; break; }
                } while (changed);
                var selected = entries.Values.Where(e => differences[e.Key].Operation == "create" && !unavailable.ContainsKey(e.Phase)).ToArray();
                var steps = selected.ToDictionary(e => e.Key, e => Step(e.Key, e.Phase, "create", e.Tool, e.Arguments, e.Readback, e.Object.Name, ProjectModelComparison.Fingerprint(e.Object)), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in selected)
                    steps[entry.Key].DependsOn = entry.Dependencies.Where(steps.ContainsKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var entry in selected)
                {
                    var previous = entry.Key;
                    foreach (var followUp in entry.FollowUps)
                    {
                        var key = entry.Key + "/" + followUp.Tool;
                        var step = Step(key, entry.Phase, "execute", followUp.Tool, followUp.Arguments, entry.Readback, entry.Object.Name, ProjectModelComparison.Fingerprint(entry.Object));
                        step.DependsOn.Add(previous); steps.Add(key, step); previous = key;
                    }
                    if (previous != entry.Key)
                        foreach (var step in steps.Values.Where(s => s.Key != entry.Key && s.Key != previous && s.DependsOn.Contains(entry.Key)))
                        { step.DependsOn.Remove(entry.Key); step.DependsOn.Add(previous); }
                }
                ProjectAndFinalActions(steps);
                if (steps.Count > 10000) throw CanonicalJson.Failure("/steps", "limit", "Plan exceeds 10000 steps.");
                if (steps.Count != 0)
                {
                    var order = ImportDependencyPlanner.BuildGeneration(steps.Values.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new ImportOrderItem
                    { Id = s.Key, Target = s.Phase, Priority = Phases.ToList().IndexOf(s.Phase), Dependencies = s.DependsOn.ToArray() }).ToArray());
                    if (!order.Valid) throw new GenerationValidationException(order.Issues.Select(i => new GenerationValidationError("/steps/" + i.Id, "dependency", i.Message)));
                    var ids = order.Order.Select((key, index) => new { key, id = "s" + (index + 1).ToString("D5", CultureInfo.InvariantCulture) }).ToDictionary(p => p.key, p => p.id, StringComparer.OrdinalIgnoreCase);
                    foreach (var key in order.Order)
                    {
                        var step = steps[key]; step.Id = ids[key]; step.DependsOn = step.DependsOn.Select(d => ids[d]).OrderBy(d => d, StringComparer.Ordinal).ToList(); plan.Steps.Add(step);
                    }
                }
                plan.Unavailable = Phases.Where(unavailable.ContainsKey).Select(phase => new GenerationPlanUnavailableItem { Phase = phase, Reason = string.Join(" ", unavailable[phase]) }).ToList();
                var orderedArtifacts = artifacts.OrderBy(a => a.Path, StringComparer.Ordinal).ToArray();
                plan.Artifacts = orderedArtifacts.Select(a => new FileDigest { Path = a.Path, Sha256 = a.Sha256 }).ToList();
                plan.PlanHash = GenerationDocuments.PlanHash(GenerationDocuments.Canonical(plan));
                return new GenerationPlanningResult(plan, expected, Array.AsReadOnly(orderedArtifacts), options.ArtifactRoot);

                void Unavailable(string phase, string reason)
                {
                    if (!unavailable.TryGetValue(phase, out var reasons)) unavailable.Add(phase, reasons = new SortedSet<string>(StringComparer.Ordinal));
                    reasons.Add(reason);
                }
            }

            private IEnumerable<MachineDescriptionDevicesItem> OrderedDevices() => machine.Devices.OrderBy(d => d.Station, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal);
            private string Software(string station) => options.SoftwarePaths.TryGetValue(station, out var path) ? path
                : machine.Target.Project.SoftwarePath != null && machine.Stations.Count == 1 ? machine.Target.Project.SoftwarePath : station;

            private void Stations()
            {
                foreach (var station in machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    var variants = hardware.Roles.SingleOrDefault(r => r.Id == station.Role)?.Variants.Where(v => v.Releases.Contains(machine.Target.Release)).ToArray()
                        ?? Array.Empty<HardwarePartRolesItemVariantsItem>();
                    if (variants.Length > 1) throw CanonicalJson.Failure("/hardware/roles/" + station.Role, "ambiguous", "Multiple hardware variants select this release.");
                    var variant = variants.SingleOrDefault();
                    var existing = observed.Devices.SingleOrDefault(d => d.Station == station.Id && d.Name == station.Id);
                    var article = station.Article ?? variant?.Article ?? existing?.Article ?? "";
                    var device = new ProjectDevice { Station = station.Id, Name = station.Id, Article = article, Firmware = station.Firmware ?? variant?.Firmware ?? "" };
                    var stationEntry = Add(device, "hardware", "device", "CreateDevice", Args(("orderNumber", article), ("version", device.Firmware), ("deviceName", device.Name)), "GetProjectTree");
                    if (article.Length == 0) stationEntry.Unavailable = "Station " + station.Id + " needs an exact catalog article or an observed device; the station kind is not a catalog identifier.";
                    foreach (var module in variant?.Modules ?? new List<HardwarePartRolesItemVariantsItemModulesItem>())
                    {
                        var parent = options.DeviceItemPaths.TryGetValue(station.Id, out var path) ? path : "";
                        var item = new ProjectDevice { Station = station.Id, Name = module.Id, Article = module.Article, ParentPath = parent, Slot = module.Slot };
                        var entry = Add(item, "hardware", "device", "PlugDeviceItem", Args(("deviceItemPath", parent), ("orderNumber", module.Article), ("name", module.Id), ("positionNumber", module.Slot), ("dryRun", false)), "GetDeviceItemTree");
                        entry.Dependencies.Add(Key(device));
                        if (parent.Length == 0) entry.Unavailable = "Module placement needs the exact parent device-item path for " + station.Id + ".";
                    }
                    if (station.Ip != null || station.ProfinetName != null)
                    {
                        var path = options.DeviceItemPaths.TryGetValue(station.Id, out var itemPath) ? itemPath : "";
                        var network = new ProjectNetwork { Station = station.Id, Name = station.Id + "_Network", DeviceItemPath = path, InterfaceIndex = 0,
                            SubnetType = "IndustrialEthernet", Ip = station.Ip == null ? null : allocator.Ip(station.Id), ProfinetName = station.ProfinetName };
                        var entry = Add(network, "network", "network", "EnsureSubnet", Args(("anchorDeviceItemPath", path), ("subnetName", network.Name), ("subnetType", network.SubnetType)), "GetDeviceItemNetworkInfo");
                        entry.Unavailable = "IP/PROFINET assignment needs exact writable attribute names from readback; EnsureSubnet alone does not set node addresses.";
                        entry.Dependencies.Add(Key(device));
                    }
                }
            }

            private void Structure()
            {
                foreach (var station in machine.Stations.Where(s => machine.Devices.Any(d => d.Station == s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    foreach (var group in structure.Groups.OrderBy(g => g.Id, StringComparer.Ordinal))
                    {
                        var contexts = group.Path.Contains("unit.") ? topology.Values.Select(t => t.Unit).Distinct().OrderBy(u => u.Id, StringComparer.Ordinal).Select(u => Context(null, u, station)).ToArray()
                            : new[] { Context(null, null, station) };
                        foreach (var context in contexts)
                        {
                            var path = naming.Expand(group.Path, context);
                            Group(station.Id, group.Kind, path);
                        }
                    }
                    foreach (var block in structure.OrganizationBlocks ?? new List<StructurePartOrganizationBlocksItem>())
                    {
                        var name = naming.Expand(block.Name, Context(null, null, station));
                        var model = new ProjectBlock { Station = station.Id, Name = name, Kind = "OB", Number = block.Number };
                        var entry = Add(model, "plcProgram", "OB", "ImportPlcExternalSource", Args(), "ListPlcBlocks");
                        entry.Unavailable = "Organization block " + name + " requires a package SCL implementation with event " + block.Event + "; a name/number does not define its interface.";
                    }
                }
            }

            private Entry Group(string station, string kind, string path)
            {
                if (kind != "blocks" && kind != "types")
                {
                    var other = new ProjectGroup { Station = station, Name = path, Kind = kind };
                    if (entries.TryGetValue(Key(other), out var existing)) return existing;
                    foreach (var segment in path.Split('/')) naming.Check(kind == "tags" ? "tagGroup" : kind == "devices" ? "deviceGroup" : "hmiGroup", segment);
                    var entry = Add(other, "plcStructure", null, kind == "devices" ? "ManageDeviceUserGroup" : kind == "tags" ? "ManagePlcUserGroup" : "ManageUnifiedEngineeringObject", Args(), "GetProjectTree");
                    entry.Unavailable = "Group kind " + kind + " needs exact tool category/parent bindings before Apply.";
                    return entry;
                }
                var parts = path.Split('/');
                if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) throw CanonicalJson.Failure("/structure/groups", "path", "Group path contains an empty or traversal segment.");
                Entry? parent = null;
                for (var i = 0; i < parts.Length; i++)
                {
                    naming.Check(kind == "blocks" ? "blockGroup" : "typeGroup", parts[i]);
                    var group = new ProjectGroup { Station = station, Name = string.Join("/", parts.Take(i + 1)), Kind = kind };
                    var key = Key(group);
                    if (!entries.TryGetValue(key, out var entry)) entry = Add(group, "plcStructure", null, kind == "blocks" ? "CreatePlcBlockGroup" : "CreatePlcTypeGroup",
                        kind == "blocks" ? Args(("softwarePath", Software(station)), ("groupPath", group.Name)) : Args(("softwarePath", Software(station)), ("groupPath", group.Name), ("dryRun", false)), "GetProjectTree");
                    if (parent != null) entry.Dependencies.Add(parent.Key);
                    parent = entry;
                }
                return parent!;
            }

            private void Expand(MachineDescriptionDevicesItem device, bool tagsOnly)
            {
                var rule = rules[device.Type];
                foreach (var emit in rule.Emit.Where(e => (e.Kind == "plc.tag") == tagsOnly))
                {
                    foreach (var context in Iterations(device, rule, emit.ForEach))
                    {
                        if (emit.When.HasValue && !Condition(emit.When.Value, context)) continue;
                        string Text(string? value, string field) => value == null ? throw CanonicalJson.Failure("/emit/" + field, "required", "Emit " + emit.Kind + " requires " + field + ".") : naming.Expand(value, context, BuiltIn);
                        JsonElement BuiltIn(string function, IReadOnlyList<JsonElement> args)
                        {
                            var active = GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(context["device"]);
                            switch (function)
                            {
                                case "alloc.io": return JsonSerializer.SerializeToElement(allocator.Io(active.Station, active.Id, SignalRole(args[0])));
                                case "alloc.ip": return JsonSerializer.SerializeToElement(allocator.Ip(args[0].ValueKind == JsonValueKind.Object ? args[0].GetProperty("id").GetString()! : GenerationNamingEngine.Value(args[0])));
                                case "seq": return JsonSerializer.SerializeToElement(allocator.Number(args.Count == 0 ? "sequence" : GenerationNamingEngine.Value(args[0]), active.Station + "/" + active.Id + "/" + (emit.Id ?? emit.Kind + ":" + rule.Emit.IndexOf(emit).ToString(CultureInfo.InvariantCulture)) + "/" + (context.TryGetValue("item", out var item) ? CanonicalJson.Hash(item) : "device")));
                                case "tag":
                                    var key = GenerationAllocator.SignalKey(active, SignalRole(args[0]));
                                    if (!tags.TryGetValue(key, out var name)) throw CanonicalJson.Failure("/tag/" + key, "reference", "Signal has no emitted PLC tag.");
                                    return JsonSerializer.SerializeToElement(name);
                                default: throw CanonicalJson.Failure("/placeholder", "function", "Unknown built-in function.");
                            }
                        }
                        var activeDevice = GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(context["device"]);
                        var station = context["station"].GetProperty("id").GetString()!;
                        var extra = (emit.Arguments ?? new Dictionary<string, JsonElement>()).ToDictionary(p => p.Key,
                            p => p.Value.ValueKind == JsonValueKind.String ? JsonSerializer.SerializeToElement(naming.Expand(p.Value.GetString()!, context, BuiltIn)) : p.Value, StringComparer.Ordinal);
                        switch (emit.Kind)
                        {
                            case "plc.tag":
                                var signal = context["signal"];
                                var role = signal.GetProperty("role").GetString()!;
                                var dataType = emit.Type == null ? signal.GetProperty("type").GetString()! : Text(emit.Type, "type");
                                var tableName = Text(emit.Table, "table");
                                var table = new ProjectTagTable { Station = station, Name = tableName };
                                if (!entries.ContainsKey(Key(table))) Add(table, "plcTags", "tagTable", "CreatePlcTagTable", Args(("plc", Software(station)), ("group", ""), ("name", tableName), ("dryRun", false), ("confirm", true)), "ListPlcTagTables");
                                var address = IoAddress.Parse(Text(emit.Address, "address"));
                                if (address.Text != allocator.Io(station, activeDevice.Id, role) || address.Width != GenerationAllocator.Width(dataType))
                                    throw CanonicalJson.Failure("/emit/address", "allocation", "Tag address/type must match the allocated signal.");
                                var tag = new ProjectTag { Station = station, Name = Text(emit.Name, "name"), Table = tableName, DataType = dataType, Address = address.Text };
                                var tagEntry = Add(tag, "plcTags", "tag", "CreatePlcTag", Args(("plc", Software(station)), ("table", tableName), ("name", tag.Name), ("dataType", dataType), ("address", address.Text), ("dryRun", false), ("confirm", true)), "ListPlcTags");
                                tagEntry.Dependencies.Add(Key(table));
                                var signalKey = GenerationAllocator.SignalKey(activeDevice, role);
                                if (tags.ContainsKey(signalKey)) throw CanonicalJson.Failure("/emit/tag", "duplicate-key", "A signal emits more than one tag.");
                                tags.Add(signalKey, tag.Name);
                                break;
                            case "plc.instance":
                            case "plc.call":
                                var selected = Type(station, Text(emit.Type, "type"), Text(emit.Group ?? "", "group"));
                                var definition = selected.Definition;
                                if (emit.Kind == "plc.instance" && definition.Kind != "FB" || emit.Kind == "plc.call" && definition.Kind != "FC")
                                    throw CanonicalJson.Failure("/emit/type", "reference", "Instances require FB types; direct calls require FC types.");
                                var instanceName = emit.Kind == "plc.instance" ? Text(emit.Name, "name") : selected.Entry.Object.Name;
                                if (emit.Kind == "plc.instance")
                                {
                                    var instance = new ProjectBlock { Station = station, Name = instanceName, Kind = "DB", InstanceType = selected.Entry.Object.Name, Group = Text(emit.Group ?? "", "group"),
                                        InterfaceFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(definition.Interface)), CodeFingerprint = HashText(GenerationScl.Instance(instanceName, selected.Entry.Object.Name)),
                                        Members = ((ProjectBlock)selected.Entry.Object).Members.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
                                    var entry = Source(instance, "plcProgram", "instanceDb", GenerationScl.Instance(instanceName, selected.Entry.Object.Name)); entry.Dependencies.Add(selected.Entry.Key);
                                }
                                if (emit.CallIn != null)
                                {
                                    var target = Text(emit.CallIn, "callIn");
                                    var key = station + "/" + target;
                                    if (!calls.TryGetValue(key, out var list)) calls.Add(key, list = new List<SclCall>());
                                    var bindings = (emit.Bind ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => Text(p.Value, "bind"), StringComparer.Ordinal);
                                    var members = (definition.Interface.In ?? new List<InterfaceMember>()).Concat(definition.Interface.Out ?? new List<InterfaceMember>()).Concat(definition.Interface.InOut ?? new List<InterfaceMember>()).ToArray();
                                    if (bindings.Keys.Any(roleKey => !members.Any(m => m.Role == roleKey))) throw CanonicalJson.Failure("/bind", "reference", "Binding role is absent from the selected type interface.");
                                    foreach (var member in members)
                                        if (!bindings.ContainsKey(member.Role) && !(member.Role.StartsWith("signal.", StringComparison.Ordinal) && !activeDevice.Io.ContainsKey(member.Role.Substring(7)) && rule.Signals.Any(s => s.Role == member.Role.Substring(7) && s.Optional == true)))
                                            throw CanonicalJson.Failure("/bind/" + member.Role, "reference", "Required interface binding is missing.");
                                    foreach (var member in (definition.Interface.Out ?? new List<InterfaceMember>()).Concat(definition.Interface.InOut ?? new List<InterfaceMember>()))
                                        if (bindings.TryGetValue(member.Role, out var targetBinding) && GenerationScl.BindingRoot(targetBinding).Length == 0 && !IoAddress.IsIo(targetBinding))
                                            throw CanonicalJson.Failure("/bind/" + member.Role, "scl-binding", "OUT and IN_OUT require a writable symbolic/IO binding.");
                                    if (list.Any(c => c.Instance == instanceName)) throw CanonicalJson.Failure("/calls", "duplicate-key", "A call block contains a duplicate instance call.");
                                    list.Add(new SclCall { Instance = instanceName, Target = target, Type = definition, Bindings = bindings });
                                    callGroups[key] = Text(emit.Group ?? "", "group");
                                }
                                else if (emit.Kind == "plc.call") throw CanonicalJson.Failure("/emit/callIn", "required", "A direct call requires callIn.");
                                break;
                            case "plc.type": Type(station, Text(emit.Type, "type"), Text(emit.Group ?? "", "group")); break;
                            case "plc.group": Group(station, Text(emit.Role ?? "blocks", "role"), Text(emit.Group ?? emit.Name, "group")); break;
                            case "alarm": Alarm(activeDevice, emit, Text); break;
                            case "hmi.screen":
                            case "hmi.widget": Screen(activeDevice, emit, Text); break;
                            case "network":
                                var network = new ProjectNetwork { Station = station, Name = Text(emit.Name, "name"), DeviceItemPath = extra.TryGetValue("deviceItemPath", out var devicePath) ? devicePath.GetString()! : "",
                                    InterfaceIndex = extra.TryGetValue("interfaceIndex", out var index) ? index.GetInt32() : 0, SubnetType = "IndustrialEthernet" };
                                var networkEntry = Add(network, "network", "network", "EnsureSubnet", Args(("anchorDeviceItemPath", network.DeviceItemPath), ("subnetName", network.Name), ("subnetType", network.SubnetType)), "GetDeviceItemNetworkInfo");
                                networkEntry.FollowUps.Add(("AttachDeviceNodeToSubnet", Args(("deviceItemPath", network.DeviceItemPath), ("interfaceIndex", network.InterfaceIndex), ("subnetName", network.Name))));
                                if (network.DeviceItemPath.Length == 0 || !extra.ContainsKey("interfaceIndex")) networkEntry.Unavailable = "Network emit requires an exact device-item path and interface index from readback.";
                                break;
                            case "hardware.device":
                            case "hardware.module":
                                var hardwareDevice = new ProjectDevice { Station = station, Name = Text(emit.Name, "name"), Article = extra.TryGetValue("orderNumber", out var article) ? article.GetString()! : "",
                                    Firmware = extra.TryGetValue("version", out var version) ? version.GetString()! : "", ParentPath = extra.TryGetValue("deviceItemPath", out var parent) ? parent.GetString()! : null,
                                    Slot = emit.Slot?.ValueKind == JsonValueKind.Number ? emit.Slot.Value.GetInt32() : (int?)null };
                                var hardwareEntry = Add(hardwareDevice, "hardware", "device", emit.Kind == "hardware.device" ? "CreateDevice" : "PlugDeviceItem",
                                    emit.Kind == "hardware.device" ? Args(("orderNumber", hardwareDevice.Article), ("version", hardwareDevice.Firmware), ("deviceName", hardwareDevice.Name))
                                        : Args(("deviceItemPath", hardwareDevice.ParentPath ?? ""), ("orderNumber", hardwareDevice.Article), ("version", hardwareDevice.Firmware), ("name", hardwareDevice.Name), ("positionNumber", hardwareDevice.Slot ?? -1), ("dryRun", false)), "GetProjectTree");
                                if (hardwareDevice.Article.Length == 0 || emit.Kind == "hardware.module" && (!hardwareDevice.Slot.HasValue || string.IsNullOrEmpty(hardwareDevice.ParentPath))) hardwareEntry.Unavailable = "Hardware emit requires an exact article, parent path and explicit slot; automatic slot selection is not planned.";
                                break;
                        }
                    }
                }
            }

            private IReadOnlyDictionary<string, JsonElement> Context(MachineDescriptionDevicesItem? device, MachineDescriptionTopology? unit, MachineDescriptionStationsItem? station)
            {
                var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["machine"] = JsonSerializer.SerializeToElement(machine.Machine, JsonOptions), ["options"] = JsonSerializer.SerializeToElement(machine.Options, JsonOptions) };
                if (device != null)
                {
                    result["device"] = JsonSerializer.SerializeToElement(device, JsonOptions); unit ??= topology[device.Parent].Unit;
                    result["parent"] = JsonSerializer.SerializeToElement(topology[device.Parent].Node, JsonOptions);
                    station ??= machine.Stations.Single(s => s.Id == device.Station);
                    var signals = rules[device.Type].Signals.Where(s => s.Optional != true || device.Io.ContainsKey(s.Role)).ToDictionary(s => s.Role, s => Signal(s), StringComparer.Ordinal);
                    result["signals"] = JsonSerializer.SerializeToElement(signals); result["signal"] = result["signals"];
                }
                if (station != null) result["station"] = JsonSerializer.SerializeToElement(station, JsonOptions);
                if (unit != null)
                {
                    var unitFields = JsonSerializer.SerializeToElement(unit, JsonOptions).EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    // unit.<namingRule> aliases are explicit rule ids, evaluated lazily by the author via naming.* where allocation is needed.
                    foreach (var rule in namingPart.Rules.Where(r => r.Id == "tagTable" || r.Id == "callBlock" || r.Id == "hmiDb"))
                    {
                        var baseContext = new Dictionary<string, JsonElement>(result, StringComparer.Ordinal) { ["unit"] = JsonSerializer.SerializeToElement(unit, JsonOptions) };
                        unitFields[rule.Id] = JsonSerializer.SerializeToElement(naming.Expand(rule.Template, baseContext));
                    }
                    result["unit"] = JsonSerializer.SerializeToElement(unitFields);
                }
                return result;
            }

            private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            private static JsonElement Signal(DeviceTypeRuleSignalsItem signal) => JsonSerializer.SerializeToElement(new { role = signal.Role, dir = signal.Dir, type = signal.Type ?? GenerationAllocator.DefaultType(signal.Dir) });
            private static string SignalRole(JsonElement signal) => signal.ValueKind == JsonValueKind.Object ? signal.GetProperty("role").GetString()! : GenerationNamingEngine.Value(signal).Replace("signal.", "");

            private IEnumerable<IReadOnlyDictionary<string, JsonElement>> Iterations(MachineDescriptionDevicesItem device, DeviceTypeRule rule, string? collection)
            {
                var context = Context(device, null, null);
                if (collection == null) { yield return context; yield break; }
                IEnumerable<(string Root, JsonElement Item)> items = collection switch
                {
                    "signals" => rule.Signals.Where(s => s.Optional != true || device.Io.ContainsKey(s.Role)).OrderBy(s => s.Role, StringComparer.Ordinal).Select(s => ("signal", Signal(s))),
                    "devices" => OrderedDevices().Select(d => ("device", JsonSerializer.SerializeToElement(d, JsonOptions))),
                    "stations" => machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => ("station", JsonSerializer.SerializeToElement(s, JsonOptions))),
                    "topology" => topology.Values.Select(t => t.Node).OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => ("unit", JsonSerializer.SerializeToElement(n, JsonOptions))),
                    "children" => (topology[device.Parent].Node.Children ?? new List<MachineDescriptionTopology>()).OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => ("child", JsonSerializer.SerializeToElement(n, JsonOptions))),
                    _ => throw CanonicalJson.Failure("/forEach", "collection", "Unsupported collection.")
                };
                foreach (var item in items)
                {
                    var nested = item.Root == "device" ? Context(GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(item.Item), null, null).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                        : context.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    nested[item.Root] = item.Item; nested["item"] = item.Item;
                    yield return nested;
                }
            }

            private static bool Condition(JsonElement condition, IReadOnlyDictionary<string, JsonElement> context)
            {
                if (condition.ValueKind == JsonValueKind.String) return GenerationNamingEngine.TryResolve(context, condition.GetString()!, out var value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.False;
                if (condition.TryGetProperty("exists", out var exists)) return GenerationNamingEngine.TryResolve(context, exists.GetString()!, out var value) && value.ValueKind != JsonValueKind.Null;
                var equals = condition.TryGetProperty("equals", out var predicate);
                if (!equals) predicate = condition.GetProperty("in");
                if (!GenerationNamingEngine.TryResolve(context, predicate.GetProperty("path").GetString()!, out var actual)) return false;
                return equals ? CanonicalJson.Hash(actual) == CanonicalJson.Hash(predicate.GetProperty("value")) : predicate.GetProperty("values").EnumerateArray().Any(v => CanonicalJson.Hash(v) == CanonicalJson.Hash(actual));
            }

            private (LibraryPartTypesItem Definition, Entry Entry) Type(string station, string reference, string group)
            {
                var match = Regex.Match(reference, @"\Alib:([^/]+)/([^@]+)@(.+)\z", RegexOptions.CultureInvariant);
                if (!match.Success || match.Groups[1].Value != manifest.Id) throw CanonicalJson.Failure("/emit/type", "reference", "Type reference must resolve in the effective local package.");
                var id = match.Groups[2].Value;
                var type = library.Types.SingleOrDefault(t => t.Id == id) ?? throw CanonicalJson.Failure("/library/" + id, "reference", "Unknown type.");
                if (!VersionMatches(match.Groups[3].Value, type.Version)) throw CanonicalJson.Failure("/library/" + id, "version", "Type version does not satisfy the reference.");
                var key = station + "/" + id;
                if (types.TryGetValue(key, out var found))
                {
                    if (found.Entry.Object is ProjectBlock block && block.Group != group || found.Entry.Object is ProjectType udt && udt.Group != group)
                        throw CanonicalJson.Failure("/library/" + id, "collision", "One type is requested in different groups.");
                    return found;
                }
                var implementation = type.Implementations.FirstOrDefault(i => i.Releases.ValueKind == JsonValueKind.Array
                    ? i.Releases.EnumerateArray().Any(r => r.GetString() == machine.Target.Release) : GenerationModelValidation.ReleaseMatches(i.Releases.GetString()!, machine.Target.Release))
                    ?? throw CanonicalJson.Failure("/library/" + id, "release-coverage", "No implementation for this release.");
                Entry primary;
                if (implementation.Kind == "sclSource")
                {
                    var declarations = implementation.Files!.SelectMany(file => GenerationScl.Declarations(package.ReadFile(file))).ToArray();
                    var candidates = declarations.Where(d => d.Kind == type.Kind).ToArray();
                    if (candidates.Length != 1) throw CanonicalJson.Failure("/library/" + id, "ambiguous", "Implementation must have exactly one primary declaration of kind " + type.Kind + ".");
                    foreach (var declaration in declarations.OrderBy(d => d.Name, StringComparer.Ordinal))
                    {
                        ProjectObject model = declaration.Kind == "UDT" ? new ProjectType { Station = station, Name = declaration.Name, Group = group, InterfaceFingerprint = HashText(declaration.Content), Members = GenerationScl.Members(declaration.Content) }
                            : new ProjectBlock { Station = station, Name = declaration.Name, Group = group, Kind = declaration.Kind,
                                InterfaceFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(type.Interface)), CodeFingerprint = HashText(declaration.Content), Members = GenerationScl.Members(declaration.Content) };
                        if (entries.TryGetValue(Key(model), out var existing))
                        {
                            if (ProjectModelComparison.Fingerprint(existing.Object) != ProjectModelComparison.Fingerprint(model)) throw CanonicalJson.Failure("/library", "duplicate-key", "Different sources declare the same object.");
                        }
                        else
                        {
                            var entry = Source(model, declaration.Kind == "UDT" ? "plcTypes" : "plcLibrary", declaration.Kind, declaration.Content);
                            sourceReferences.Add(entry.Key, declaration.References);
                        }
                    }
                    primary = entries[ProjectModelComparison.Key(station, type.Kind == "UDT" ? "type" : "block", candidates[0].Name)];
                    var declaredMembers = primary.Object is ProjectBlock declaredBlock ? declaredBlock.Members : ((ProjectType)primary.Object).Members;
                    var contract = (type.Interface.In ?? new List<InterfaceMember>()).Concat(type.Interface.Out ?? new List<InterfaceMember>()).Concat(type.Interface.InOut ?? new List<InterfaceMember>()).Concat(type.Interface.Static ?? new List<InterfaceMember>()).ToArray();
                    if (contract.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != contract.Length || contract.Select(m => m.Role).Distinct(StringComparer.Ordinal).Count() != contract.Length)
                        throw CanonicalJson.Failure("/library/interface", "duplicate-key", "Interface names and binding roles must be unique.");
                    foreach (var member in contract)
                        if (!declaredMembers.TryGetValue(member.Name, out var declaredType) || !string.Equals(declaredType.Trim('"'), member.Type.Trim('"'), StringComparison.OrdinalIgnoreCase))
                            throw CanonicalJson.Failure("/library/interface/" + member.Name, "reference", "Logical interface does not match the primary SCL declaration.");
                }
                else
                {
                    var path = implementation.TypePath ?? implementation.MasterCopyPath!;
                    var name = path.Split('/').Last();
                    ProjectObject model = type.Kind == "UDT" ? new ProjectType { Station = station, Name = name, Group = group, InterfaceFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(type.Interface)) }
                        : new ProjectBlock { Station = station, Name = name, Kind = type.Kind, Group = group, LibraryTypeFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(implementation)) };
                    primary = Add(model, "plcLibrary", type.Kind, implementation.Kind == "globalLibrary" ? "PlaceLibraryType" : "CreateFromMasterCopy", Args(), type.Kind == "UDT" ? "ListPlcTypes" : "ListPlcBlocks");
                    primary.Unavailable = "Library placement requires P8-31f and an observed external library/type version; no tool or exact version is inferred.";
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
                var source = new ProjectExternalSource { Station = model.Station, Name = sourceName, Group = group, ContentFingerprint = "sha256:" + artifact.Sha256 };
                var sourceEntry = Add(source, phase, "externalSource", "ImportPlcExternalSource", Args(("softwarePath", Software(model.Station)), ("groupPath", group),
                    ("filePath", options.ArtifactRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/" + path), ("dryRun", false), ("confirm", true)), "ListPlcExternalSources");
                var entry = Add(model, phase, namingKind, "GenerateBlocksFromExternalSource", Args(("softwarePath", Software(model.Station)), ("externalSourceName", sourceName), ("dryRun", false), ("confirm", true)), model is ProjectType ? "ListPlcTypes" : "ListPlcBlocks");
                entry.Dependencies.Add(sourceEntry.Key);
                if (group.Length != 0)
                {
                    var groupEntry = Group(model.Station, model is ProjectType ? "types" : "blocks", group);
                    sourceEntry.Dependencies.Add(groupEntry.Key);
                }
                return entry;
            }

            private void CallBlocks()
            {
                foreach (var pair in calls.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var station = pair.Key.Substring(0, pair.Key.IndexOf('/'));
                    var name = pair.Value[0].Target;
                    var content = GenerationScl.Calls(name, pair.Value);
                    var block = new ProjectBlock { Station = station, Name = name, Kind = "FC", Group = callGroups[pair.Key], CodeFingerprint = HashText(content), InterfaceFingerprint = HashText("Void") };
                    var entry = Source(block, "plcProgram", "FC", content);
                    foreach (var call in pair.Value)
                    {
                        entry.Dependencies.Add(ProjectModelComparison.Key(station, "block", call.Instance));
                        foreach (var bindingPair in call.Bindings)
                        {
                            var binding = bindingPair.Value;
                            var parameter = (call.Type.Interface.In ?? new List<InterfaceMember>()).Concat(call.Type.Interface.Out ?? new List<InterfaceMember>()).Concat(call.Type.Interface.InOut ?? new List<InterfaceMember>()).Single(m => m.Role == bindingPair.Key);
                            var root = GenerationScl.BindingRoot(binding);
                            if (root.Length == 0) { GenerationScl.CheckLiteralType(binding, parameter.Type); continue; }
                            var candidates = entries.Values.Where(e => e.Object.Station == station && e.Object.Name == root && (e.Object is ProjectTag || e.Object is ProjectBlock)).ToArray();
                            if (candidates.Length != 1) throw CanonicalJson.Failure("/bind/" + root, "reference", "Binding does not resolve to one generated tag/block.");
                            entry.Dependencies.Add(candidates[0].Key);
                            var bindingPath = GenerationScl.BindingPath(binding);
                            var referenced = candidates[0].Object;
                            foreach (var member in bindingPath.Skip(1))
                            {
                                var members = referenced is ProjectBlock referencedBlock ? referencedBlock.Members : referenced is ProjectType referencedType ? referencedType.Members : new Dictionary<string, string>();
                                if (!members.TryGetValue(member, out var memberType)) throw CanonicalJson.Failure("/bind/" + binding, "reference", "Symbolic member does not exist in the generated interface.");
                                referenced = expected.Types.FirstOrDefault(t => t.Station == station && t.Name == memberType) ?? (ProjectObject)new ProjectTag { DataType = memberType };
                            }
                            var boundType = referenced is ProjectTag boundTag ? boundTag.DataType : referenced is ProjectType boundUdt ? boundUdt.Name : ((ProjectBlock)referenced).InstanceType;
                            if (boundType == null || !string.Equals(boundType.Trim('"'), parameter.Type.Trim('"'), StringComparison.OrdinalIgnoreCase))
                                throw CanonicalJson.Failure("/bind/" + binding, "type", "Binding data type differs from its interface parameter.");
                        }
                    }
                }
            }

            private void Alarm(MachineDescriptionDevicesItem device, DeviceTypeRuleEmitItem emit, Func<string?, string, string> text)
            {
                var classId = text(emit.Class, "class"); var textKey = text(emit.TextKey, "textKey");
                var alarmClass = alarms.Classes.SingleOrDefault(c => c.Id == classId) ?? throw CanonicalJson.Failure("/alarms/class", "reference", "Unknown alarm class.");
                if (!alarms.Texts.TryGetValue(textKey, out var texts) || manifest.Languages.Required.Any(l => !texts.ContainsKey(l))) throw CanonicalJson.Failure("/alarms/text", "language", "Alarm text lacks a required language.");
                var template = alarms.Templates.SingleOrDefault(t => t.DeviceType == device.Type && t.TextKey == textKey && t.Class == classId);
                if (template == null) throw CanonicalJson.Failure("/alarms/template", "reference", "Alarm emit does not resolve to one template.");
                if (alarms.Numbering == null) throw CanonicalJson.Failure("/alarms/numbering", "required", "Alarms require a number range.");
                if (!int.TryParse(text(alarms.Numbering.Template ?? "{{seq('alarms')}}", "number"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < alarms.Numbering.Start || number > alarms.Numbering.End)
                    throw CanonicalJson.Failure("/alarms/numbering", "range", "Alarm numbering template must yield an integer inside the declared range.");
                var name = emit.Name == null ? device.Id + "_" + textKey.Replace('.', '_') : text(emit.Name, "name");
                var model = new ProjectAlarm { Station = device.Station, Name = name, Number = number, Class = classId, Priority = alarmClass.Priority, Acknowledgement = alarmClass.Acknowledgement,
                    Texts = texts.ToDictionary(p => p.Key, p => text(p.Value, "text"), StringComparer.Ordinal), Backend = template.Backend };
                var entry = Add(model, "alarms", "alarm", template.Backend == "hmiDiscrete" ? "ManageUnifiedEngineeringObject" : "ImportPlcAlarmInstanceTexts", Args(), "ExportPlcAlarmInstanceTexts");
                entry.Unavailable = "Alarm template " + template.Id + " needs a concrete Program_Alarm binding or Unified tag/class mapping and import resource; metadata alone is not an executable alarm.";
            }

            private void Screen(MachineDescriptionDevicesItem device, DeviceTypeRuleEmitItem emit, Func<string?, string, string> text)
            {
                var name = text(emit.Screen ?? emit.Name, "screen");
                var station = machine.Stations.SingleOrDefault(s => s.Role == "hmi.panel")?.Id ?? device.Station;
                var template = text(emit.Template, "template");
                var content = package.Documents.TryGetValue(template, out var document) ? document : CanonicalJson.Parse(package.ReadFile(template));
                content = ExpandDocument(content, value => text(value, "template"));
                var model = new ProjectScreen { Station = station, Name = name, TemplateFingerprint = CanonicalJson.Hash(content), Design = content.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal) };
                if (emit.Kind == "hmi.widget")
                {
                    var key = Key(model);
                    Entry widgetScreen;
                    if (entries.TryGetValue(key, out var existingScreen)) widgetScreen = existingScreen;
                    else widgetScreen = Add(model, "hmi", "screen", "ApplyUnifiedHmiScreenDesign", Args(), "ListUnifiedHmiScreens");
                    var screen = (ProjectScreen)widgetScreen.Object;
                    var widgetName = emit.Name == null ? device.Id : text(emit.Name, "name");
                    naming.Check("widget", widgetName);
                    if (screen.Widgets.Any(w => string.Equals(w.Name, widgetName, StringComparison.OrdinalIgnoreCase))) throw CanonicalJson.Failure("/hmi/widgets", "duplicate-key", "Duplicate widget name on a screen.");
                    var slot = emit.Slot?.ValueKind == JsonValueKind.Number ? emit.Slot.Value.GetInt32() : 0;
                    if (emit.Slot?.ValueKind != JsonValueKind.Number) while (screen.Widgets.Any(w => w.Slot == slot)) slot++;
                    if (screen.Widgets.Any(w => w.Slot == slot)) throw CanonicalJson.Failure("/hmi/widgets", "collision", "Widget slot is occupied.");
                    screen.Widgets.Add(new ProjectScreenWidget { Name = widgetName, Device = device.Id, Slot = slot, TemplateFingerprint = CanonicalJson.Hash(content) });
                    widgetScreen.Unavailable = "Widget layout/interface binding needs a complete screen/items design before Apply; every emitted widget is retained in the expected model.";
                    return;
                }
                if (entries.TryGetValue(Key(model), out var existing))
                {
                    if (ProjectModelComparison.Fingerprint(existing.Object) != ProjectModelComparison.Fingerprint(model)) throw CanonicalJson.Failure("/hmi", "duplicate-key", "Screen definitions differ.");
                    return;
                }
                var entry = Add(model, "hmi", "screen", "EnsureUnifiedHmiScreen", Args(("hmiSoftwarePath", Software(station)), ("screenName", name)), "ListUnifiedHmiScreens");
                if (emit.Kind == "hmi.screen" && content.TryGetProperty("screen", out _) && content.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    if (!content.GetProperty("screen").TryGetProperty("name", out var screenName) || screenName.GetString() != name)
                        throw CanonicalJson.Failure("/hmi/screen/name", "reference", "Screen design name must equal the generated screen name.");
                    var itemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var itemName) || itemName.ValueKind != JsonValueKind.String) throw CanonicalJson.Failure("/hmi/items/name", "required", "Every generated screen item needs an explicit name.");
                        naming.Check("screenItem", itemName.GetString()!);
                        if (!itemNames.Add(itemName.GetString()!)) throw CanonicalJson.Failure("/hmi/items/name", "duplicate-key", "Duplicate screen item name.");
                    }
                    entry.FollowUps.Add(("ApplyUnifiedHmiScreenDesign", Args(("hmiSoftwarePath", Software(station)), ("screenName", name), ("design", content))));
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
                    else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
                    else if (value.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var property in value.EnumerateObject()) { writer.WritePropertyName(property.Name); Write(property.Value, writer); } writer.WriteEndObject(); }
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
                        var candidates = entries.Values.Where(e => e.Object.Station == entry.Object.Station && e.Object.Name == name && (e.Object is ProjectType || e.Object is ProjectBlock || e.Object is ProjectTag)).ToArray();
                        if (candidates.Length == 1) entry.Dependencies.Add(candidates[0].Key);
                        else throw CanonicalJson.Failure("/sources/" + name, "reference", "Quoted SCL reference does not resolve to one expected object.");
                    }
                }
                foreach (var entry in entries.Values)
                    foreach (var dependency in entry.Dependencies)
                        if (!entries.ContainsKey(dependency)) throw CanonicalJson.Failure("/dependsOn/" + dependency, "reference", "Missing expected prerequisite.");
                if (entries.Count > 0)
                {
                    var graph = ImportDependencyPlanner.BuildGeneration(entries.Values.Select(e => new ImportOrderItem
                    { Id = e.Key, Target = e.Phase, Priority = Phases.ToList().IndexOf(e.Phase), Dependencies = e.Dependencies.OrderBy(d => d, StringComparer.Ordinal).ToArray() }).ToArray());
                    if (!graph.Valid) throw new GenerationValidationException(graph.Issues.Select(i => new GenerationValidationError("/expected/" + i.Id, "dependency", i.Message)));
                }
                var devices = expected.Devices.ToArray();
                if (devices.Where(d => d.Slot.HasValue).GroupBy(d => d.Station + "/" + d.ParentPath + "/" + d.Slot).Any(g => g.Count() > 1))
                    throw CanonicalJson.Failure("/hardware", "collision", "Duplicate hardware slots.");
                var blocks = expected.Blocks.Where(b => b.Number.HasValue).ToArray();
                if (blocks.GroupBy(b => b.Station + "/" + b.Kind + "/" + b.Number).Any(g => g.Count() > 1)) throw CanonicalJson.Failure("/blocks", "collision", "Duplicate block numbers.");
                if (expected.Alarms.GroupBy(a => a.Station + "/" + a.Number).Any(g => g.Count() > 1)) throw CanonicalJson.Failure("/alarms", "collision", "Duplicate alarm numbers.");
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
                    case ProjectScreen screen: expected.Screens.Add(screen); break; case ProjectExternalSource source: expected.ExternalSources.Add(source); break;
                }
                return entry;
            }

            private static string Key(ProjectObject model) => ProjectModelComparison.Key(model.Station, model switch
            { ProjectDevice => "device", ProjectBlock => "block", ProjectType => "type", ProjectTagTable => "tagTable", ProjectTag => "tag", ProjectGroup => "group",
                ProjectNetwork => "network", ProjectAlarm => "alarm", ProjectScreen => "screen", _ => "externalSource" }, model is ProjectGroup group ? group.Kind + "/" + model.Name : model.Name);
            private static Dictionary<string, JsonElement> Args(params (string Name, object Value)[] values) => values.ToDictionary(v => v.Name, v => JsonSerializer.SerializeToElement(v.Value), StringComparer.Ordinal);
            private static string HashText(string text) => "sha256:" + CanonicalJson.HashBytes(Encoding.UTF8.GetBytes(text));

            private GenerationPlanStepsItem Step(string key, string phase, string op, string tool, Dictionary<string, JsonElement> arguments, string readback, string? contains = null, string? fingerprint = null)
                => new GenerationPlanStepsItem { Key = key, Phase = phase, Op = op, Tool = tool, Arguments = arguments,
                    ArgumentDigest = CanonicalJson.Hash(JsonSerializer.SerializeToElement(arguments)), Availability = GenerationAvailability.Get(machine.Target.Release, tool),
                    Expect = new GenerationPlanStepsItemExpect { Readback = readback, Contains = contains, Fingerprint = fingerprint } };

            private void ProjectAndFinalActions(Dictionary<string, GenerationPlanStepsItem> steps)
            {
                if (machine.Target.Project.Mode == "new")
                {
                    naming.Check("project", machine.Target.Project.Name!);
                    var key = "project/create";
                    steps.Add(key, Step(key, "project", "create", "CreateProject", Args(("directoryPath", machine.Target.Project.Directory!), ("projectName", machine.Target.Project.Name!), ("dryRun", false), ("confirm", true)), "GetSessionState"));
                    foreach (var step in steps.Values.Where(s => s.Key != key)) step.DependsOn.Add(key);
                }
                else if (steps.Count > 0 && GenerationAvailability.Get(machine.Target.Release, "ArchiveSavedProject").Tool == "present")
                {
                    var key = "project/backup";
                    var path = options.BackupPath ?? options.ArtifactRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/backups/" + plan.PlanId + ".zap" + machine.Target.Release;
                    steps.Add(key, Step(key, "project", "execute", "ArchiveSavedProject", Args(("archivePath", path), ("dryRun", false)), "GetSessionState"));
                    foreach (var step in steps.Values.Where(s => s.Key != key)) step.DependsOn.Add(key);
                }
                if (steps.Count == 0) return;
                var prerequisites = steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var station in machine.Devices.Select(d => d.Station).Distinct().OrderBy(s => s, StringComparer.Ordinal))
                {
                    var key = station + "/compile";
                    var step = Step(key, "compile", "execute", "CompilePlcDiagnostics", Args(("softwarePath", Software(station)), ("dryRun", false), ("confirm", true)), "CompilePlcDiagnostics");
                    step.DependsOn = prerequisites.ToList(); steps.Add(key, step);
                }
                var save = Step("project/save", "save", "execute", "SaveProject", Args(("dryRun", false), ("confirm", true)), "GetSessionState");
                save.DependsOn = steps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); steps.Add(save.Key, save);
            }
        }

        internal static bool VersionMatches(string range, string version)
        {
            if (range == "*") return true;
            if (version.Split('+')[0].Contains("-")) return false;
            if (!Version.TryParse(version.Split('+')[0], out var actual)) return false;
            foreach (var term in Regex.Split(range, @"\s+", RegexOptions.CultureInvariant).Where(t => t.Length > 0))
            {
                var match = Regex.Match(term, @"\A(>=|<=|>|<|=|\^)?([0-9]+(?:\.[0-9]+){0,2})\z", RegexOptions.CultureInvariant);
                if (!match.Success) return false;
                var parts = match.Groups[2].Value.Split('.');
                if (parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number >= int.MaxValue)) return false;
                var target = new Version(int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0, parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
                var upper = target.Major > 0 ? new Version(target.Major + 1, 0, 0) : target.Minor > 0 ? new Version(0, target.Minor + 1, 0) : new Version(0, 0, target.Build + 1);
                var comparison = actual.CompareTo(target);
                if (!(match.Groups[1].Value switch { ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, "^" => comparison >= 0 && actual < upper, _ => comparison == 0 })) return false;
            }
            return true;
        }
    }
}
