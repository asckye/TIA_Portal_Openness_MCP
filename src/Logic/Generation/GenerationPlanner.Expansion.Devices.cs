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

            private IEnumerable<MachineDescriptionDevicesItem> OrderedDevices() => machine.Devices.OrderBy(d => d.Station, StringComparer.Ordinal)
                .ThenBy(d => d.Id, StringComparer.Ordinal);
            private string Software(string station) => options.SoftwarePaths.TryGetValue(station, out var path) ? path
                : machine.Target.Project.SoftwarePath != null && machine.Stations.Count == 1 ? machine.Target.Project.SoftwarePath : station;

            private void Stations()
            {
                foreach (var station in machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    var variants = hardware.Roles.SingleOrDefault(r => r.Id == station.Role)?.Variants.Where(v => v.Releases.Contains(machine.Target.Release))
                        .ToArray()
                        ?? Array.Empty<HardwarePartRolesItemVariantsItem>();
                    if (variants.Length > 1) throw CanonicalJson
                        .Failure("/hardware/roles/" + station.Role, "ambiguous", "Multiple hardware variants select this release.");
                    var variant = variants.SingleOrDefault();
                    var existing = observed.Devices.SingleOrDefault(d => d.Station == station.Id && d.Name == station.Id);
                    var article = station.Article ?? variant?.Article ?? existing?.Article ?? "";
                    var device = new ProjectDevice {
                        Station = station.Id, Name = station.Id, Article = article, Firmware = station.Firmware ?? variant?.Firmware ?? "" };
                    var stationEntry =
                        Add(device, "hardware", "device", "CreateDevice",
                            Args(("orderNumber", article), ("version", device.Firmware), ("deviceName", device.Name), ("dryRun", false)), "GetProjectTree");
                    if (article.Length == 0) stationEntry.Unavailable = "Station " + station.Id
                        + " needs an exact catalog article or an observed device; the station kind is not a catalog identifier.";
                    foreach (var module in variant?.Modules ?? new List<HardwarePartRolesItemVariantsItemModulesItem>())
                    {
                        var parent = options.DeviceItemPaths.TryGetValue(station.Id, out var path) ? path : "";
                        var item = new ProjectDevice {
                            Station = station.Id, Name = module.Id, Article = module.Article, ParentPath = parent, Slot = module.Slot };
                        var entry =
                            Add(item, "hardware", "device", "PlugDeviceItem",
                                Args(("deviceItemPath", parent), ("orderNumber", module.Article), ("name", module.Id), ("positionNumber", module.Slot),
                                    ("dryRun", false)),
                                "GetDeviceItemTree");
                        entry.Dependencies.Add(Key(device));
                        if (parent.Length == 0) entry.Unavailable = "Module placement needs the exact parent device-item path for " + station.Id + ".";
                    }
                    if (station.Ip != null || station.ProfinetName != null)
                    {
                        var path = options.DeviceItemPaths.TryGetValue(station.Id, out var itemPath) ? itemPath : "";
                        var network = new ProjectNetwork { Station = station.Id, Name = station.Id + "_Network", DeviceItemPath = path, InterfaceIndex = 0,
                            SubnetType = "IndustrialEthernet", Ip = station.Ip == null ? null : allocator.Ip(station.Id), ProfinetName = station.ProfinetName };
                        var entry =
                            Add(network, "network", "network", "EnsureSubnet",
                                Args(("anchorDeviceItemPath", path), ("subnetName", network.Name), ("subnetType", network.SubnetType), ("dryRun", false)),
                                "GetDeviceItemNetworkInfo");
                        entry.Unavailable =
                            "IP/PROFINET assignment needs exact writable attribute names from readback; EnsureSubnet alone does not set node addresses.";
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
                        var contexts = group.Path.Contains("unit.") ? topology.Values.Select(t => t.Unit).Distinct().OrderBy(u => u.Id, StringComparer.Ordinal)
                            .Select(u => Context(null, u, station)).ToArray()
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
                        entry.Unavailable = "Organization block " + name + " requires a package SCL implementation with event " + block.Event
                            + "; a name/number does not define its interface.";
                    }
                }
            }

            private Entry Group(string station, string kind, string path)
            {
                if (kind != "blocks" && kind != "types")
                {
                    var other = new ProjectGroup { Station = station, Name = path, Kind = kind };
                    if (entries.TryGetValue(Key(other), out var existing)) return existing;
                    foreach (var segment in path.Split('/')) naming
                        .Check(kind == "tags" ? "tagGroup" : kind == "devices" ? "deviceGroup" : "hmiGroup", segment);
                    var entry =
                        Add(other, "plcStructure", null, kind == "devices" ? "ManageDeviceUserGroup" : kind == "tags" ? "ManagePlcUserGroup"
                            : "ManageUnifiedEngineeringObject", Args(), "GetProjectTree");
                    entry.Unavailable = "Group kind " + kind + " needs exact tool category/parent bindings before Apply.";
                    return entry;
                }
                var parts = path.Split('/');
                if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) throw CanonicalJson
                    .Failure("/structure/groups", "path", "Group path contains an empty or traversal segment.");
                Entry? parent = null;
                for (var i = 0; i < parts.Length; i++)
                {
                    naming.Check(kind == "blocks" ? "blockGroup" : "typeGroup", parts[i]);
                    var group = new ProjectGroup { Station = station, Name = string.Join("/", parts.Take(i + 1)), Kind = kind };
                    var key = Key(group);
                    if (!entries.TryGetValue(key, out var entry)) entry =
                        Add(group, "plcStructure", null, kind == "blocks" ? "CreatePlcBlockGroup" : "CreatePlcTypeGroup",
                        Args(("softwarePath", Software(station)), ("groupPath", group.Name), ("dryRun", false)), "GetProjectTree");
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
                        string Text(string? value, string field) => value == null ? throw CanonicalJson
                            .Failure("/emit/" + field, "required", "Emit " + emit.Kind + " requires " + field + ".") : naming.Expand(value, context, BuiltIn);
                        JsonElement BuiltIn(string function, IReadOnlyList<JsonElement> args)
                        {
                            var active = GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(context["device"]);
                            switch (function)
                            {
                                case "alloc.io": return JsonSerializer.SerializeToElement(allocator.Io(active.Station, active.Id, SignalRole(args[0])));
                                case "alloc.ip": return JsonSerializer
                                    .SerializeToElement(allocator
                                        .Ip(args[0].ValueKind == JsonValueKind.Object ? args[0].GetProperty("id").GetString()! : GenerationNamingEngine
                                            .Value(args[0])));
                                case "seq": return JsonSerializer
                                    .SerializeToElement(allocator
                                        .Number(args.Count == 0 ? "sequence" : GenerationNamingEngine.Value(args[0]), active.Station + "/" + active.Id + "/"
                                            + (emit.Id ?? emit.Kind + ":" + rule.Emit.IndexOf(emit).ToString(CultureInfo.InvariantCulture)) + "/"
                                            + (context.TryGetValue("item", out var item) ? CanonicalJson.Hash(item) : "device")));
                                case "tag":
                                    var key = GenerationAllocator.SignalKey(active, SignalRole(args[0]));
                                    if (!tags.TryGetValue(key, out var name)) throw CanonicalJson
                                        .Failure("/tag/" + key, "reference", "Signal has no emitted PLC tag.");
                                    return JsonSerializer.SerializeToElement(name);
                                default: throw CanonicalJson.Failure("/placeholder", "function", "Unknown built-in function.");
                            }
                        }
                        var activeDevice = GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(context["device"]);
                        var station = context["station"].GetProperty("id").GetString()!;
                        var extra = (emit.Arguments ?? new Dictionary<string, JsonElement>()).ToDictionary(p => p.Key,
                            p => p.Value.ValueKind == JsonValueKind.String ? JsonSerializer
                                .SerializeToElement(naming.Expand(p.Value.GetString()!, context, BuiltIn)) : p.Value, StringComparer.Ordinal);
                        switch (emit.Kind)
                        {
                            case "plc.tag":
                                var signal = context["signal"];
                                var role = signal.GetProperty("role").GetString()!;
                                var dataType = emit.Type == null ? signal.GetProperty("type").GetString()! : Text(emit.Type, "type");
                                var tableName = Text(emit.Table, "table");
                                var table = new ProjectTagTable { Station = station, Name = tableName };
                                if (!entries.ContainsKey(Key(table)))
                                    Add(table, "plcTags", "tagTable", "CreatePlcTagTable",
                                        Args(("plc", Software(station)), ("group", ""), ("name", tableName), ("dryRun", false), ("confirm", true)),
                                        "ListPlcTagTables");
                                var address = IoAddress.Parse(Text(emit.Address, "address"));
                                if (address.Text != allocator.Io(station, activeDevice.Id, role) || address.Width != GenerationAllocator.Width(dataType))
                                    throw CanonicalJson.Failure("/emit/address", "allocation", "Tag address/type must match the allocated signal.");
                                var tag = new ProjectTag {
                                    Station = station, Name = Text(emit.Name, "name"), Table = tableName, DataType = dataType, Address = address.Text };
                                var tagEntry =
                                    Add(tag, "plcTags", "tag", "CreatePlcTag",
                                        Args(("plc", Software(station)), ("table", tableName), ("name", tag.Name), ("dataType", dataType),
                                            ("address", address.Text), ("dryRun", false), ("confirm", true)),
                                        "ListPlcTags");
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
                                    var instance = new ProjectBlock {
                                        Station = station, Name = instanceName, Kind = "DB", InstanceType = selected.Entry.Object.Name, Group =
                                            Text(emit.Group ?? "", "group"),
                                        InterfaceFingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToElement(definition.Interface)), CodeFingerprint =
                                            HashText(GenerationScl.Instance(instanceName, selected.Entry.Object.Name)),
                                        Members = ((ProjectBlock)selected.Entry.Object).Members.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                                        };
                                    var entry = Source(instance, "plcProgram", "instanceDb", GenerationScl.Instance(instanceName, selected.Entry.Object.Name));
                                    entry.Dependencies.Add(selected.Entry.Key);
                                }
                                if (emit.CallIn != null)
                                {
                                    var target = Text(emit.CallIn, "callIn");
                                    var key = station + "/" + target;
                                    if (!calls.TryGetValue(key, out var list)) calls.Add(key, list = new List<SclCall>());
                                    var bindings = (emit.Bind ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
                                        .ToDictionary(p => p.Key, p => Text(p.Value, "bind"), StringComparer.Ordinal);
                                    var members = (definition.Interface.In ?? new List<InterfaceMember>())
                                        .Concat(definition.Interface.Out ?? new List<InterfaceMember>())
                                        .Concat(definition.Interface.InOut ?? new List<InterfaceMember>()).ToArray();
                                    if (bindings.Keys.Any(roleKey => !members.Any(m => m.Role == roleKey))) throw CanonicalJson
                                        .Failure("/bind", "reference", "Binding role is absent from the selected type interface.");
                                    foreach (var member in members)
                                        if (!bindings.ContainsKey(member.Role)
                                                && !(member.Role.StartsWith("signal.", StringComparison.Ordinal) && !activeDevice.Io
                                                    .ContainsKey(member.Role.Substring(7)) && rule.Signals
                                                    .Any(s => s.Role == member.Role.Substring(7) && s.Optional == true)))
                                            throw CanonicalJson.Failure("/bind/" + member.Role, "reference", "Required interface binding is missing.");
                                    foreach (var member in (definition.Interface.Out ?? new List<InterfaceMember>())
                                            .Concat(definition.Interface.InOut ?? new List<InterfaceMember>()))
                                        if (bindings.TryGetValue(member.Role, out var targetBinding) && GenerationScl.BindingRoot(targetBinding).Length == 0
                                                && !IoAddress.IsIo(targetBinding))
                                            throw CanonicalJson
                                                .Failure("/bind/" + member.Role, "scl-binding", "OUT and IN_OUT require a writable symbolic/IO binding.");
                                    if (list.Any(c => c.Instance == instanceName)) throw CanonicalJson
                                        .Failure("/calls", "duplicate-key", "A call block contains a duplicate instance call.");
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
                                var network = new ProjectNetwork {
                                    Station = station, Name = Text(emit.Name, "name"), DeviceItemPath = extra.TryGetValue("deviceItemPath", out var devicePath)
                                        ? devicePath.GetString()! : "",
                                    InterfaceIndex = extra.TryGetValue("interfaceIndex", out var index) ? index.GetInt32() : 0, SubnetType =
                                        "IndustrialEthernet" };
                                var networkEntry =
                                    Add(network, "network", "network", "EnsureSubnet",
                                        Args(("anchorDeviceItemPath", network.DeviceItemPath), ("subnetName", network.Name),
                                            ("subnetType", network.SubnetType), ("dryRun", false)),
                                        "GetDeviceItemNetworkInfo");
                                networkEntry.FollowUps
                                    .Add(("AttachDeviceNodeToSubnet",
                                            Args(("deviceItemPath", network.DeviceItemPath), ("interfaceIndex", network.InterfaceIndex),
                                                ("subnetName", network.Name), ("dryRun", false))));
                                if (network.DeviceItemPath.Length == 0 || !extra.ContainsKey("interfaceIndex")) networkEntry.Unavailable =
                                    "Network emit requires an exact device-item path and interface index from readback.";
                                break;
                            case "hardware.device":
                            case "hardware.module":
                                var hardwareDevice = new ProjectDevice {
                                    Station = station, Name = Text(emit.Name, "name"), Article = extra.TryGetValue("orderNumber", out var article) ? article
                                        .GetString()! : "",
                                    Firmware = extra.TryGetValue("version", out var version) ? version.GetString()! : "", ParentPath = extra
                                        .TryGetValue("deviceItemPath", out var parent) ? parent.GetString()! : null,
                                    Slot = emit.Slot?.ValueKind == JsonValueKind.Number ? emit.Slot.Value.GetInt32() : (int?)null };
                                var hardwareEntry =
                                    Add(hardwareDevice, "hardware", "device", emit.Kind == "hardware.device" ? "CreateDevice" : "PlugDeviceItem",
                                    emit.Kind == "hardware.device"
                                        ? Args(("orderNumber", hardwareDevice.Article), ("version", hardwareDevice.Firmware),
                                            ("deviceName", hardwareDevice.Name), ("dryRun", false))
                                        : Args(("deviceItemPath", hardwareDevice.ParentPath ?? ""), ("orderNumber", hardwareDevice.Article),
                                                ("version", hardwareDevice.Firmware), ("name", hardwareDevice.Name),
                                                ("positionNumber", hardwareDevice.Slot ?? -1), ("dryRun", false)),
                                            "GetProjectTree");
                                if (hardwareDevice.Article.Length == 0 || emit.Kind == "hardware.module"
                                        && (!hardwareDevice.Slot.HasValue || string.IsNullOrEmpty(hardwareDevice.ParentPath)))
                                    hardwareEntry.Unavailable =
                                    "Hardware emit requires an exact article, parent path and explicit slot; automatic slot selection is not planned.";
                                break;
                        }
                    }
                }
            }

            private IReadOnlyDictionary<string,
                JsonElement> Context(MachineDescriptionDevicesItem? device, MachineDescriptionTopology? unit, MachineDescriptionStationsItem? station)
            {
                var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal) {
                    ["machine"] = JsonSerializer.SerializeToElement(machine.Machine, JsonOptions), ["options"] = JsonSerializer
                        .SerializeToElement(machine.Options, JsonOptions)
                };
                if (device != null)
                {
                    result["device"] = JsonSerializer.SerializeToElement(device, JsonOptions); unit ??= topology[device.Parent].Unit;
                    result["parent"] = JsonSerializer.SerializeToElement(topology[device.Parent].Node, JsonOptions);
                    station ??= machine.Stations.Single(s => s.Id == device.Station);
                    var signals = rules[device.Type].Signals.Where(s => s.Optional != true || device.Io.ContainsKey(s.Role))
                        .ToDictionary(s => s.Role, s => Signal(s), StringComparer.Ordinal);
                    result["signals"] = JsonSerializer.SerializeToElement(signals); result["signal"] = result["signals"];
                }
                if (station != null) result["station"] = JsonSerializer.SerializeToElement(station, JsonOptions);
                if (unit != null)
                {
                    var unitFields = JsonSerializer.SerializeToElement(unit, JsonOptions).EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    // unit.<namingRule> aliases are explicit rule ids, evaluated lazily by the author via naming.* where allocation is needed.
                    foreach (var rule in namingPart.Rules.Where(r => r.Id == "tagTable" || r.Id == "callBlock" || r.Id == "hmiDb"))
                    {
                        var baseContext = new Dictionary<string, JsonElement>(result, StringComparer.Ordinal) {
                            ["unit"] = JsonSerializer.SerializeToElement(unit, JsonOptions) };
                        unitFields[rule.Id] = JsonSerializer.SerializeToElement(naming.Expand(rule.Template, baseContext));
                    }
                    result["unit"] = JsonSerializer.SerializeToElement(unitFields);
                }
                return result;
            }
            private static JsonElement Signal(DeviceTypeRuleSignalsItem signal) => JsonSerializer
                .SerializeToElement(new { role = signal.Role, dir = signal.Dir, type = signal.Type ?? GenerationAllocator.DefaultType(signal.Dir) });
            private static string SignalRole(JsonElement signal) => signal.ValueKind == JsonValueKind.Object ? signal.GetProperty("role").GetString()!
                : GenerationNamingEngine.Value(signal).Replace("signal.", "");

            private IEnumerable<IReadOnlyDictionary<string,
                JsonElement>> Iterations(MachineDescriptionDevicesItem device, DeviceTypeRule rule, string? collection)
            {
                var context = Context(device, null, null);
                if (collection == null) { yield return context; yield break; }
                IEnumerable<(string Root, JsonElement Item)> items = collection switch
                {
                    "signals" => rule.Signals.Where(s => s.Optional != true || device.Io.ContainsKey(s.Role)).OrderBy(s => s.Role, StringComparer.Ordinal)
                        .Select(s => ("signal", Signal(s))),
                    "devices" => OrderedDevices().Select(d => ("device", JsonSerializer.SerializeToElement(d, JsonOptions))),
                    "stations" => machine.Stations.OrderBy(s => s.Id, StringComparer.Ordinal)
                        .Select(s => ("station", JsonSerializer.SerializeToElement(s, JsonOptions))),
                    "topology" => topology.Values.Select(t => t.Node).OrderBy(n => n.Id, StringComparer.Ordinal)
                        .Select(n => ("unit", JsonSerializer.SerializeToElement(n, JsonOptions))),
                    "children" => (topology[device.Parent].Node.Children ?? new List<MachineDescriptionTopology>()).OrderBy(n => n.Id, StringComparer.Ordinal)
                        .Select(n => ("child", JsonSerializer.SerializeToElement(n, JsonOptions))),
                    _ => throw CanonicalJson.Failure("/forEach", "collection", "Unsupported collection.")
                };
                foreach (var item in items)
                {
                    var nested = item.Root == "device" ? Context(GenerationDocuments.Deserialize<MachineDescriptionDevicesItem>(item.Item), null, null)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                        : context.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    nested[item.Root] = item.Item; nested["item"] = item.Item;
                    yield return nested;
                }
            }

            private static bool Condition(JsonElement condition, IReadOnlyDictionary<string, JsonElement> context)
            {
                if (condition.ValueKind == JsonValueKind.String) return GenerationNamingEngine.TryResolve(context, condition.GetString()!, out var value)
                    && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.False;
                if (condition.TryGetProperty("exists", out var exists)) return GenerationNamingEngine.TryResolve(context, exists.GetString()!, out var value)
                    && value.ValueKind != JsonValueKind.Null;
                var equals = condition.TryGetProperty("equals", out var predicate);
                if (!equals) predicate = condition.GetProperty("in");
                if (!GenerationNamingEngine.TryResolve(context, predicate.GetProperty("path").GetString()!, out var actual)) return false;
                return equals ? CanonicalJson.Hash(actual) == CanonicalJson.Hash(predicate.GetProperty("value")) : predicate.GetProperty("values")
                    .EnumerateArray().Any(v => CanonicalJson.Hash(v) == CanonicalJson.Hash(actual));
            }
        }
    }
}
