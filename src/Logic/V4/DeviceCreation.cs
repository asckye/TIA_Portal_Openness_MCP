using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public sealed class DeviceCatalogEntry
    {
        public string TypeIdentifier { get; set; } = "";
        public string ArticleNumber { get; set; } = "";
        public string Version { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Description { get; set; } = "";
        public string CatalogPath { get; set; } = "";
    }

    public sealed class DeviceInventoryItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ParentId { get; set; } = "";
        public bool IsGroup { get; set; }
    }

    public interface IDeviceCreationAdapter
    {
        PlanIdentity ReadIdentity();
        IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string typeIdentifier);
        IReadOnlyList<DeviceInventoryItem> ReadInventory();
        string RootId { get; }
        void BeforeCreate();
        DeviceInventoryItem Create(string typeIdentifier, string deviceName);
    }

    public sealed class DeviceCreationRejection : Exception
    {
        public Error Error { get; }
        public DeviceCreationRejection(Error error) : base(error.Message) { Error = error; }
    }

    // Serialized by the owning native thread. Plans and consumed attempts belong to this session only.
    public sealed class DeviceCreationSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; private set; }

        public Envelope Run(IDeviceCreationAdapter adapter, string release, string tool, string requestId,
            string typeIdentifier, string deviceName, string family, string mode = "preview", bool confirm = false,
            string expectedPlanHash = "", string expectedProjectFile = "", bool foundation = false)
        {
            bool issued = false;
            DeviceInventoryItem[]? before = null;
            PlanIdentity? identity = null;
            Plan? plan = null;
            JsonObject? data = null;
            try
            {
                if (RequiresSessionReset) Refuse("Inspect the device inventory and establish a new session.", new SessionResetRequiredDetails("device-create-unknown"));
                Text(typeIdentifier, "typeIdentifier", 512);
                Text(deviceName, "deviceName", 128);
                Text(family, "family", 64);
                if (typeIdentifier.IndexOfAny(new[] { '*', '?' }) >= 0) Invalid("typeIdentifier");
                if (deviceName == "." || deviceName == ".." || deviceName.IndexOfAny("/\\:*?\"<>|".ToCharArray()) >= 0) Invalid("deviceName");
                if (mode != "preview" && mode != "apply") Invalid("mode");
                if (mode == "apply" && !confirm) Refuse("Apply requires explicit confirmation.", new ConfirmationRequiredDetails(expectedPlanHash));
                if (mode == "apply" && (expectedPlanHash.Length != 64 || expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");
                if (mode == "apply") CanonicalProject(expectedProjectFile);
                if (foundation && release != "19") Refuse("Device creation is outside this Foundation release's capability.", new UnsupportedCapabilityDetails(release, "P6-DEVICE", "create"));

                identity = adapter.ReadIdentity();
                if (mode == "apply")
                {
                    if (CanonicalProject(identity.ProjectFile!) != CanonicalProject(expectedProjectFile)) IdentityMismatch();
                    if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash))
                        Refuse("Preview this exact operation in the current session; an attempt cannot be replayed.", new PlanStaleDetails(expectedPlanHash, "missing-or-consumed-plan"));
                    if (Hash(reviewed!.Identity) != Hash(identity)) IdentityMismatch();
                }

                DeviceCatalogEntry ObserveCatalog()
                {
                    try { return Select(adapter.ReadCatalog(typeIdentifier), typeIdentifier); }
                    catch (DeviceCreationRejection) when (mode == "apply") { Stale(expectedPlanHash); throw; }
                }
                var selected = ObserveCatalog();
                var scope = Capability(selected, family, release, foundation, tool);
                before = Inventory(adapter.ReadInventory());
                var conflicts = before.Where(i => !i.IsGroup && string.Equals(i.Name, deviceName, StringComparison.OrdinalIgnoreCase)).Select(i => i.Name).ToArray();
                plan = BuildPlan(release, tool, identity, selected, before, deviceName, family, scope);
                data = new JsonObject { ["catalogEntry"] = JsonNode.Parse(V4Json.Serialize(selected)), ["capabilityScope"] = scope.DeepClone(),
                    ["nameConflicts"] = new JsonArray(conflicts.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                    ["plan"] = JsonNode.Parse(V4Json.Serialize(plan)), ["createIssued"] = false };
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(plan.Hash)) Refuse("The session plan budget is exhausted.", new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[plan.Hash] = plan;
                    return Result(release, tool, requestId, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (conflicts.Length > 0) Refuse("A device with this name already exists.", new AlreadyExistsDetails(deviceName));
                if (plan.Hash != expectedPlanHash) Stale(expectedPlanHash);

                // A second complete observation closes the gap between review and the single write.
                if (Hash(identity) != Hash(adapter.ReadIdentity())) IdentityMismatch();
                var fresh = ObserveCatalog();
                var freshInventory = Inventory(adapter.ReadInventory());
                var freshScope = Capability(fresh, family, release, foundation, tool);
                if (BuildPlan(release, tool, identity, fresh, freshInventory, deviceName, family, freshScope).Hash != expectedPlanHash) Stale(expectedPlanHash);
                adapter.BeforeCreate();
                if (Hash(identity) != Hash(adapter.ReadIdentity())) IdentityMismatch();
                consumed.Add(expectedPlanHash);
                issued = true;
                data["createIssued"] = true;
                var created = adapter.Create(typeIdentifier, deviceName);
                if (created == null || created.IsGroup || created.Name != deviceName || created.ParentId != adapter.RootId || before.Any(i => i.Id == created.Id))
                    throw new InvalidOperationException("Created device identity is not the planned addition.");
                if (Hash(identity) != Hash(adapter.ReadIdentity())) throw new InvalidOperationException("Identity changed after Create.");
                var after = Inventory(adapter.ReadInventory());
                if (after.Length != before.Length + 1 || !after.Any(i => Hash(i) == Hash(created)) || !before.All(i => after.Any(j => Hash(i) == Hash(j))))
                    throw new InvalidOperationException("Post-create inventory did not verify exactly one addition.");
                data["created"] = JsonNode.Parse(V4Json.Serialize(created));
                data["residueCheck"] = Residue(before, after);
                return Result(release, tool, requestId, data, null, Outcome.Succeeded, Execution.Completed);
            }
            catch (Exception failure)
            {
                if (!issued)
                {
                    var error = failure is DeviceCreationRejection rejected ? rejected.Error
                        : new Error("Preflight could not establish a complete device-creation plan. No Create was issued.", new PreconditionFailedDetails("device-create-preflight", deviceName));
                    return Result(release, tool, requestId, data, error, Outcome.RejectedBeforeOperation, Execution.NotStarted);
                }
                RequiresSessionReset = true;
                var residue = new JsonObject { ["status"] = "unavailable", ["reason"] = "residue-read-failed" };
                try
                {
                    if (identity != null && Hash(identity) == Hash(adapter.ReadIdentity())) residue = Residue(before!, Inventory(adapter.ReadInventory()));
                    else residue["reason"] = "identity-changed";
                }
                catch (Exception) /* swallow(privacy): retain an explicit unavailable residue check after an unknown write */ { }
                data ??= new JsonObject();
                data["createIssued"] = true;
                data["residueCheck"] = residue;
                var evidence = new Dictionary<string, JsonElement> { ["residueCheck"] = V4Json.Data(residue)!.Value, ["createCalls"] = JsonSerializer.SerializeToElement(1) };
                return Result(release, tool, requestId, data,
                    new Error("Device creation outcome is unknown. Inspect the residue and establish a new session; do not replay.", new OutcomeUnknownDetails("create-or-readback", evidence)),
                    Outcome.Unknown, Execution.Unknown);
            }
        }

        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, tool, Meta.Correlate(id), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, BehaviorPolicy.SafeV4,
                outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Succeeded ? Completeness.Complete : Completeness.None,
                null, Array.Empty<Warning>()));

        private static Plan BuildPlan(string release, string tool, PlanIdentity identity, DeviceCatalogEntry entry, DeviceInventoryItem[] inventory,
            string name, string family, JsonObject scope)
        {
            var args = JsonSerializer.SerializeToElement(new { typeIdentifier = entry.TypeIdentifier, deviceName = name, family });
            var argumentsHash = Hash(args);
            var inventoryHash = Hash(new { catalogEntry = entry, capabilityScope = scope, inventory });
            var operations = new[] { new PlanOperation(tool, name, args) };
            var inputHashes = new Dictionary<string, string>();
            var warnings = Array.Empty<Warning>();
            var hash = Hash(new { releaseKey = release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings });
            return new Plan(hash, release, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings);
        }

        private static DeviceCatalogEntry Select(IReadOnlyList<DeviceCatalogEntry> rows, string exact)
        {
            if (rows == null || rows.Count > 1000 || rows.Any(r => r == null || string.IsNullOrEmpty(r.TypeIdentifier)))
                Refuse("Catalog observation is incomplete.", new PreconditionFailedDetails("complete-catalog", exact));
            var matched = rows!.Where(r => string.Equals(r.TypeIdentifier, exact, StringComparison.Ordinal)).ToArray();
            if (matched.Length == 0) Refuse("The exact catalog TypeIdentifier was not found.", new NotFoundDetails(exact));
            if (matched.Length != 1) Refuse("The catalog TypeIdentifier is ambiguous.", new TargetAmbiguousDetails(exact, matched.Select(r => r.TypeIdentifier).ToArray()));
            return matched[0];
        }

        private static JsonObject Capability(DeviceCatalogEntry entry, string family, string release, bool foundation, string tool)
        {
            string article = entry.ArticleNumber.Replace(" ", "");
            bool match = family == "S7-1200" ? article.StartsWith("6ES721", StringComparison.Ordinal)
                : family == "S7-1500" ? article.StartsWith("6ES751", StringComparison.Ordinal)
                : family == "HMI" || family == "WinCCUnifiedPC" ? article.StartsWith("6AV", StringComparison.Ordinal)
                : family == "GSD" ? entry.TypeIdentifier.StartsWith("GSD", StringComparison.Ordinal)
                : family == "Hardware" && !foundation;
            if (tool == "CreateGsdDevice" && family != "GSD") match = false;
            if (foundation) match &= (family == "S7-1200" && (entry.ArticleNumber == "6ES7211-1BE40-0XB0" || entry.ArticleNumber == "6ES7 211-1BE40-0XB0")
                || family == "S7-1500" && (entry.ArticleNumber == "6ES7513-1AM03-0AB0" || entry.ArticleNumber == "6ES7 513-1AM03-0AB0"))
                && entry.TypeIdentifier == "OrderNumber:" + entry.ArticleNumber + "/" + entry.Version && !string.IsNullOrEmpty(entry.Version);
            if (!match) Refuse("The exact catalog entry is outside the requested device family capability.", new UnsupportedCapabilityDetails(release, family, "create"));
            return new JsonObject { ["releaseKey"] = release, ["host"] = foundation ? "foundation" : "full-engine", ["family"] = family,
                ["placement"] = "project-root", ["selection"] = "exact-type-identifier", ["modelWhitelist"] = foundation
                    ? new JsonArray("6ES7211-1BE40-0XB0", "6ES7513-1AM03-0AB0") : new JsonArray(), ["save"] = false, ["compile"] = false, ["download"] = false };
        }

        private static DeviceInventoryItem[] Inventory(IReadOnlyList<DeviceInventoryItem> rows)
        {
            if (rows == null || rows.Count > 4096 || rows.Any(i => i == null || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(i.ParentId))
                || rows.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                Refuse("Device inventory is incomplete or ambiguous.", new PreconditionFailedDetails("complete-inventory", null));
            return rows!.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        }

        private static JsonObject Residue(DeviceInventoryItem[] before, DeviceInventoryItem[] after) => new JsonObject {
            ["status"] = "checked", ["added"] = JsonNode.Parse(V4Json.Serialize(after.Where(i => !before.Any(j => j.Id == i.Id)).ToArray())),
            ["removed"] = JsonNode.Parse(V4Json.Serialize(before.Where(i => !after.Any(j => j.Id == i.Id)).ToArray())),
            ["changed"] = JsonNode.Parse(V4Json.Serialize(after.Where(i => before.Any(j => j.Id == i.Id && Hash(j) != Hash(i))).ToArray())) };

        public static string CanonicalProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Length < 3 || (path[1] == ':' && path[2] != '\\' && path[2] != '/')) Invalid("expectedProjectFile");
            return Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        }

        public static string Hash<T>(T value)
        {
            JsonNode? Sort(JsonNode? node)
            {
                if (node is JsonObject obj) { var result = new JsonObject(); foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal)) result[pair.Key] = Sort(pair.Value); return result; }
                if (node is JsonArray array) return new JsonArray(array.Select(Sort).ToArray());
                return node?.DeepClone();
            }
            var canonical = Sort(JsonNode.Parse(V4Json.Serialize(value)))!.ToJsonString();
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }

        private static void Text(string value, string parameter, int max)
        { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value || value.Any(char.IsControl)) Invalid(parameter); }
        private static void Invalid(string parameter) => Refuse("Invalid device creation argument.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        private static void Stale(string hash) => Refuse("The catalog, inventory or arguments changed since preview.", new PlanStaleDetails(hash, "device-plan-changed"));
        private static void IdentityMismatch() => Refuse("The project or process binding changed since preview.", new IdentityMismatchDetails("project-binding", null, null));
        private static void Refuse(string message, ErrorDetails details) => throw new DeviceCreationRejection(new Error(message, details));
    }
}
