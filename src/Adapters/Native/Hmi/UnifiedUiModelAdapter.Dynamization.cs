using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Engineering;
using System;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed partial class UnifiedUiModelAdapter
    {
        public HmiMessage ManageUnifiedDynamization(string softwarePath, string objectPathJson, string propertyName, string action = "read",
                string dynamizationKind = "", string propertiesJson = "{}", string mappingEntriesJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedDynamization", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                if ((action == "read" || action == "delete") && (changes.Count != 0 || mappingEntriesJson.Trim() != "[]"))
                    throw new ArgumentException("Properties and mapping entries are only accepted by create/update.");
                bool write = action != "read" && !dryRun; using var access = write ? _session.AcquireHmiEditAccess() : null;
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var dynamizations = PlcGroupOperations.Get(target, "Dynamizations");
                var existing = UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName);
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["propertyName"] = propertyName; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["exists"] = existing != null;
                meta["supportedKinds"] = new HmiArray(UnifiedUiModelLogic.DynamizationKinds.Keys.Select(k => (object)HmiValues.Value(k)!).ToArray());
                if (existing != null) {
                    meta["before"] = UnifiedUiModelLogic.Tree(existing, 4); meta["dynamizationKind"] = UnifiedUiModelLogic.KindOf(existing); }
                if (action == "read") {
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = existing == null || meta["before"]!.Field("dataComplete")!.GetValue<bool>();
                    return existing == null ? "No dynamization on this property."
                        : "Dynamization read including value converter, mapping table entries and trigger where present.";
                }
                if ((action == "create") == (existing != null))
                    throw new PlcSoftwareException(action == "create" ? "AlreadyExists" : "NotFound", action == "create"
                            ? "Dynamization exists; use update or delete." : "Exact dynamization not found.");
                if (action == "update" && dynamizationKind != "" && dynamizationKind != UnifiedUiModelLogic.KindOf(existing!))
                    throw new ArgumentException("dynamizationKind differs from the existing " + UnifiedUiModelLogic.KindOf(existing!) + " dynamization.");
                var type = action == "create" ? UnifiedUiModelLogic.DynamizationType(dynamizations, dynamizationKind) : existing!.GetType();
                var create = action == "create" ? UnifiedUiModelLogic.GenericCreate(dynamizations, type, 1) : null;
                var prepared = UnifiedUiModelLogic.PrepareNested(type, changes);
                var entryPath = UnifiedUiModelLogic.MappingTablePath(type);
                var entries = UnifiedUiModelLogic
                    .ParseMappingEntries(mappingEntriesJson, n => dynamizations.GetType().Assembly.GetType(n, false)
                        ?? throw new NotSupportedException("Native mapping entry type unavailable: " + n));
                if (entries.Count != 0 && entryPath.Length == 0)
                    throw new NotSupportedException("Mapping entries only apply to Tag/Expression dynamizations with ValueConverter.MappingTable.");
                if (action == "delete" && type.GetMethod("Delete", Type.EmptyTypes) == null)
                    throw new NotSupportedException("Native Delete() unavailable on " + type.FullName);
                if (action == "update" && prepared.Count == 0 && entries.Count == 0)
                    throw new ArgumentException("update requires propertiesJson and/or mappingEntriesJson.");
                meta["dynamizationKind"] = UnifiedUiModelLogic.KindOf(type); meta["requestedProperties"] = changes.DeepClone(); meta["requestedEntryCount"] =
                    entries.Count;
                if (dryRun) return "Dynamization " + action + " preview; native Create<T>/setter shape and conversions checked, no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true.");
                int countBefore = action == "create" ? PlcGroupOperations.Items(dynamizations).Count() : 0;
                if (action == "delete")
                {
                    meta["mayHaveChanged"] = true;
                    PlcGroupOperations.Call(existing!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                    if (UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName) != null)
                        throw new InvalidOperationException("Delete returned but the dynamization remains.");
                    meta["exists"] = false; meta["verifiedAbsent"] = true;
                    return "Dynamization deleted and absence verified. No save/compile/download.";
                }
                var dyn = existing ?? EngineeringWriteJournal.ProbeCreate(meta, propertyName,
                    () => PlcGroupOperations.Items(FreshDynamizations()).Select(item => PlcGroupOperations.Get(item, "PropertyName")).ToArray(),
                    () => create!.Invoke(dynamizations, new object[] { propertyName }));
                meta["apiCallSuccess"] = true;
                UnifiedUiModelLogic.ApplyNested(dyn, prepared, meta);
                if (entries.Count != 0)
                {
                    object owner = dyn;
                    foreach (var step in entryPath) owner = PlcGroupOperations.Get(owner, step);
                    var added = new HmiArray(); meta["appliedEntries"] = added;
                    foreach (var entry in entries)
                    {
                        var createEntry = UnifiedUiModelLogic.GenericCreate(owner, entry.Type, 0);
                        var created = EngineeringWriteJournal.ProbeCreate(meta, propertyName + "/" + entry.Kind,
                            () => new object?[] { PlcGroupOperations.Items(owner).Count() }, () => createEntry.Invoke(owner, Array.Empty<object>()));
                        var entryMeta = new HmiObject(); UnifiedUiModelLogic
                            .ApplyNested(created, UnifiedUiModelLogic.PrepareNested(entry.Type, entry.Properties), entryMeta);
                        added.Add(new HmiObject { ["kind"] = entry.Kind, ["applied"] = entryMeta["appliedProperties"]?.DeepClone() });
                    }
                }
                object FreshDynamizations() => PlcGroupOperations.Get(UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath),
                    objectPathJson), "Dynamizations");
                if (action == "create") UnifiedUiModelLogic.VerifyCreated(FreshDynamizations, dyn, null, countBefore);
                var after = UnifiedUiModelLogic.FindDynamization(FreshDynamizations(),
                    propertyName) ?? throw new InvalidOperationException("Dynamization missing after write.");
                if (after.GetType() != type) throw new InvalidOperationException("Readback dynamization type differs: " + after.GetType().FullName);
                meta["exists"] = true; meta["after"] = UnifiedUiModelLogic.Tree(action == "create" ? dyn : after, 4);
                return "Dynamization " + action
                    + " completed; scalar writes read back. Native semantic validity (tag existence, formula) is not asserted; no save/compile/download.";
            });
    }
}
