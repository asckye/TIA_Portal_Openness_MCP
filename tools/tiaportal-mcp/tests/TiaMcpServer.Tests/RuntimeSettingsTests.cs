using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class RuntimeSettingsTests
    {
        public enum Resolution { SR_800X480, SR_1920X1080 }
        public enum Interaction { Customized, Enabled, Disabled }
        public class Settings
        {
            private string start = "Old";
            public int Writes;
            public bool IgnoreStart, BreakReadback, CoupleResolution;
            public Exception? WriteError;
            public virtual string StartScreen
            {
                get => BreakReadback && Writes > 0 ? throw new ObjectDisposedException("RuntimeSettings") : start;
                set { Writes++; if (WriteError != null) throw WriteError; if (!IgnoreStart) start = value; if (CoupleResolution) ScreenResolution = Resolution.SR_800X480; }
            }
            public Resolution ScreenResolution { get; set; } = Resolution.SR_800X480;
            public bool BitSelection { get; set; }
            public Interaction CentralPanning { get; set; }
            public Interaction CentralInputHint { get; private set; }
            public object Parent => throw new Exception("Parent must not be read");
            public object TelemetryRuntimeSettings => throw new Exception("Nested object must not be traversed");
        }
        public sealed class Screen { public string Name { get; set; } = ""; }
        public sealed class Group
        {
            public string Name { get; set; } = "";
            public List<Group> Groups { get; } = new List<Group>();
            public List<Screen> Screens { get; } = new List<Screen>();
        }
        public sealed class Hmi
        {
            public Settings RuntimeSettings { get; } = new Settings();
            public List<Screen> Screens { get; } = new List<Screen> { new Screen { Name = "Old" } };
            public List<Group> ScreenGroups { get; } = new List<Group>();
        }
        private static JsonObject Update(Hmi hmi, string changes, bool dryRun = true, string token = "")
        {
            var result = new JsonObject();
            UnifiedRuntimeSettingsAccess.Update(hmi, "Project_A", "HMI", UnifiedRuntimeSettingsAccess.Changes(changes), dryRun, token, result, new HmiReadTrace());
            return result;
        }
        private static bool Fails(Action action) { try { action(); return false; } catch { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== HMI Runtime startup settings preview/write/readback ==");
            var hmi = new Hmi(); var folder = new Group { Name = "Pop up" }; hmi.ScreenGroups.Add(folder); folder.Screens.Add(new Screen { Name = "New" });
            const string changes = "{\"StartScreen\":\"/Pop%20up/New\",\"ScreenResolution\":\"SR_1920X1080\"}";
            var read = new JsonObject(); UnifiedRuntimeSettingsAccess.Read(hmi, new[] { "StartScreen", "ScreenResolution" }, read, new HmiReadTrace());
            check(read["dataComplete"]!.GetValue<bool>() && read["actualCount"]!.GetValue<int>() == 2 && hmi.RuntimeSettings.Writes == 0, "read settings with no writes or nested traversal");
            check(read["capabilities"]!.AsArray().Single(c => c!["field"]!.ToString() == "ScreenResolution")!["allowedValues"]!.AsArray().Count == 2, "read advertises real enum names");
            var preview = Update(hmi, changes);
            check(preview["status"]!.ToString() == "Preview" && hmi.RuntimeSettings.StartScreen == "Old" && hmi.RuntimeSettings.Writes == 0, "preview never mutates runtime settings");
            check(preview["proposed"]!["StartScreen"]!.ToString() == "New" && preview["startScreenTarget"]!["uniqueNameVerified"]!.GetValue<bool>(), "nested escaped screen path maps to exact unique API name");
            var token = preview["token"]!.ToString();
            check(Fails(() => Update(hmi, changes, false)) && hmi.RuntimeSettings.Writes == 0, "missing apply token blocks setters");
            check(Fails(() => Update(hmi, changes, false, "wrong")) && hmi.RuntimeSettings.Writes == 0, "wrong token blocks setters");
            check(Fails(() => Update(hmi, "{\"StartScreen\":\"/Old\"}", false, token)) && hmi.RuntimeSettings.Writes == 0, "token is bound to proposed changes");
            hmi.RuntimeSettings.ScreenResolution = Resolution.SR_1920X1080;
            check(Fails(() => Update(hmi, changes, false, token)) && hmi.RuntimeSettings.Writes == 0, "changed source settings invalidate preview token");
            hmi.RuntimeSettings.ScreenResolution = Resolution.SR_800X480;
            var applied = Update(hmi, changes, false, token);
            check(applied["verificationSuccess"]!.GetValue<bool>() && hmi.RuntimeSettings.StartScreen == "New" && hmi.RuntimeSettings.ScreenResolution == Resolution.SR_1920X1080, "startup screen and resolution written then verified");
            check(applied["readback"]!["StartScreen"]!["value"]!.ToString() == "New" && applied["actualCount"]!.GetValue<int>() == 2, "final readback reports each requested field");
            int writes = hmi.RuntimeSettings.Writes;
            var noOpPreview = Update(hmi, changes); var noOp = Update(hmi, changes, false, noOpPreview["token"]!.ToString());
            check(noOp["status"]!.ToString() == "Unchanged" && hmi.RuntimeSettings.Writes == writes && !noOp["writeAttempted"]!.GetValue<bool>(), "already-matching settings do not invoke setters");
            foreach (var invalid in new[] {
                "{\"StartScreen\":\"New\"}", "{\"StartScreen\":\"/Missing\"}", "{\"StartScreen\":\"/Pop%20up/new\"}",
                "{\"ScreenResolution\":1}", "{\"ScreenResolution\":\"sr_800x480\"}", "{\"BitSelection\":\"true\"}",
                "{\"CentralInputHint\":\"Enabled\"}", "{\"Parent\":\"x\"}", "{\"LanguageAndFonts[0].Enable\":true}",
                "{\"StartScreen\":null}", "{\"StartScreen\":\"\"}", "{\"BitSelection\":true,\"BitSelection\":false}" })
                check(Fails(() => Update(hmi, invalid)) && hmi.RuntimeSettings.Writes == writes, "reject unsafe/unsupported request before writing: " + invalid);
            hmi.Screens.Add(new Screen { Name = "new" });
            check(Fails(() => Update(hmi, changes)) && hmi.RuntimeSettings.Writes == writes, "duplicate name across folders rejected even with exact target path");
            hmi.Screens.RemoveAt(hmi.Screens.Count - 1);
            hmi.RuntimeSettings.StartScreen = "Old"; hmi.RuntimeSettings.ScreenResolution = Resolution.SR_800X480;
            var rejectPreview = Update(hmi, changes); hmi.RuntimeSettings.IgnoreStart = true;
            var rejected = Update(hmi, changes, false, rejectPreview["token"]!.ToString());
            check(!rejected["operationSuccess"]!.GetValue<bool>() && rejected["status"]!.ToString() == "ReadbackMismatch" && rejected["mayHaveChanged"]!.GetValue<bool>(), "setter returning without effective change is not reported successful");
            check(rejected["appliedFields"]!.AsArray().Count == 2 && hmi.RuntimeSettings.ScreenResolution == Resolution.SR_1920X1080, "partial multi-setting changes retained and reported, no automatic rollback");
            hmi.RuntimeSettings.IgnoreStart = false; hmi.RuntimeSettings.ScreenResolution = Resolution.SR_800X480;
            var coupledPreview = Update(hmi, changes); hmi.RuntimeSettings.CoupleResolution = true;
            var coupled = Update(hmi, changes, false, coupledPreview["token"]!.ToString());
            check(!coupled["verificationSuccess"]!.GetValue<bool>() && coupled["failures"]!.AsArray().Count == 1, "final readback detects another setting changed by the last setter");
            hmi.RuntimeSettings.CoupleResolution = false;
            var failing = new Hmi(); failing.Screens.Add(new Screen { Name = "New" });
            var failingPreview = Update(failing, "{\"StartScreen\":\"/New\"}");
            failing.RuntimeSettings.WriteError = new ObjectDisposedException("Portal");
            check(Fails(() => Update(failing, "{\"StartScreen\":\"/New\"}", false, failingPreview["token"]!.ToString())) && failing.RuntimeSettings.Writes == 1, "fatal setter error is propagated without retry");
            var native = new global::Siemens.Engineering.HmiUnified.HmiSoftware();
            var portal = new Portal { FixtureRoot = native };
            check(portal.ReadUnifiedRuntimeSettings("HMI", "Project_A").Meta!["success"]!.GetValue<bool>(), "dedicated read tool handles explicit Unified project scope");
            int resolutions = portal.FixtureResolveCalls;
            check(!portal.UpdateUnifiedRuntimeSettings("HMI", "Wrong", "{\"BitSelection\":true}").Meta!["success"]!.GetValue<bool>() && portal.FixtureResolveCalls == resolutions, "project mismatch stops before resolving HMI");
            var setting = (Settings)native.RuntimeSettings;
            var guarded = portal.UpdateUnifiedRuntimeSettings("HMI", "Project_A", "{\"BitSelection\":true}");
            check(guarded.Meta!["dryRun"]!.GetValue<bool>() && !setting.BitSelection, "Portal write tool defaults to preview");
            native.Screens.Add(new GraphicSelectionTests.Screen());
            var fatalPreview = portal.UpdateUnifiedRuntimeSettings("HMI", "Project_A", "{\"StartScreen\":\"/Main\"}");
            setting.BreakReadback = true;
            var fatal = portal.UpdateUnifiedRuntimeSettings("HMI", "Project_A", "{\"StartScreen\":\"/Main\"}", false, fatalPreview.Meta!["token"]!.ToString()).Meta!;
            check(fatal["connectionUnavailable"]!.GetValue<bool>() && fatal["exclusiveReleaseSkipped"]!.GetValue<bool>() && fatal["mayHaveChanged"]!.GetValue<bool>(), "fatal readback blocks future HMI operations and skips remote lease disposal");
            check(portal.ReadUnifiedRuntimeSettings("HMI", "Project_A").Meta!["status"]!.ToString() == "HmiReadSessionBlocked", "runtime reads respect shared HMI health block");
            McpServer.Portal = new Portal { FixtureRoot = new global::Siemens.Engineering.HmiUnified.HmiSoftware() };
            check(McpServer.CallTool("UpdateUnifiedRuntimeSettings", "{\"softwarePath\":\"HMI\",\"expectedProject\":\"Project_A\",\"changesJson\":\"{\\\"BitSelection\\\":true}\"}").Meta!["success"]!.GetValue<bool>(), "write preview exposed through CallTool");
            check(!McpServer.CallTool("UpdateUnifiedRuntimeSettings", "{\"softwarePath\":\"HMI\",\"expectedProject\":\"Project_A\",\"changesJson\":\"{\\\"BitSelection\\\":true}\",\"dryRun\":false}").Meta!["success"]!.GetValue<bool>(), "CallTool propagates missing-token business failure");
        }
    }
}
