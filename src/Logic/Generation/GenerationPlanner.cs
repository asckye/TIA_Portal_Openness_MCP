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
        public static IReadOnlyList<string> Phases { get; } = Array.AsReadOnly(new[] { "project", "hardware", "network", "plcStructure", "plcTypes",
            "plcLibrary", "plcProgram", "plcTags", "alarms", "hmi", "compile", "cleanup", "save", "record" });

        public static GenerationPlanningResult Build(StandardPackage package, string machineJson, ProjectModel? observed, GenerationPlanningOptions options,
            IEnumerable<StandardPackage>? inheritedPackages = null)
            => new Expansion(EffectiveGenerationPackage.Resolve(package, inheritedPackages ?? Array.Empty<StandardPackage>()), machineJson, observed, options)
                .Build();

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

        private sealed partial class Expansion
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
            private readonly Dictionary<string, (MachineDescriptionTopology Node, MachineDescriptionTopology Unit)> topology = new Dictionary<string,
                (MachineDescriptionTopology, MachineDescriptionTopology)>(StringComparer.Ordinal);
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, (LibraryPartTypesItem Definition, Entry Entry)> types = new Dictionary<string,
                (LibraryPartTypesItem, Entry)>(StringComparer.Ordinal);
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
                if (manifest.Extends != null) throw CanonicalJson
                    .Failure("/extends", "effective-package", "Resolve and validate the effective inherited package before planning.");
                machine = package.ValidateMachine(machineJson);
                if (!VersionMatches(machine.Standard.Version, manifest.Version)) throw CanonicalJson
                    .Failure("/standard/version", "version", "Selected package does not satisfy the machine version range.");
                if (options.MaximumObjects < 1 || options.MaximumObjects > 10000) throw CanonicalJson
                    .Failure("/options/maximumObjects", "limit", "Object limit must be 1..10000.");
                if (string.IsNullOrEmpty(options.ArtifactRoot) || !Regex
                        .IsMatch(options.ArtifactRoot, @"\A(?:[A-Za-z]:[/\\]|/)", RegexOptions.CultureInvariant))
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
                rules = (manifest.Parts.Rules ?? new List<string>()).Select(package.GetPart<DeviceTypeRule>)
                    .ToDictionary(r => r.DeviceType, StringComparer.Ordinal);
                allocator.Prepare(machine, rules);
                Walk(machine.Topology, null);
                var machineHash = GenerationDocuments.MachineHash(machineJson);
                var seed = CanonicalJson.Hash(JsonSerializer.SerializeToElement(new { package = package.ContentHash, machine = machineHash,
                    observed = ProjectModelComparison.Index(this.observed).OrderBy(p => p.Key, StringComparer.Ordinal)
                        .ToDictionary(p => p.Key, p => ProjectModelComparison.Properties(p.Value), StringComparer.Ordinal),
                    identity = this.observed.ProjectIdentity, options }));
                plan = new GenerationPlan { Schema = "tiamcp.plan/1", PlanId = "gen-" + seed.Substring(7), PlanHash = new string('0', 64),
                    Package = new GenerationPlanPackage { Id = manifest.Id, Version = manifest.Version, Hash = package.ContentHash }, MachineHash = machineHash,
                    Target = new GenerationPlanTarget { Release = machine.Target.Release, Mode = machine.Target.Project.Mode,
                        ProjectIdentity = machine.Target.Project.Mode == "new" ? GenerationProjectPath
                            .CreationFile(machine.Target.Release, machine.Target.Project.Directory!, machine.Target.Project.Name!) : machine.Target.Project
                            .ProjectIdentity!,
                        SoftwarePath = machine.Target.Project.SoftwarePath }, Phases = Phases.ToList() };
                expected.ProjectIdentity = plan.Target.ProjectIdentity;
                foreach (var station in machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal))
                    plan.Observed[Software(station.Id)] = new GenerationPlanObserved {
                        SourceNames = this.observed.ExternalSources.Where(s => s.Station == station.Id).Select(s => s.Name)
                            .OrderBy(s => s, StringComparer.Ordinal).ToList(),
                        ObjectKeys = this.observed.Blocks.Where(b => b.Station == station.Id).Select(b => "block:" + ObjectPath(b.Group, b.Name))
                            .Concat(this.observed.Types.Where(t => t.Station == station.Id).Select(t => "type:" + ObjectPath(t.Group, t.Name)))
                                .OrderBy(s => s, StringComparer.Ordinal).ToList() };

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
                var differences = ProjectModelComparison.Compare(expected, observed, options).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
                plan.Objects = ProjectModelComparison.Describe(expected);
                foreach (var item in plan.Objects)
                {
                    var entry = entries[item.Key];
                    var source = entry.Object is ProjectExternalSource ? entry : entry.Dependencies.Select(d => entries[d])
                        .FirstOrDefault(e => e.Object is ProjectExternalSource);
                    if (source != null && source.Arguments.TryGetValue("filePath", out var file))
                    {
                        var artifact = artifacts.Single(a => file.GetString()!.EndsWith("/" + a.Path, StringComparison.Ordinal));
                        item.Artifact = new FileDigest { Path = artifact.Path, Sha256 = artifact.Sha256 };
                    }
                }
                plan.Adopt = options.Adopt.Select(k => plan.Objects.Single(o => string.Equals(o.Key, k, StringComparison.OrdinalIgnoreCase)).Key)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var source in entries.Values.Where(e => e.Object is ProjectExternalSource))
                {
                    var consumers = entries.Values.Where(e => e.Tool == "GenerateBlocksFromExternalSource" && e.Dependencies.Contains(source.Key)).ToArray();
                    if (consumers.Length > 0 && consumers.All(e => differences[e.Key].Operation != "create") && differences[source.Key]
                            .Detail != "exists-identical")
                        differences[source.Key] = new ProjectObjectDifference(source.Key, "skip", "generation-satisfied");
                    else if (consumers.Any(e => differences[e.Key].Operation == "create") && differences[source.Key].Detail is "owned-unchanged" or "adopted")
                    {
                        var previous = options.Ownership.Single(o => string.Equals(o.Key, source.Key, StringComparison.OrdinalIgnoreCase));
                        var item = plan.Objects.Single(o => o.Key == source.Key);
                        item.State = differences[source.Key].Detail == "adopted" ? "adopted" : previous.State;
                        item.Witness = options.Witnesses.TryGetValue(source.Key, out var witness) ? witness : previous.Witness; item.CreatedBy = previous
                            .CreatedBy;
                        differences[source.Key] = new ProjectObjectDifference(source.Key, "create", "replace-owned-source");
                    }
                }
                foreach (var difference in differences.Values.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    if (difference.Operation == "skip") plan.Skipped.Add(new GenerationPlanSkippedItem { Key = difference.Key, Reason = difference.Detail });
                    if (difference.Operation == "conflict") plan.Conflicts.Add(new GenerationPlanConflictsItem { Key = difference.Key,
                        Reason = new[] { "unknown-outcome", "modified-after-generation", "regeneration-required" }.Contains(difference.Detail) ? difference
                            .Detail : "exists-different", Detail = difference.Detail });
                }
                var unavailable = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (var entry in entries.Values.Where(e => differences[e.Key].Operation == "create"))
                {
                    if (entry.Unavailable != null) Unavailable(entry.Phase, entry.Unavailable);
                    var availability = GenerationAvailability.Get(machine.Target.Release, entry.Tool);
                    if (availability.Tool == "absent") Unavailable(entry.Phase, "Release " + machine.Target.Release + " has no " + entry.Tool + " tool.");
                    foreach (var followUp in entry.FollowUps)
                        if (GenerationAvailability.Get(machine.Target.Release, followUp.Tool).Tool == "absent")
                            Unavailable(entry.Phase, "Release " + machine.Target.Release + " has no " + followUp.Tool + " tool.");
                }
                bool changed;
                do
                {
                    changed = false;
                    foreach (var entry in entries.Values.Where(e => differences[e.Key].Operation == "create" && !unavailable.ContainsKey(e.Phase)))
                        foreach (var dependency in entry.Dependencies)
                            if (differences[dependency].Operation == "conflict" || differences[dependency].Operation == "create" && unavailable
                                    .ContainsKey(entries[dependency].Phase))
                            { Unavailable(entry.Phase, "Prerequisite " + dependency + " is conflicting or unavailable."); changed = true; break; }
                } while (changed);
                var selected = entries.Values.Where(e => differences[e.Key].Operation == "create" && !unavailable.ContainsKey(e.Phase)).ToArray();
                var steps = selected
                    .ToDictionary(e => e.Key, e =>
                        Step(e.Key, e.Phase, "create", e.Tool, e.Arguments, e.Readback, e.Object.Name, ProjectModelComparison.Fingerprint(e.Object)),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var entry in selected)
                    steps[entry.Key].DependsOn = entry.Dependencies.Where(steps.ContainsKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var entry in selected)
                {
                    var previous = entry.Key;
                    foreach (var followUp in entry.FollowUps)
                    {
                        var key = entry.Key + "/" + followUp.Tool;
                        var step =
                            Step(key, entry.Phase, "execute", followUp.Tool, followUp.Arguments, entry.Readback, entry.Object.Name, ProjectModelComparison
                                .Fingerprint(entry.Object));
                        step.DependsOn.Add(previous); steps.Add(key, step); previous = key;
                    }
                    if (previous != entry.Key)
                        foreach (var step in steps.Values.Where(s => s.Key != entry.Key && s.Key != previous && s.DependsOn.Contains(entry.Key)))
                        { step.DependsOn.Remove(entry.Key); step.DependsOn.Add(previous); }
                }
                foreach (var source in selected.Where(e => e.Object is ProjectExternalSource && differences[e.Key].Detail == "replace-owned-source"))
                {
                    string key = source.Key + "/replace";
                    var replace = Step(key, source.Phase, "execute", "DeletePlcExternalSource", Args(("softwarePath", source.Arguments["softwarePath"]),
                        ("groupPath", ""), ("externalSourceName", source.Object.Name), ("dryRun", false), ("confirm", true)), "ListPlcExternalSources");
                    replace.DependsOn = steps[source.Key].DependsOn.ToList(); steps.Add(key, replace); steps[source.Key].DependsOn.Add(key);
                }
                ProjectAndFinalActions(steps);
                if (steps.Count > 10000) throw CanonicalJson.Failure("/steps", "limit", "Plan exceeds 10000 steps.");
                if (steps.Count != 0)
                {
                    var order = ImportDependencyPlanner.BuildGeneration(steps.Values.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new ImportOrderItem
                    { Id = s.Key, Target = s.Phase, Priority = Phases.ToList().IndexOf(s.Phase), Dependencies = s.DependsOn.ToArray() }).ToArray());
                    if (!order.Valid)
                        throw new GenerationValidationException(order.Issues
                            .Select(i => new GenerationValidationError("/steps/" + i.Id, "dependency", i.Message)));
                    var ids = order.Order.Select((key, index) => new { key, id = "s" + (index + 1).ToString("D5", CultureInfo.InvariantCulture) })
                        .ToDictionary(p => p.key, p => p.id, StringComparer.OrdinalIgnoreCase);
                    foreach (var key in order.Order)
                    {
                        var step = steps[key]; step.Id = ids[key]; step.DependsOn = step.DependsOn.Select(d => ids[d]).OrderBy(d => d, StringComparer.Ordinal)
                            .ToList(); plan.Steps.Add(step);
                    }
                }
                plan.Unavailable = Phases.Where(unavailable.ContainsKey)
                    .Select(phase => new GenerationPlanUnavailableItem { Phase = phase, Reason = string.Join(" ", unavailable[phase]) }).ToList();
                var orderedArtifacts = artifacts.OrderBy(a => a.Path, StringComparer.Ordinal).ToArray();
                plan.Artifacts = orderedArtifacts.Select(a => new FileDigest { Path = a.Path, Sha256 = a.Sha256 }).ToList();
                Reviews();
                plan.PlanHash = GenerationDocuments.PlanHash(GenerationDocuments.Canonical(plan));
                return new GenerationPlanningResult(plan, expected, Array.AsReadOnly(orderedArtifacts), options.ArtifactRoot);

                void Unavailable(string phase, string reason)
                {
                    if (!unavailable.TryGetValue(phase, out var reasons)) unavailable.Add(phase, reasons = new SortedSet<string>(StringComparer.Ordinal));
                    reasons.Add(reason);
                }
            }

            private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
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
                var target =
                    new Version(int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0,
                        parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
                var upper = target.Major > 0 ? new Version(target.Major + 1, 0, 0) : target.Minor > 0 ? new Version(0, target.Minor + 1, 0)
                    : new Version(0, 0, target.Build + 1);
                var comparison = actual.CompareTo(target);
                if (!(match.Groups[1].Value switch {
                            ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, "^" => comparison >= 0
                                && actual < upper, _ => comparison == 0
                        }))
                    return false;
            }
            return true;
        }
    }
}
