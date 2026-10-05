using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class BindingAndTagDeletionTests
    {
        private static bool Refused(Action call) { try { call(); return false; } catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is PortalException) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "tia-binding-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "same.ap21");
                var binding = new ProjectBindingIdentity(21, 101, DateTime.UtcNow.Ticks, path, "same");
                binding.Verify(21, 101, binding.StartUtcTicks, path, "same");
                check(true, "exact binding accepts unchanged identity");
                check(Refused(() => binding.Verify(20, 101, binding.StartUtcTicks, path, "same")), "binding refuses engine-major change");
                check(Refused(() => binding.Verify(21, 102, binding.StartUtcTicks, path, "same")), "binding refuses another PID with same project name");
                check(Refused(() => binding.Verify(21, 101, binding.StartUtcTicks + 1, path, "same")), "binding refuses PID reuse");
                check(Refused(() => binding.Verify(21, 101, binding.StartUtcTicks, Path.Combine(root, "other", "same.ap21"), "same")), "binding refuses same name in another directory");
                check(Refused(() => binding.Verify(21, 101, binding.StartUtcTicks, path, "renamed")), "binding refuses unapproved project rename");
                check(Refused(() => ProjectBindingIdentity.CanonicalPath("relative.ap21")), "binding refuses relative file identity");
                check(new ProjectBindingIdentity(21, 101, binding.StartUtcTicks, path, "same").Generation != binding.Generation, "explicit recapture changes generation even for same path");
                string batchIdentity = BatchPlanStore.BindingState(new JsonObject { ["identity"] = binding.ToJson() });
                var reboundIdentity = new ProjectBindingIdentity(21, 101, binding.StartUtcTicks, path, "same").ToJson();
                check(batchIdentity != BatchPlanStore.BindingState(new JsonObject { ["identity"] = reboundIdentity.DeepClone() }), "old batch preview invalidated even after reconnecting to same PID/name/path");
                reboundIdentity.Remove("generation");
                check(Refused(() => BatchPlanStore.BindingState(new JsonObject { ["identity"] = reboundIdentity })), "batch refuses identity without generation");
                string hmiToken = HmiExactAccess.Token("same target", new JsonObject { ["value"] = 1 });
                check(hmiToken == HmiExactAccess.Token("same target", new JsonObject { ["value"] = 1 }), "HMI preview hash stable inside one binding");
                HmiExactAccess.InvalidateTokens();
                check(hmiToken != HmiExactAccess.Token("same target", new JsonObject { ["value"] = 1 }), "HMI event/script/settings previews invalid after binding reset despite unchanged content");
                using (var first = PortalProcessLease.Acquire(root, 101, 100))
                {
                    check(Refused(() => PortalProcessLease.Acquire(root, 101, 100)), "competing lease refused");
                    using var other = PortalProcessLease.Acquire(root, 102, 100);
                    other.ReleaseCleanly(); first.ReleaseCleanly();
                }
                using (var clean = PortalProcessLease.Acquire(root, 101, 100)) clean.ReleaseCleanly();
                check(true, "clean detach permits later explicit attachment");
                using (var dirty = PortalProcessLease.Acquire(root, 101, 100)) { }
                check(Refused(() => PortalProcessLease.Acquire(root, 101, 100)), "uncertain owner exit remains blocked after handle release");
                using (var restarted = PortalProcessLease.Acquire(root, 101, 101)) restarted.ReleaseCleanly();
                check(true, "restarted TIA has a different lease identity");

                foreach (var invalid in new[] { "Table", "/", "/a//b", "/a/../b", "/a/*", "/a/" })
                    check(Refused(() => HmiTagDeletion.Validate(invalid, "Tag", true, false)), "tag delete refuses ambiguous table path " + invalid);
                check(HmiTagDeletion.Validate("/分组/变量表", "速度", true, false).Length == 2, "tag delete preserves literal Unicode path");
                check(Refused(() => HmiTagDeletion.Validate("/Table", "*", true, false)), "tag deletion rejects wildcard");
                check(Refused(() => HmiTagDeletion.Validate("/Table", "Tag", false, false)), "tag deletion requires explicit delete flag");
                object? tag = new object(); int deletes = 0; var meta = new JsonObject();
                HmiTagDeletion.Execute(meta, true, () => tag, _ => deletes++);
                check(deletes == 0 && meta["referencesChecked"]!.GetValue<bool>() == false, "preview never deletes or claims unused");
                HmiTagDeletion.Execute(meta, false, () => tag, _ => { deletes++; tag = null; });
                check(deletes == 1 && meta["verifiedAbsent"]!.GetValue<bool>(), "delete invokes once and verifies absence");
                check(Refused(() => HmiTagDeletion.Execute(new JsonObject(), false, () => null, _ => deletes++)) && deletes == 1, "missing tag never triggers a delete");
                meta = new JsonObject();
                check(Refused(() => HmiTagDeletion.Execute(meta, false, () => new object(), _ => deletes++)) && deletes == 2 && meta["mayHaveChanged"]!.GetValue<bool>(), "failed readback preserves uncertainty without retry");

                string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
                try
                {
                    Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", "relative-for-test");
                    long before = InvocationJournal.Health()["failedWrites"]!.GetValue<long>();
                    InvocationJournal.Begin("offline-failure-probe");
                    check(InvocationJournal.Health()["failedWrites"]!.GetValue<long>() == before + 1, "unavailable journal is visible in health status");
                }
                finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
