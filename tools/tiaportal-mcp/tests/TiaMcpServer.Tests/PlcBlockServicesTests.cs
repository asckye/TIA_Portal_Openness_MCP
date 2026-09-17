using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class PlcBlockServicesTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool Fails<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

            // Block protection: read never writes, protect/unprotect need a password, real change needs the explicit confirmation.
            check(!PlcBlockServicesLogic.ValidateProtectionRequest("read", "", false, false), "protection: read is never a write even without preview");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateProtectionRequest("lock", "pw", true, false)), "protection: unknown action refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateProtectionRequest("protect", "", true, true)), "protection: empty password refused even in preview");
            check(!PlcBlockServicesLogic.ValidateProtectionRequest("protect", "pw", false, true), "protection: preview does not need confirmation");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateProtectionRequest("unprotect", "pw", false, false)), "protection: real change without confirmation refused");
            check(PlcBlockServicesLogic.ValidateProtectionRequest("unprotect", "pw", true, false), "protection: confirmed real change is a write (sentinel)");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateProtectionRequest("protect", new string('x', 257), true, false)), "protection: oversized password refused");
            using (var secure = PlcBlockServicesLogic.ToSecureString("abc"))
                check(secure.Length == 3 && secure.IsReadOnly(), "protection: SecureString carries every character and is read-only");
            check(PlcBlockServicesLogic.ContainsInvalidPasswordCharacter("pa$s", new[] { '$', '#' }), "protection: native invalid character detected");
            check(!PlcBlockServicesLogic.ContainsInvalidPasswordCharacter("pass", new[] { '$', '#' }), "protection: clean password passes (sentinel)");
            check(!PlcBlockServicesLogic.ContainsInvalidPasswordCharacter("pa$s", Array.Empty<char>()), "protection: empty policy rejects nothing");

            // DB snapshot: export needs a file path, other actions must not carry one, load actions need confirmValueChange.
            check(!PlcBlockServicesLogic.ValidateSnapshotRequest("read", "", false, false), "snapshot: read is never a write");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateSnapshotRequest("snapshot", "", true, true)), "snapshot: unknown action refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateSnapshotRequest("exportSnapshot", "", true, true)), "snapshot: export without file refused in preview");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateSnapshotRequest("createSnapshot", @"C:\x.xml", true, true)), "snapshot: file path on non-export action refused");
            check(!PlcBlockServicesLogic.ValidateSnapshotRequest("loadSnapshotAsActualValues", "", false, true), "snapshot: load preview needs no confirmation");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateSnapshotRequest("loadSnapshotAsActualValues", "", false, false)), "snapshot: real load without confirmValueChange refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateSnapshotRequest("loadStartValuesAsActualValues", "", false, false)), "snapshot: real start-value load without confirmValueChange refused");
            check(PlcBlockServicesLogic.ValidateSnapshotRequest("loadStartValuesAsActualValues", "", true, false), "snapshot: confirmed load is a write (sentinel)");
            check(PlcBlockServicesLogic.ValidateSnapshotRequest("createSnapshot", "", false, false), "snapshot: createSnapshot needs no value confirmation (it does not load values)");
            check(PlcBlockServicesLogic.ValidateSnapshotRequest("exportSnapshot", @"C:\x.xml", false, false), "snapshot: real export is a write (sentinel)");

            // Text lists: delete needs exact name + confirmDelete, createFromMasterCopy needs library and path.
            check(!PlcBlockServicesLogic.ValidateTextListRequest("read", "", "", "", false, false), "textlist: read is never a write");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateTextListRequest("create", "A", "", "", false, true)), "textlist: unknown action refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateTextListRequest("delete", " ", "", "", true, false)), "textlist: delete without exact name refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateTextListRequest("delete", "A", "", "", false, false)), "textlist: real delete without confirmDelete refused");
            check(!PlcBlockServicesLogic.ValidateTextListRequest("delete", "A", "", "", false, true), "textlist: delete preview needs no confirmation");
            check(PlcBlockServicesLogic.ValidateTextListRequest("delete", "A", "", "", true, false), "textlist: confirmed delete is a write (sentinel)");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateTextListRequest("createFromMasterCopy", "", "Lib", "", false, true)), "textlist: createFromMasterCopy without path refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ValidateTextListRequest("createFromMasterCopy", "", "", "Folder/TL", false, true)), "textlist: createFromMasterCopy without library refused");
            check(PlcBlockServicesLogic.ValidateTextListRequest("createFromMasterCopy", "", "Lib", "Folder/TL", false, false), "textlist: real create is a write (sentinel)");

            // Cultures: JSON array of exact, unique, known culture names.
            var cultures = PlcBlockServicesLogic.ParseCultureNames("[\"en-US\",\"de-DE\"]");
            check(cultures.Length == 2 && cultures[0].Name == "en-US" && cultures[1].Name == "de-DE", "cultures: valid array parsed (sentinel)");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("")), "cultures: empty input refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("\"en-US\"")), "cultures: bare string refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("[]")), "cultures: empty array refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("[1]")), "cultures: non-string entry refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("[\"en-US\",\"EN-us\"]")), "cultures: duplicate (case-insensitive) refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("[\"en_US!!\"]")), "cultures: malformed culture name refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.ParseCultureNames("[" + string.Join(",", Enumerable.Range(0, 65).Select(i => "\"c" + i + "\"")) + "]")), "cultures: more than 64 entries refused");

            // Pagination: honest counters and dataComplete only for a full first page.
            var meta = new JsonObject();
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.Paginate(meta, 10, -1, 10)), "paginate: negative offset refused");
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.Paginate(meta, 10, 0, 501)), "paginate: limit above 500 refused");
            var window = PlcBlockServicesLogic.Paginate(meta, 10, 0, 100);
            check(window == (0, 100) && meta["dataComplete"]!.GetValue<bool>() && meta["nextOffset"] == null && meta["actualCount"]!.GetValue<int>() == 10, "paginate: single full page is complete (sentinel)");
            PlcBlockServicesLogic.Paginate(meta, 10, 0, 4);
            check(!meta["dataComplete"]!.GetValue<bool>() && meta["truncated"]!.GetValue<bool>() && meta["nextOffset"]!.GetValue<int>() == 4 && meta["actualCount"]!.GetValue<int>() == 4, "paginate: truncated page is not complete");
            PlcBlockServicesLogic.Paginate(meta, 10, 8, 4);
            check(!meta["dataComplete"]!.GetValue<bool>() && meta["actualCount"]!.GetValue<int>() == 2 && meta["nextOffset"] == null, "paginate: last partial page is never reported complete");
            PlcBlockServicesLogic.Paginate(meta, 3, 10, 4);
            check(meta["actualCount"]!.GetValue<int>() == 0, "paginate: offset past end yields zero rows, no negative count");

            // Fingerprint route: exact IP only, ambiguity across PG/PC adapters needs the exact adapter name.
            PlcBlockServicesLogic.RouteCandidate Route(string pc, string ip) => new PlcBlockServicesLogic.RouteCandidate { ModeName = "PN/IE", PcInterfaceName = pc, TargetName = "PROFINET interface_1", Address = ip };
            var routes = new List<PlcBlockServicesLogic.RouteCandidate> { Route("Ethernet", "192.168.0.1"), Route("PLCSIM", "192.168.0.1"), Route("Ethernet", "192.168.0.10") };
            check(Fails<ArgumentException>(() => PlcBlockServicesLogic.SelectFingerprintRoute(routes, "", "")), "route: missing target IP refused, no first-route fallback");
            check(PlcBlockServicesLogic.SelectFingerprintRoute(routes, "192.168.0.10", "").PcInterfaceName == "Ethernet", "route: unique IP selected (sentinel)");
            check(Fails<PortalException>(() => PlcBlockServicesLogic.SelectFingerprintRoute(routes, "192.168.0.1", "")), "route: same IP via two adapters is ambiguous");
            check(PlcBlockServicesLogic.SelectFingerprintRoute(routes, "192.168.0.1", "plcsim").PcInterfaceName == "PLCSIM", "route: exact adapter name resolves ambiguity");
            check(Fails<PortalException>(() => PlcBlockServicesLogic.SelectFingerprintRoute(routes, "192.168.0.1", "WLAN")), "route: unknown adapter refused");
            check(Fails<PortalException>(() => PlcBlockServicesLogic.SelectFingerprintRoute(routes, "192.168.0.", "")), "route: prefix match is not an exact match");
            check(Fails<PortalException>(() => PlcBlockServicesLogic.SelectFingerprintRoute(new List<PlcBlockServicesLogic.RouteCandidate>(), "192.168.0.1", "")), "route: no configured routes refused");
        }
    }
}
