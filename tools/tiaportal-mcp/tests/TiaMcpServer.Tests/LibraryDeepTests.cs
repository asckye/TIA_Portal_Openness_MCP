using System;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Deep library family pure logic (selection / scope parsing, mode catalogs, request gating, GUID parsing, archive names)
    // plus the 2.7.31 disposed-object classifier of HmiReadSafety.
    internal static class LibraryDeepTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        // Same simple name as the Openness exception; HmiReadSafety classifies by type name only.
        private sealed class EngineeringObjectDisposedException : Exception { public EngineeringObjectDisposedException(string m, Exception? inner = null) : base(m, inner) { } }
        private sealed class RemotingException : Exception { public RemotingException(string m) : base(m) { } }

        internal static void Run(Action<bool, string> check)
        {
            // Selection JSON.
            var sel = LibraryDeepLogic.ParseSelection("[{\"folder\":\"\"}]");
            check(sel.Length == 1 && sel[0].IsFolder && sel[0].Path == "", "libdeep: empty folder = whole Types folder");
            sel = LibraryDeepLogic.ParseSelection("[{\"type\":\"Grp/Sub/FB_1\"},{\"folder\":\"/Grp/\"}]");
            check(sel.Length == 2 && !sel[0].IsFolder && sel[0].Path == "Grp/Sub/FB_1" && sel[1].IsFolder && sel[1].Path == "Grp", "libdeep: type and folder entries parsed, slashes trimmed");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[]")), "libdeep: empty selection refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[{\"type\":\"\"}]")), "libdeep: type entry without path refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[{\"type\":\"A\",\"folder\":\"B\"}]")), "libdeep: entry with two keys refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[{\"type\":\"A\"},{\"type\":\"a\"}]")), "libdeep: duplicate entry (case-insensitive) refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[{\"type\":\"../x\"}]")), "libdeep: dot-dot path refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("[\"A\"]")), "libdeep: bare string entry refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseSelection("{\"type\":\"A\"}")), "libdeep: object instead of array refused");
            // Scopes.
            check(LibraryDeepLogic.ParseScopes("").Length == 0 && LibraryDeepLogic.ParseScopes("[\"+S1-K1\",\"HMI_RT_1\"]").Length == 2, "libdeep: scope software paths parsed");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseScopes("[\"a\",\"a\"]")), "libdeep: duplicate scope refused");
            // Harmonize flags.
            check(LibraryDeepLogic.JoinHarmonizeOptions(new[] { "HarmonizeNames", "HarmonizePaths" }) == "HarmonizeNames, HarmonizePaths", "libdeep: harmonize flags joined");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.JoinHarmonizeOptions(Array.Empty<string>())), "libdeep: harmonize without flags refused (TIA refuses None)");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.JoinHarmonizeOptions(new[] { "None" })), "libdeep: harmonize None refused");
            // Mode catalogs (names of the V20/V21 enums).
            check(LibraryDeepLogic.ForceUpdateModes.Length == 3 && LibraryDeepLogic.DeleteUnusedVersionsModes.Length == 2 && LibraryDeepLogic.StructureConflictResolutionModes.Length == 3, "libdeep: update mode catalogs");
            check(LibraryDeepLogic.CleanUpModes.SequenceEqual(new[] { "PreserveDefaultVersionOfUnusedTypes", "DeleteUnusedTypes" }) && LibraryDeepLogic.UpdateCheckModes.Length == 2, "libdeep: clean-up / update-check catalogs");
            check(LibraryDeepLogic.ArchivationModes.Length == 4 && LibraryDeepLogic.CreateOptions.SequenceEqual(new[] { "None", "Override" }) && LibraryDeepLogic.ImportOptions.Length == 3, "libdeep: archive / create / import option catalogs");
            check(LibraryDeepLogic.ConsistencyStatuses.Length == 6 && LibraryDeepLogic.CompareStates.Length == 6 && LibraryDeepLogic.DetailStatuses.Length == 3, "libdeep: status catalogs");
            // Sync request gating.
            var whole = LibraryDeepLogic.ParseSelection("[{\"folder\":\"\"}]"); var scopes = new[] { "+S1-K1" };
            LibraryDeepLogic.ValidateSyncRequest("updateLibrary", "", "GL_1", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes");
            LibraryDeepLogic.ValidateSyncRequest("updateProject", "GL_1", "", scopes, whole, "NoDefaultVersionChange", "AutomaticallyDelete", "UpdateStructure", "PreserveDefaultVersionOfUnusedTypes");
            LibraryDeepLogic.ValidateSyncRequest("harmonizeProject", "", "", scopes, whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes");
            LibraryDeepLogic.ValidateSyncRequest("cleanUp", "", "", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes");
            check(true, "libdeep: valid synchronization requests accepted");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateSyncRequest("updateLibrary", "", "", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes")), "libdeep: updateLibrary onto itself refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateSyncRequest("updateProject", "", "", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes")), "libdeep: updateProject without scope refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateSyncRequest("harmonizeProject", "", "", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes")), "libdeep: harmonize without scope refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateSyncRequest("cleanUp", "", "", Array.Empty<string>(), whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "AllowTypeDeletion")), "libdeep: unknown clean-up mode refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateSyncRequest("sync", "", "GL", scopes, whole, "SetOnlyHigherUpdatedVersionAsDefault", "DoNotDelete", "RetainStructure", "DeleteUnusedTypes")), "libdeep: unknown sync action refused");
            // Type request gating.
            var props = System.Text.Json.Nodes.JsonNode.Parse("{\"DoNotUse\":true,\"SetForUpdate\":false}")!.AsObject(); var empty = new System.Text.Json.Nodes.JsonObject();
            LibraryDeepLogic.ValidateTypeRequest("update", props, "", "", Array.Empty<string>());
            LibraryDeepLogic.ValidateTypeRequest("delete", empty, "", "", Array.Empty<string>());
            LibraryDeepLogic.ValidateTypeRequest("updateLibrary", empty, "GL_1", "", Array.Empty<string>());
            LibraryDeepLogic.ValidateTypeRequest("updateProject", empty, "", "", scopes);
            check(true, "libdeep: valid type requests accepted");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateTypeRequest("update", empty, "", "", Array.Empty<string>())), "libdeep: type update without properties refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateTypeRequest("update", System.Text.Json.Nodes.JsonNode.Parse("{\"Author\":\"x\"}")!.AsObject(), "", "", Array.Empty<string>())), "libdeep: non-editable type property refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateTypeRequest("delete", props, "", "", Array.Empty<string>())), "libdeep: properties on delete refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateTypeRequest("updateLibrary", empty, "", "", Array.Empty<string>())), "libdeep: type updateLibrary onto itself refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateTypeRequest("updateProject", empty, "", "", Array.Empty<string>())), "libdeep: type updateProject without scope refused");
            // Compare request gating.
            LibraryDeepLogic.ValidateCompareRequest("type", "A/FB_1", "FB_1", "", ""); LibraryDeepLogic.ValidateCompareRequest("version", "FB_1", "FB_1", "1.0.0", "1.0.1"); LibraryDeepLogic.ValidateCompareRequest("masterCopy", "MC_1", "MC_1", "", "");
            check(true, "libdeep: valid compare requests accepted");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateCompareRequest("version", "FB_1", "FB_1", "1.0.0", "")), "libdeep: version compare without both versions refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateCompareRequest("type", "FB_1", "FB_1", "1.0.0", "")), "libdeep: versions on type compare refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateCompareRequest("block", "a", "b", "", "")), "libdeep: unknown compare kind refused");
            // GUIDs and archive names.
            check(LibraryDeepLogic.ParseGuid("", "g") == null && LibraryDeepLogic.ParseGuid(" {45e9f803-eb80-4171-af9e-46796145a784} ", "g") == Guid.Parse("45e9f803-eb80-4171-af9e-46796145a784"), "libdeep: GUID parsed (braces, padding) and empty = none");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ParseGuid("not-a-guid", "g")), "libdeep: bad GUID refused");
            LibraryDeepLogic.ValidateArchiveName("Sample1"); LibraryDeepLogic.ValidateArchiveName("Sample1.zal21");
            check(true, "libdeep: archive names accepted");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateArchiveName("a/b")) && Fails<ArgumentException>(() => LibraryDeepLogic.ValidateArchiveName("")), "libdeep: archive name with separator / empty refused");
            check(Fails<ArgumentException>(() => LibraryDeepLogic.ValidateBounds(0, 10)) && Fails<ArgumentException>(() => LibraryDeepLogic.ValidateBounds(3, 9999)), "libdeep: bounds refused outside 1..16 / 1..5000");
            // HmiReadSafety: a disposed Openness proxy alone is not a session loss; anything remoting-shaped in the chain still is.
            check(HmiReadSafety.DisposedObjectOnly(new EngineeringObjectDisposedException("Access to a disposed object of type 'Siemens.Engineering.HW.MrpDomain' is not possible.")), "safety: disposed-object exception alone is classified as disposed-only");
            check(HmiReadSafety.DisposedObjectOnly(new InvalidOperationException("wrap", new EngineeringObjectDisposedException("inner"))), "safety: disposed-object inner exception is classified as disposed-only");
            check(!HmiReadSafety.DisposedObjectOnly(new EngineeringObjectDisposedException("outer", new RemotingException("channel gone"))), "safety: disposed + remoting in the chain is not disposed-only");
            check(!HmiReadSafety.DisposedObjectOnly(new InvalidOperationException("plain")), "safety: unrelated exception is not disposed-only");
            check(HmiReadSafety.ConnectionUnavailable(new EngineeringObjectDisposedException("x")), "safety: ConnectionUnavailable still flags disposed objects (global fail-safe kept; expected cases are caught locally)");
        }
    }
}
