using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class UnifiedGlobalScriptEditTests
    {
        private const string Old = "import * as Colors from 'Colors';\nexport function Navigate(name) { UI.Screen = name; }\n";
        private const string New = "import * as Colors from 'Colors';\nconst target = 'Main';\nexport function Navigate(name) { UI.Screen = target; }\n";
        public sealed class Root
        {
            public string Name => "Project_A";
            public Scripts Scripts { get; } = new Scripts();
        }
        // Real V21's composition shape: Count + indexed Item, no Find(string).
        public sealed class Scripts
        {
            public readonly List<Module> Items = new List<Module> { new Module(), new Module { Name = "Other", Text = "export function Keep() {}" } };
            public int Count => Items.Count;
            public Module this[int index] => Items[index];
            public int Imports;
            public bool Reject, NoOp;
            public Exception? ImportError;
            public Action? AfterImport;
            public bool Import(DirectoryInfo directory)
            {
                Imports++;
                var pair = UnifiedGlobalScriptEdit.ReadPair(directory.FullName, "Navigation");
                if (!NoOp && !Reject) Items[0].Text = pair.Text;
                if (ImportError != null) throw ImportError;
                AfterImport?.Invoke();
                return !Reject;
            }
        }
        public sealed class Module
        {
            public string Name { get; set; } = "Navigation";
            public string Text = Old;
            public int Exports;
            public Exception? ExportError;
            public string? Manifest;
            public bool Extra, Empty, OmitReturnedYaml, Utf16;
            public string Yaml => Manifest ?? "#Version: 2.0\r\n\r\nScriptModules:\r\n  " + Name + ":\r\n    ScriptFile: Module.hmi.js\r\n";
            public IEnumerable<FileInfo> Export(DirectoryInfo directory, string basename)
            {
                Exports++;
                if (ExportError != null) throw ExportError;
                if (Empty) return Array.Empty<FileInfo>();
                if (basename != "Module") throw new Exception("Unexpected native export base name");
                var js = new FileInfo(Path.Combine(directory.FullName, basename + ".hmi.js"));
                var yaml = new FileInfo(Path.Combine(directory.FullName, basename + ".hmi.yml"));
                File.WriteAllText(js.FullName, Text, Utf16 ? Encoding.Unicode : new UTF8Encoding(true));
                File.WriteAllText(yaml.FullName, Yaml, new UTF8Encoding(true));
                if (Extra) File.WriteAllText(Path.Combine(directory.FullName, "Extra.hmi.js"), "export function Unrelated() {}");
                return OmitReturnedYaml ? new[] { js } : new[] { js, yaml };
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== Exact global script update: native roundtrip, preview, stale content and failures ==");
            var directories = new HashSet<string>();
            Root root = null!;
            void Reset()
            {
                root = new Root(); McpServer.Portal.FixtureRoot = root;
                McpServer.Portal.FixtureResolutionError = null; McpServer.Portal.FixtureResetReadHealth();
            }
            JsonObject Edit(bool dry = true, string token = "", string text = New, string module = "Navigation", string project = "Project_A")
            {
                var meta = McpServer.UpdateUnifiedGlobalScript("HMI_RT_2", project, module, text, dry, token).Meta!;
                if (meta["backupDirectory"] is JsonNode path) directories.Add(Path.GetDirectoryName(path.ToString())!);
                return meta;
            }
            bool Ok(JsonObject meta) => meta["success"]?.GetValue<bool>() == true;
            bool Throws(Action action) { try { action(); return false; } catch { return true; } }
            try
            {
                Reset();
                check(ReferenceEquals(UnifiedScriptAccess.Module(root, "Navigation"), root.Scripts[0]), "module lookup uses indexed composition without Find");
                check(Throws(() => UnifiedScriptAccess.Module(root, "navigation")), "module lookup rejects case-near names");
                string selected = "";
                object Resolve(string path) { selected = path; return root; }
                check(ReferenceEquals(UnifiedScriptAccess.Resolve(Resolve, "/Scripts/Navigation", "HMI_RT_2", false), root.Scripts[0]) && selected == "HMI_RT_2", "generic script resolver uses explicit HMI and native read path");
                check(ReferenceEquals(UnifiedScriptAccess.Resolve(Resolve, "HMI_RT_2:Navigation", "", false), root.Scripts[0]), "legacy combined HMI:module path resolves exactly");
                check(ReferenceEquals(UnifiedScriptAccess.Resolve(Resolve, "/Scripts", "HMI_RT_2", true), root.Scripts), "generic Scripts collection resolves for official Import");
                check(Throws(() => UnifiedScriptAccess.Resolve(Resolve, "Navigation", "", false)) && Throws(() => UnifiedScriptAccess.Resolve(Resolve, "/Scripts/Navigation/Other", "HMI", false)), "missing HMI and multi-segment paths are refused");
                root.Scripts.Items.Add(new Module());
                check(Throws(() => UnifiedScriptAccess.Module(root, "Navigation")), "ambiguous modules never select first for writing"); root.Scripts.Items.RemoveAt(2);
                root.Scripts.Items.Add(new Module { Name = "A/B" });
                check(ReferenceEquals(UnifiedScriptAccess.Resolve(Resolve, "/Scripts/A%2FB", "HMI", false), root.Scripts[2]), "URI escaped single module segment is decoded exactly once"); root.Scripts.Items.RemoveAt(2);

                var preview = Edit();
                check(Ok(preview) && preview["status"]!.ToString() == "Preview" && root.Scripts.Imports == 0 && root.Scripts[0].Text == Old, "preview exports only and preserves the entire original module");
                check(preview["before"]!["scriptCode"]!.ToString() == Old && preview["after"]!["scriptCode"]!.ToString() == New && !preview["verificationSuccess"]!.GetValue<bool>(), "preview contains exact before/after without claiming applied verification");
                var applied = Edit(false, preview["token"]!.ToString());
                var backup = applied["backupDirectory"]!.ToString();
                check(Ok(applied) && applied["verificationSuccess"]!.GetValue<bool>() && applied["importReturned"]!.GetValue<bool>() && root.Scripts.Imports == 1 && root.Scripts[0].Exports == 3, "apply imports once and re-exports freshly resolved module for full-text verification");
                check(root.Scripts[0].Text == New && root.Scripts[1].Text == "export function Keep() {}" && root.Scripts.Count == 2, "exact module updated with unrelated module intact");
                check(File.ReadAllText(Path.Combine(backup, "Module.hmi.js")) == Old && File.ReadAllBytes(Path.Combine(backup, "Module.hmi.yml")).SequenceEqual(File.ReadAllBytes(Path.Combine(applied["candidateDirectory"]!.ToString(), "Module.hmi.yml"))), "native original backup kept and manifest preserved byte for byte");
                check(File.ReadAllBytes(Path.Combine(applied["candidateDirectory"]!.ToString(), "Module.hmi.js")).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "original UTF8 BOM preserved in candidate");
                var same = Edit(text: New); var noop = Edit(false, same["token"]!.ToString(), New);
                check(Ok(noop) && noop["status"]!.ToString() == "Unchanged" && root.Scripts.Imports == 1, "identical requested text avoids another import");

                Reset(); preview = Edit(); root.Scripts[0].Text += "// human edit\n";
                check(!Ok(Edit(false, preview["token"]!.ToString())) && root.Scripts.Imports == 0, "stale preview refuses import after human edit");
                Reset(); preview = Edit();
                check(!Ok(Edit(false, preview["token"]!.ToString(), New + "// changed proposal\n")) && root.Scripts.Imports == 0, "preview token binds proposed text as well as original text");
                check(!Ok(Edit(false)) && root.Scripts.Imports == 0, "missing token refuses apply");
                Reset();
                check(!Ok(Edit(project: "OtherProject")) && root.Scripts[0].Exports == 0 && root.Scripts.Imports == 0, "wrong project does not touch scripts");
                check(!Ok(Edit(module: "Missing")) && root.Scripts.Imports == 0 && root.Scripts.Count == 2, "missing module is never created");
                check(!Ok(Edit(text: "export function {")) && root.Scripts[0].Exports == 0, "invalid proposed JavaScript rejected before export");
                check(!Ok(Edit(text: new string('x', 1048577))) && root.Scripts[0].Exports == 0, "oversized source rejected without native operation");

                foreach (var manifest in new[] {
                    "ScriptModules:\n  Other:\n    ScriptFile: Module.hmi.js\n",
                    "ScriptModules:\n  Navigation:\n    ScriptFile: Module.hmi.js\n  Other:\n    ScriptFile: Module.hmi.js\n",
                    "ScriptModules:\n  Navigation:\n    ScriptFile: ../Module.hmi.js\n",
                    "ScriptModules:\n  Navigation:\n    ScriptFile: Module.hmi.js\n    Unexpected: data\n" })
                { Reset(); root.Scripts[0].Manifest = manifest; check(!Ok(Edit()) && root.Scripts.Imports == 0, "unsupported or unsafe manifest refuses import"); }
                Reset(); root.Scripts[0].Extra = true;
                check(!Ok(Edit()) && root.Scripts.Imports == 0, "extra files cannot expand import scope");
                Reset(); root.Scripts[0].Empty = true;
                check(!Ok(Edit()) && root.Scripts.Imports == 0, "empty native return never considered readable source");
                Reset(); root.Scripts[0].OmitReturnedYaml = true;
                check(!Ok(Edit()) && root.Scripts.Imports == 0, "incomplete returned file list refuses write even if manifest exists on disk");
                Reset(); root.Scripts[0].Utf16 = true; preview = Edit(); applied = Edit(false, preview["token"]!.ToString());
                check(Ok(applied) && File.ReadAllBytes(Path.Combine(applied["candidateDirectory"]!.ToString(), "Module.hmi.js")).Take(2).SequenceEqual(new byte[] { 0xff, 0xfe }), "UTF16 native encoding and BOM survive update");

                Reset(); preview = Edit(); root.Scripts.Reject = true; var rejected = Edit(false, preview["token"]!.ToString());
                check(!Ok(rejected) && rejected["apiCallSuccess"]!.GetValue<bool>() && !rejected["importReturned"]!.GetValue<bool>() && rejected["mayHaveChanged"]!.GetValue<bool>() && root.Scripts.Imports == 1 && root.Scripts[0].Exports == 2, "Import false is business failure; no readback/retry or claim of atomic rollback");
                Reset(); preview = Edit(); root.Scripts.NoOp = true; var mismatch = Edit(false, preview["token"]!.ToString());
                check(!Ok(mismatch) && mismatch["status"]!.ToString() == "ReadbackMismatch" && !mismatch["verificationSuccess"]!.GetValue<bool>() && root.Scripts.Imports == 1, "Import true without applied content is not success");
                Reset(); preview = Edit(); root.Scripts.ImportError = new ObjectDisposedException("Portal");
                var failed = Edit(false, preview["token"]!.ToString());
                check(!Ok(failed) && failed["mayHaveChanged"]!.GetValue<bool>() && failed["requiresExplicitRebind"]!.GetValue<bool>() && failed["exclusiveReleaseSkipped"]!.GetValue<bool>() && root.Scripts.Imports == 1 && root.Scripts[0].Exports == 2, "disposed during import stops remote readback and lease cleanup, reports possible partial write");
                check(!Ok(Edit()) && root.Scripts[0].Exports == 2, "session remains blocked after lost import connection");
                Reset(); preview = Edit(); root.Scripts.AfterImport = () => root.Scripts[0].ExportError = new ObjectDisposedException("Portal");
                failed = Edit(false, preview["token"]!.ToString());
                check(!Ok(failed) && failed["importReturned"]!.GetValue<bool>() && failed["requiresExplicitRebind"]!.GetValue<bool>() && !failed["verificationSuccess"]!.GetValue<bool>(), "disposed during readback preserves successful Import observation without claiming verified update");
                Reset(); root.Scripts[0].ExportError = new ObjectDisposedException("Portal"); failed = Edit();
                check(!Ok(failed) && root.Scripts.Imports == 0 && failed["requiresExplicitRebind"]!.GetValue<bool>(), "failed before-export marks session unhealthy without writing");

                Reset();
                var bridged = McpServer.CallTool("UpdateUnifiedGlobalScript", new JsonObject { ["softwarePath"] = "HMI_RT_2", ["expectedProject"] = "Wrong", ["moduleName"] = "Navigation", ["scriptCode"] = New }.ToJsonString());
                check(bridged.Meta!["success"]?.GetValue<bool>() == false && root.Scripts.Imports == 0, "generic tool dispatcher preserves project guard failure");
            }
            finally
            {
                McpServer.Portal.FixtureResetReadHealth();
                foreach (var directory in directories)
                {
                    var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TiaMcpServer", "GlobalScriptEdits")) + Path.DirectorySeparatorChar;
                    if (Path.GetFullPath(directory).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }
        }
    }
}
