using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.Generation
{
    public sealed class ProjectObjectDifference
    {
        public string Key { get; }
        public string Operation { get; }
        public string Detail { get; }
        internal ProjectObjectDifference(string key, string operation, string detail) { Key = key; Operation = operation; Detail = detail; }
    }

    public static class ProjectModelComparison
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static IReadOnlyList<ProjectObjectDifference> Compare(ProjectModel expected, ProjectModel observed)
        {
            if (!observed.Complete) throw CanonicalJson.Failure("/observed", "readback-incomplete", "Complete readback is required; absence cannot be inferred from a partial snapshot.");
            var actual = Index(observed);
            var desired = Index(expected);
            return desired.OrderBy(p => p.Key, StringComparer.Ordinal).Select(pair => Difference(pair.Key, pair.Value, actual)).ToArray();
        }

        internal static Dictionary<string, ProjectObject> Index(ProjectModel model)
        {
            var result = new Dictionary<string, ProjectObject>(StringComparer.OrdinalIgnoreCase);
            Add(model.Devices, "device"); Add(model.Blocks, "block"); Add(model.Types, "type"); Add(model.TagTables, "tagTable");
            Add(model.Tags, "tag"); Add(model.Groups, "group"); Add(model.Networks, "network"); Add(model.Alarms, "alarm"); Add(model.Screens, "screen");
            Add(model.ExternalSources, "externalSource");
            return result;
            void Add<T>(IEnumerable<T> objects, string kind) where T : ProjectObject
            {
                foreach (var item in objects)
                {
                    if (string.IsNullOrEmpty(item.Station) || string.IsNullOrEmpty(item.Name)) throw CanonicalJson.Failure("/model", "identity", "Objects require a station and name.");
                    var key = Key(item.Station, kind, item is ProjectGroup group ? group.Kind + "/" + item.Name : item.Name);
                    if (result.ContainsKey(key)) throw CanonicalJson.Failure("/model/" + key, "duplicate-key", "Duplicate project object key (case insensitive).");
                    result.Add(key, item);
                }
            }
        }

        internal static string Key(string station, string kind, string name) => Uri.EscapeDataString(station) + "/" + kind + "/" + Uri.EscapeDataString(name);
        internal static JsonElement Properties(ProjectObject item) => JsonSerializer.SerializeToElement(item, item.GetType(), Options);
        internal static string Fingerprint(ProjectObject item) => CanonicalJson.Hash(Properties(item));

        private static ProjectObjectDifference Difference(string key, ProjectObject expected, IReadOnlyDictionary<string, ProjectObject> observed)
        {
            if (!observed.TryGetValue(key, out var actual))
            {
                var collision = ResourceCollision(expected, observed);
                return collision == null ? new ProjectObjectDifference(key, "create", "missing") : new ProjectObjectDifference(key, "conflict", collision);
            }
            if (expected.GetType() != actual.GetType()) return new ProjectObjectDifference(key, "conflict", "Object kind differs.");
            var wanted = Properties(expected); var found = Properties(actual);
            var differences = wanted.EnumerateObject().Where(p => !found.TryGetProperty(p.Name, out var value)
                || !Equal(p.Name, p.Value, value)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var resource = ResourceCollision(expected, observed, key);
            if (resource != null) return new ProjectObjectDifference(key, "conflict", resource);
            return differences.Length == 0 ? new ProjectObjectDifference(key, "skip", "exists-identical")
                : new ProjectObjectDifference(key, "conflict", "Different or unverified fields: " + string.Join(", ", differences) + ".");
        }

        private static bool Equal(string field, JsonElement expected, JsonElement actual)
        {
            if (field == "address" && expected.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.String)
                return IoAddress.IsIo(actual.GetString()!) && IoAddress.Parse(expected.GetString()!).Text == IoAddress.Parse(actual.GetString()!).Text;
            if (field == "dataType" && expected.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.String)
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.OrdinalIgnoreCase);
            return CanonicalJson.Hash(expected) == CanonicalJson.Hash(actual);
        }

        private static string? ResourceCollision(ProjectObject expected, IReadOnlyDictionary<string, ProjectObject> observed, string? ownKey = null)
        {
            foreach (var pair in observed.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (string.Equals(ownKey, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var ip = expected is ProjectDevice expectedDevice ? expectedDevice.Ip : expected is ProjectNetwork expectedNetwork ? expectedNetwork.Ip : null;
                var otherIp = pair.Value is ProjectDevice actualDevice ? actualDevice.Ip : pair.Value is ProjectNetwork actualNetwork ? actualNetwork.Ip : null;
                if (ip != null && ip == otherIp && !(pair.Value is ProjectDevice ownDevice && ownDevice.Station == expected.Station && ownDevice.Name == expected.Station))
                    return "IP address is occupied by " + pair.Key + ".";
                if (expected is ProjectDevice device && pair.Value is ProjectDevice otherDevice)
                {
                    if (device.Ip != null && device.Ip == otherDevice.Ip) return "IP address is occupied by " + pair.Key + ".";
                    if (device.Slot.HasValue && device.Station == otherDevice.Station && device.ParentPath == otherDevice.ParentPath && device.Slot == otherDevice.Slot)
                        return "Hardware slot is occupied by " + pair.Key + ".";
                }
                if (expected.Station != pair.Value.Station) continue;
                if (expected is ProjectTag tag && pair.Value is ProjectTag otherTag)
                {
                    if (!IoAddress.IsIo(otherTag.Address)) continue;
                    var address = IoAddress.Parse(tag.Address); var other = IoAddress.Parse(otherTag.Address);
                    if (address.Area == other.Area && address.Start <= other.End && other.Start <= address.End) return "IO address is occupied by " + pair.Key + ".";
                }
                if (expected is ProjectBlock block && pair.Value is ProjectBlock otherBlock && block.Number.HasValue && block.Kind == otherBlock.Kind && block.Number == otherBlock.Number)
                    return "Block number is occupied by " + pair.Key + ".";
                if (expected is ProjectAlarm alarm && pair.Value is ProjectAlarm otherAlarm && alarm.Number == otherAlarm.Number)
                    return "Alarm number is occupied by " + pair.Key + ".";
            }
            return null;
        }
    }
}
