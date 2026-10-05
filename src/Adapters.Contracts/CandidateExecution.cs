using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class DeviceCreateCheck
    {
        public CandidateIdentity Identity { get; set; } = new CandidateIdentity();
        public DeviceCatalogEntry Catalog { get; set; } = new DeviceCatalogEntry();
        public DeviceInventoryItem[] Inventory { get; set; } = Array.Empty<DeviceInventoryItem>();
        public string TypeIdentifier { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Digest { get; set; } = "";
    }
    public sealed class DeviceResidue
    {
        public string Status { get; set; } = "unavailable";
        public string Reason { get; set; } = "residue-read-failed";
        public DeviceInventoryItem[] Added { get; set; } = Array.Empty<DeviceInventoryItem>();
        public DeviceInventoryItem[] Removed { get; set; } = Array.Empty<DeviceInventoryItem>();
        public DeviceInventoryItem[] Changed { get; set; } = Array.Empty<DeviceInventoryItem>();
    }
    public sealed class DeviceCreateAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public DeviceInventoryItem? Created { get; set; }
        public DeviceInventoryItem[] After { get; set; } = Array.Empty<DeviceInventoryItem>();
        public DeviceResidue Residue { get; set; } = new DeviceResidue();
        public CandidateFault? Fault { get; set; }
    }
    public interface IDeviceCandidateBoundary
    {
        DeviceCreateAttempt Execute(DeviceCreateCheck check);
    }
    public sealed class PlcImportCheck
    {
        public string Release { get; set; } = "";
        public string Tool { get; set; } = "";
        public PlcImportRequest Request { get; set; } = new PlcImportRequest();
        public CandidateIdentity Identity { get; set; } = new CandidateIdentity();
        public PlcImportInput[] Inputs { get; set; } = Array.Empty<PlcImportInput>();
        public CandidateFile[] Files { get; set; } = Array.Empty<CandidateFile>();
        public PlcImportObject[] InitialInventory { get; set; } = Array.Empty<PlcImportObject>();
        public PlcImportObject[] CurrentInventory { get; set; } = Array.Empty<PlcImportObject>();
        public string GroupIdentity { get; set; } = "";
        public bool OverwriteSupported { get; set; }
        public int Index { get; set; }
        public string Digest { get; set; } = "";
    }
    public sealed class ImportResidue
    {
        public string Status { get; set; } = "unavailable";
        public string Reason { get; set; } = "residue-read-failed";
        public PlcImportObject[] Added { get; set; } = Array.Empty<PlcImportObject>();
        public PlcImportObject[] Removed { get; set; } = Array.Empty<PlcImportObject>();
        public PlcImportObject[] Changed { get; set; } = Array.Empty<PlcImportObject>();
    }
    public sealed class PlcImportAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public PlcImportObject? Imported { get; set; }
        public string ContentHash { get; set; } = "";
        public PlcImportObject[] After { get; set; } = Array.Empty<PlcImportObject>();
        public ImportResidue Residue { get; set; } = new ImportResidue();
        public CandidateFault? Fault { get; set; }
    }
    public interface IImportCandidateBoundary
    {
        PlcImportAttempt Execute(PlcImportCheck check, IDictionary<string, Stream> locks);
    }

    // No plan, confirmation, envelope or retry state. Callers execute this entire
    // primitive on the owning thread; Foundation invokes it in one worker dispatch.
    public static class CandidateExecution
    {
        private static CandidateFault Fault(Exception ex) => ex is CandidateObservationException observed ? observed.Fault
            : new CandidateFault { Kind = ex is IOException || ex is UnauthorizedAccessException ? "io" : "preflight" };
        private static void Identity(CandidateIdentity expected, CandidateIdentity actual)
        { if (CandidateDigest.Binding(expected) != CandidateDigest.Binding(actual)) CandidatePrimitives.Fail("identity", "project-binding"); }
        private static void Stale(bool mismatch, string reason)
        { if (mismatch) CandidatePrimitives.Fail("stale", reason); }
        public static DeviceInventoryItem[] Devices(IReadOnlyList<DeviceInventoryItem> rows)
        {
            if (rows == null || rows.Count > 4096 || rows.Any(i => i == null || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(i.ParentId))
                || rows.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count) CandidatePrimitives.Fail("precondition", "complete-inventory");
            return rows!.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        }
        public static PlcImportObject[] Objects(IReadOnlyList<PlcImportObject> rows)
        {
            if (rows == null || rows.Count > 4096 || rows.Any(i => i == null || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(i.Kind))
                || rows.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count) CandidatePrimitives.Fail("precondition", "complete-inventory");
            return rows!.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        }
        public static DeviceResidue DeviceResidue(DeviceInventoryItem[] before, DeviceInventoryItem[] after) => new DeviceResidue {
            Status = "checked", Reason = "", Added = after.Where(i => !before.Any(j => j.Id == i.Id)).ToArray(),
            Removed = before.Where(i => !after.Any(j => j.Id == i.Id)).ToArray(),
            Changed = after.Where(i => before.Any(j => j.Id == i.Id && CandidateDigest.DeviceRow(j) != CandidateDigest.DeviceRow(i))).ToArray() };
        public static ImportResidue ImportResidue(PlcImportObject[] before, PlcImportObject[] after) => new ImportResidue {
            Status = "checked", Reason = "", Added = after.Where(i => !before.Any(j => j.Id == i.Id)).ToArray(),
            Removed = before.Where(i => !after.Any(j => j.Id == i.Id)).ToArray(),
            Changed = after.Where(i => before.Any(j => j.Id == i.Id && CandidateDigest.ImportRow(j) != CandidateDigest.ImportRow(i))).ToArray() };

        public static DeviceCreateAttempt Create(IDeviceCreationAdapter adapter, DeviceCreateCheck check)
        {
            var result = new DeviceCreateAttempt();
            try
            {
                Identity(check.Identity, adapter.ReadIdentity());
                var catalog = adapter.ReadCatalog(check.TypeIdentifier);
                if (catalog == null || catalog.Count > 1000 || catalog.Any(r => r == null || string.IsNullOrEmpty(r.TypeIdentifier)))
                    CandidatePrimitives.Fail("stale", "device-plan-changed");
                var selected = catalog!.Where(r => r.TypeIdentifier == check.TypeIdentifier).ToArray();
                if (selected.Length != 1) CandidatePrimitives.Fail("stale", "device-plan-changed");
                var inventory = Devices(adapter.ReadInventory());
                Stale(check.Digest != CandidateDigest.DeviceObservation(check.Identity, selected[0], inventory, check.TypeIdentifier, check.DeviceName), "device-plan-changed");
                adapter.BeforeCreate();
                Identity(check.Identity, adapter.ReadIdentity());
                Stale(check.Digest != CandidateDigest.DeviceObservation(check.Identity, check.Catalog, check.Inventory, check.TypeIdentifier, check.DeviceName), "device-plan-changed");
                result.Issued = true;
                var created = adapter.Create(check.TypeIdentifier, check.DeviceName);
                if (created == null || created.IsGroup || created.Name != check.DeviceName || created.ParentId != adapter.RootId || check.Inventory.Any(i => i.Id == created.Id))
                    throw new InvalidOperationException("Created device identity is not the planned addition.");
                Identity(check.Identity, adapter.ReadIdentity());
                var after = Devices(adapter.ReadInventory());
                VerifyDeviceReadback(check, adapter.RootId, created, after);
                result.Created = created; result.After = after; result.Residue = DeviceResidue(check.Inventory, after);
            }
            catch (Exception ex)
            {
                result.Fault = Fault(ex);
                if (result.Issued)
                {
                    result.RequiresSessionReset = true;
                    try
                    {
                        if (CandidateDigest.Binding(check.Identity) == CandidateDigest.Binding(adapter.ReadIdentity())) result.Residue = DeviceResidue(check.Inventory, Devices(adapter.ReadInventory()));
                        else result.Residue.Reason = "identity-changed";
                    }
                    catch (Exception) /* swallow(privacy): preserve unavailable residue after an uncertain native write */ { }
                }
            }
            return result;
        }

        private static bool SameTarget(PlcImportObject a, PlcImportObject b) => a.Name == b.Name && a.Kind == b.Kind && a.GroupPath == b.GroupPath && (!a.Number.HasValue || a.Number == b.Number);
        public static void VerifyDeviceReadback(DeviceCreateCheck check, string root, DeviceInventoryItem created, DeviceInventoryItem[] after)
        {
            try { Devices(after); }
            catch (CandidateObservationException ex) { throw new InvalidDataException("Invalid device readback inventory.", ex); }
            if (created == null || string.IsNullOrEmpty(created.Id) || created.IsGroup || created.Name != check.DeviceName || created.ParentId != root
                || check.Inventory.Any(i => i.Id == created.Id) || after.Length != check.Inventory.Length + 1
                || !after.Any(i => CandidateDigest.DeviceRow(i) == CandidateDigest.DeviceRow(created))
                || !check.Inventory.All(i => after.Any(j => CandidateDigest.DeviceRow(i) == CandidateDigest.DeviceRow(j))))
                throw new InvalidDataException("Post-create inventory did not verify exactly one planned addition.");
        }
        public static void VerifyImportReadback(PlcImportCheck check, PlcImportObject imported, string content, PlcImportObject[] after)
        {
            try { Objects(after); }
            catch (CandidateObservationException ex) { throw new InvalidDataException("Invalid import readback inventory.", ex); }
            var input = check.Inputs[check.Index];
            if (imported == null || !SameTarget(input.Target, imported) || content != input.ContentHash)
                throw new InvalidDataException("Imported target/content differs from the reviewed input.");
            var replaced = check.Request.Overwrite ? check.CurrentInventory.Where(i => SameTarget(input.Target, i)).ToArray() : Array.Empty<PlcImportObject>();
            var retained = check.CurrentInventory.Except(replaced).ToArray();
            if (after.Length != retained.Length + 1 || !after.Any(i => i.Id == imported.Id && SameTarget(imported, i))
                || retained.Any(i => !after.Any(j => CandidateDigest.ImportRow(i) == CandidateDigest.ImportRow(j))))
                throw new InvalidDataException("Import inventory delta is not the reviewed addition/replacement.");
        }
        public static PlcImportAttempt Import(IPlcImportAdapter adapter, PlcImportCheck check, IDictionary<string, Stream> locks)
        {
            var result = new PlcImportAttempt();
            try
            {
                if (check.Index < 0 || check.Index >= check.Inputs.Length || check.Digest != CandidateDigest.ImportObservation(check)) CandidatePrimitives.Invalid("observation");
                var input = check.Inputs[check.Index];
                Identity(check.Identity, adapter.ReadIdentity());
                Stale(CandidateDigest.ImportInventory(check.CurrentInventory) != CandidateDigest.ImportInventory(Objects(adapter.ReadInventory())), "inventory-changed-before-item");
                Stale(check.GroupIdentity != adapter.TargetGroupIdentity(input.Target), "group-changed-before-item");
                Stale(CandidateDigest.Files(check.Files) != CandidateDigest.Files(CandidatePrimitives.Files(locks)), "files-changed-before-item");
                Stale(CandidateDigest.Inputs(check.Inputs) != CandidateDigest.Inputs(adapter.ReadInputs(check.Release, check.Tool, check.Request, locks)), "manifest-changed-before-item");
                Stale(check.OverwriteSupported != adapter.SupportsOverwrite(input), "overwrite-changed-before-item");
                adapter.BeforeImport(input);
                Stale(check.GroupIdentity != adapter.TargetGroupIdentity(input.Target), "group-changed-at-import-boundary");
                Stale(CandidateDigest.ImportInventory(check.CurrentInventory) != CandidateDigest.ImportInventory(Objects(adapter.ReadInventory())), "inventory-changed-at-import-boundary");
                Stale(CandidateDigest.Files(check.Files) != CandidateDigest.Files(CandidatePrimitives.Files(locks)), "files-changed-at-import-boundary");
                Identity(check.Identity, adapter.ReadIdentity());
                Stale(check.Digest != CandidateDigest.ImportObservation(check), "arguments-changed-at-import-boundary");
                result.Issued = true;
                var imported = adapter.Import(input, check.Request.Overwrite);
                if (imported == null || !SameTarget(input.Target, imported)) throw new InvalidDataException("Native import returned a different target.");
                Identity(check.Identity, adapter.ReadIdentity());
                string content = adapter.ReadContent(input, imported);
                if (content != input.ContentHash) throw new InvalidDataException("Imported content differs from the reviewed input.");
                var after = Objects(adapter.ReadInventory());
                VerifyImportReadback(check, imported, content, after);
                result.Imported = imported; result.After = after; result.ContentHash = content; result.Residue = ImportResidue(check.InitialInventory, after);
            }
            catch (Exception ex)
            {
                result.Fault = Fault(ex);
                if (result.Issued)
                {
                    result.RequiresSessionReset = true;
                    try
                    {
                        if (CandidateDigest.Binding(check.Identity) == CandidateDigest.Binding(adapter.ReadIdentity())) result.Residue = ImportResidue(check.InitialInventory, Objects(adapter.ReadInventory()));
                        else result.Residue.Reason = "identity-changed";
                    }
                    catch (Exception) /* swallow(privacy): preserve unavailable residue after an uncertain native import */ { }
                }
            }
            return result;
        }
    }
}
