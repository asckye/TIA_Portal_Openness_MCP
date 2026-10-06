using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// Thin CLI front-end: maps verbs (gen/patch/compile/export/import/describe/prewarm/schema/
    /// version/help) onto the existing McpServer engine statics. No new Openness logic lives here —
    /// it only loads input, calls the engine, formats output, and returns an exit code.
    /// </summary>
    public static class CliCommands
    {
        private static readonly string[] Verbs =
            { "gen", "patch", "compile", "export", "import", "describe", "prewarm", "config", "doctor", "schema", "version", "help", "--help", "-h", "audit" };

        public static bool IsVerb(string s) => Array.IndexOf(Verbs, s.ToLowerInvariant()) >= 0;

        internal static int RunWithContext(string[] args)
            => CliBoundary.RunContext(args, () => EngineServices.InitializeStandalone(), () => Run(args), Console.Error);

        public static int Run(string[] args)
        {
            if (CliBoundary.TryValidate(args, out int syntaxExit)) return syntaxExit;
            if (CliBoundary.IsTool(args)) return CliToolExecution.Run(args);
            var verb = args[0].ToLowerInvariant();
            try
            {
                switch (verb)
                {
                    case "prewarm": return Prewarm(args);
                    case "config": return Config(args);
                    case "doctor": return DoctorCli(args);
                    case "install-plc-tools": return InstallPlcToolsCommand.Run(args);
                    case "audit": return TiaOpenness.Shared.AuditCli.Verify(Opt(args, "--path") is string auditPath
                        ? new[] { auditPath } : TiaOpenness.Shared.DataLocations.Current.AuditReadRoots, Console.Out);
                    case "schema": Console.WriteLine(SchemaText); return 0;
                    case "version": Console.WriteLine("tia " + AssemblyVersion()); return 0;
                    default: PrintUsage(); return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                if (ex is TiaOpenness.Shared.BundleResourceUnavailableException) return 70;
                return 2;
            }
        }

        // ---- verbs ----

        private static int Prewarm(string[] args)
        {
            if (Flag(args, "--stop"))
            {
                if (!EngineServices.Get<Siemens.Portal>().IsConnected()) EngineServices.Get<SessionTools>().Connect(); // attach to the running headless instance
                EngineServices.Get<SessionTools>().Disconnect();                          // Dispose it
                Console.WriteLine("prewarm: stopped (headless instance disposed).");
                return 0;
            }

            Console.WriteLine("prewarm: cold-starting headless TIA and holding it open. Press Ctrl+C to stop.");
            EngineServices.Get<SessionTools>().Connect();
            Console.WriteLine($"prewarm: ready ({EngineServices.Get<SessionTools>().GetState().Message}). Subsequent `tia` commands will attach in ~1s.");

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            while (!stop.IsSet)
            {
                stop.Wait(60000);
                if (!stop.IsSet) { try { _ = EngineServices.Get<SessionTools>().GetState(); } catch { /* swallow(probe-optional): a failed heartbeat must keep the prewarm loop alive */ } } // heartbeat
            }
            try { EngineServices.Get<SessionTools>().Disconnect(); } catch { /* swallow(teardown): prewarm shutdown remains best-effort */ }
            Console.WriteLine("prewarm: stopped.");
            return 0;
        }

        // One-click MCP registration into AI hosts (Claude Desktop / Claude Code / Cursor /
        // VS Code), no manual JSON editing. Self-discovers everything: own exe path, TIA
        // version from the registry, and the version-matching sibling exe.
        private static int Config(string[] args)
        {
            // Main has already resolved all aliases and selected the matching engine.
            int ver = TiaMcpServer.Siemens.Engineering.TiaMajorVersion;
            TiaMcp.Versioning.TiaVersionCatalog.RequireMatchingEngine(ver, Siemens.EngineRouter.CompiledTiaMajorVersion);
            string exe = McpConfigInstaller.ExeForVersion(ver);
            // The engine defaults to lite, so a plain config need not pin a profile.
            // --full selects the complete roster; --lite explicitly selects the default.
            bool full = Flag(args, "--full");

            if (Flag(args, "--print"))
            {
                Console.WriteLine("Claude Desktop / Claude Code / Cursor (mcpServers):");
                Console.WriteLine(McpConfigInstaller.Snippet(exe, ver, McpConfigInstaller.HostStyle.McpServers, full));
                Console.WriteLine();
                Console.WriteLine("VS Code — %APPDATA%\\Code\\User\\mcp.json (servers):");
                Console.WriteLine(McpConfigInstaller.Snippet(exe, ver, McpConfigInstaller.HostStyle.VsCode, full));
                Console.WriteLine();
                Console.WriteLine("Gemini CLI / Windsurf / Cline use the same mcpServers shape as the first snippet.");
                Console.WriteLine();
                Console.WriteLine("Codex — %USERPROFILE%\\.codex\\config.toml (TOML):");
                Console.WriteLine(McpConfigInstaller.Snippet(exe, ver, McpConfigInstaller.HostStyle.CodexToml, full));
                return 0;
            }

            string? only = Opt(args, "--host"); // claude|claude-code|cursor|vscode (default: all installed)
            int done = 0, failed = 0;
            foreach (var h in McpConfigInstaller.KnownHosts())
            {
                bool targeted = !string.IsNullOrEmpty(only) && MatchesHost(h.Name, only!);
                if (!string.IsNullOrEmpty(only) && !targeted) continue;

                // Without an explicit --host, only touch hosts that look installed —
                // don't fabricate config files for IDEs the user doesn't have.
                bool installed = System.IO.File.Exists(h.ConfigPath) ||
                                 System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(h.ConfigPath));
                if (!targeted && !installed)
                {
                    Console.WriteLine("  [skip]   " + h.Name + " (not detected on this machine)");
                    continue;
                }

                try { Console.WriteLine("  [ok]     " + h.Name + ": " + McpConfigInstaller.Apply(h.ConfigPath, exe, ver, h.Style, full)); done++; }
                catch (Exception ex) { Console.Error.WriteLine("  [failed] " + h.Name + ": " + ex.Message); failed++; }
            }

            Console.WriteLine(done > 0
                ? $"Configured {done} host(s) for TIA V{ver} -> {exe}{(full ? " [full profile: all tools — exceeds VS Code/Copilot's 128 and Windsurf's 100 tool cap]" : " [default lite profile: ~48 core tools; the rest stay reachable via FindTools/CallTool]")}. Restart the AI client to load it. (original config backed up as *.bak)"
                : "No host config written. Targeted host not found, or use `config --print` to copy the snippet manually.");
            Console.WriteLine("For other hosts, run `config --print` and paste the matching snippet.");
            return failed > 0 && done == 0 ? 1 : 0;
        }

        // `tia doctor` — standalone environment check that works even when the MCP host can't
        // start the server (the exact situation where an in-server Doctor tool is unreachable).
        // Read-only by default; --fix adds the current user to the Openness group (may UAC).
        private static int DoctorCli(string[] args)
        {
            bool fix = Flag(args, "--fix");
            bool zh = Runtime.EnvironmentDoctor.PreferChinese;

            Console.WriteLine(zh
                ? "tia doctor —— 环境体检" + (fix ? "（修复模式）" : "（只读；加 --fix 可自动把当前用户加入 Openness 组）")
                : "tia doctor — environment check" + (fix ? " (fix mode)" : " (read-only; pass --fix to auto-add the Openness group)"));

            bool ready = true;
            try
            {
                string logs = TiaOpenness.Shared.DataLocations.Current.LogDirectory(TiaMcpServer.Siemens.EngineRouter.CompiledTiaMajorVersion.ToString());
                Console.WriteLine("Logs: " + logs);
                Console.WriteLine("Diagnostics: " + TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory);
            }
            catch (IOException error) { Console.Error.WriteLine(error.Message); ready = false; }

            void Line(bool ok, string name, string detail, string? fixHint)
            {
                Console.WriteLine($"  [{(ok ? " ok " : "FAIL")}] {name}: {detail}");
                if (!ok && !string.IsNullOrEmpty(fixHint))
                    Console.WriteLine($"         {(zh ? "修法" : "fix")}: {fixHint}");
            }

            var detected = TiaMcpServer.Siemens.Engineering.DetectTiaMajorVersion();
            int compiled = TiaMcpServer.Siemens.EngineRouter.CompiledTiaMajorVersion;

            int effective = TiaMcpServer.Siemens.Engineering.TiaMajorVersion;
            bool supported = TiaMcp.Versioning.TiaVersionCatalog.Runnable.Where(v => v.IsFullEngine).Any(v => v.MajorVersion == effective)
                && (!detected.HasValue || TiaMcp.Versioning.TiaVersionCatalog.Runnable.Where(v => v.IsFullEngine).Any(v => v.MajorVersion == detected.Value));
            Line(supported, "Engine version support", "Selected V" + effective + "; detected " +
                (detected.HasValue ? "V" + detected.Value : "unknown"),
                "Only V20/V21 have runnable engines; earlier catalog entries are planned only.");
            ready &= supported;

            foreach (var c in Runtime.EnvironmentDoctor.Run(compiled, detected))
            {
                Line(c.Ok, c.Name(zh), c.Detail(zh), c.Fix(zh));
                if (c.Gating) ready &= c.Ok;
            }

            // Openness group is the one check that can also repair itself, so it stays here rather
            // than in the shared read-only set.
            bool groupOk; string groupDetail;
            try
            {
                groupOk = fix
                    ? TiaMcpServer.Siemens.Openness.IsUserInGroup().GetAwaiter().GetResult()
                    : TiaMcpServer.Siemens.Openness.IsUserInGroupNoFix();
                groupDetail = groupOk
                    ? (zh ? "当前用户已在 'Siemens TIA Openness' 组" : "current user is in 'Siemens TIA Openness'")
                    : (zh ? "当前用户不在 'Siemens TIA Openness' 组" : "current user NOT in 'Siemens TIA Openness'");
            }
            catch (Exception ex)
            {
                groupOk = false;
                groupDetail = (zh ? "检查失败：" : "check failed: ") + ex.Message;
            }
            Line(groupOk, zh ? "Openness 用户组" : "Openness user group", groupDetail,
                zh ? "运行 `tia doctor --fix`（会弹 UAC），或用 lusrmgr.msc 把当前 Windows 用户加入本地组 'Siemens TIA Openness'，然后注销重登。"
                   : "run `tia doctor --fix` (prompts UAC), or add your Windows user to the local group 'Siemens TIA Openness' (lusrmgr.msc) and sign out/in.");
            ready &= groupOk;

            // AI host configs (informational — does not gate readiness)
            foreach (var h in McpConfigInstaller.KnownHosts())
            {
                // "Registered" is not "working": a config copied from another machine, or one
                // written before the bundle moved, still holds the entry while pointing at an exe
                // that is gone — the host then silently fails to start the server.
                string? cmd = McpConfigInstaller.RegisteredCommand(h);
                bool present = cmd != null;
                bool exeOk = present && File.Exists(cmd!);
                string mark = !present ? " -- " : (exeOk ? " ok " : "FAIL");
                string state = !present
                    ? (zh ? "未注册" : "not registered")
                    : exeOk
                        ? (zh ? "已注册 tia-portal" : "tia-portal registered")
                        : (zh ? "已注册，但指向的引擎不存在：" + cmd : "registered, but the engine it points at is missing: " + cmd);
                Console.WriteLine($"  [{mark}] {(zh ? "AI 客户端配置" : "AI host config")} — {h.Name}: {state}");
                if (present && !exeOk)
                    Console.WriteLine("         " + (zh
                        ? $"修法: 运行 `tia config --host {h.Name.Split(' ')[0].ToLowerInvariant()}` 重新指向本交付包的引擎。"
                        : $"fix: run `tia config --host {h.Name.Split(' ')[0].ToLowerInvariant()}` to repoint it at this bundle's engine."));
            }
            Console.WriteLine(zh
                ? "         （一次性写入所有检测到的客户端：tia config）"
                : "         (register into all detected hosts with: tia config)");

            Console.WriteLine(ready
                ? (zh ? "READY —— 环境正常。下一步：重启 AI 客户端，让它调用 InitializeEnvironment。"
                      : "READY — environment OK. Next: restart your AI client and ask it to call InitializeEnvironment.")
                : (zh ? "NOT READY —— 请按上面的『修法』处理 FAIL 项，然后重新运行 tia doctor。"
                      : "NOT READY — fix the FAIL items above, then run `tia doctor` again."));
            return ready ? 0 : 1;
        }

        private static bool MatchesHost(string hostName, string query)
        {
            string norm(string s) => s.Replace(" ", "").Replace("-", "").ToLowerInvariant();
            return norm(hostName).Contains(norm(query));
        }

        // ---- helpers ----

        private static string? Opt(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static bool Flag(string[] args, string name) =>
            args.Skip(1).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static string AssemblyVersion() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        private static void PrintUsage() => Console.WriteLine(UsageText);

        private const string UsageText =
@"tia — drive TIA Portal from a single spec. (Same engine as the MCP server.)

USAGE
  tia gen      <spec.yaml|json> [--dry-run] [--json]      Build a project from a spec
  tia patch    <spec.yaml|json> [--dry-run] [--json] [--no-overwrite]
                                                          Upsert spec into an EXISTING project (spec.projectPath)
  tia compile  <project.apXX> [--plc NAME] [--json]       Compile + diagnose a PLC
  tia describe <project.apXX> [--plc NAME] [--json]       Print project tree (and PLC blocks)
  tia export   <project.apXX> --plc NAME --out DIR --block PATH [--scl]
  tia import   <project.apXX> --plc NAME --from DIR [--no-overwrite]
  tia prewarm  [--stop]                                   Hold a headless instance open (~1s attach after)
  tia config   [--host claude|claude-code|cursor|vscode|codex|gemini|windsurf|cline] [--print] [--full]
                                                          One-click: register this MCP into all detected AI hosts
                                                          (Claude Desktop / Claude Code / Cursor / VS Code); auto-picks
                                                          the exe matching your installed TIA version.
                                                          Default lists 61 core tools; the rest stay reachable
                                                          on demand via FindTools + CallTool.
                                                          --full = list every tool instead (rejected by VS Code/
                                                          Copilot above 128 and Windsurf above 100)
  tia doctor   [--fix]                                    Environment check: TIA install, exe/version match, Openness
                                                          group, AI host configs. --fix auto-adds the Openness group
  tia install-plc-tools [--python PATH] [--environment-path DIR]
                                                          Install pinned companion dependencies in the user's Python 3.12+ environment
  tia schema                                              Print the spec field reference
  tia audit verify [--path DIR]                            Verify the audit chain; JSON report, exit 0 intact / 3 broken
  tia version

GLOBAL FLAGS (also accepted): --with-ui, --tia-portal-location PATH, --tia-version KEY
  --bundle-root ABSOLUTE_PATH overrides TIA_MCP_BUNDLE_ROOT and formal install/development anchors.
  Runnable keys: 20, 21. --tia-major-version N remains an alias.
  Keys 14sp1, 15.1, 16, 17, 18, 19 are planned only and cannot run.
  Original V14 and V15 are outside the target scope and are unsupported.
  Put project/spec paths before global flags. help/version/schema need no TIA installation.
MCP SERVER FLAGS (no subcommand): --isolate-openness, --worker-timeout-seconds 10..180 (default 120)
  Opt-in worker isolation; local tests do not establish native TIA stability. See docs/guides/openness-worker-isolation.md.
Tool exit codes: 0 = complete success, 2 = rejected, 3 = failed, 4 = partial, 5 = unknown;
64 = syntax error, 70 = tool context creation failure. Tool stdout is one V4 JSON envelope.";

        private const string SchemaText =
@"PROJECT SPEC (YAML or JSON). JSON is canonical; YAML is for humans.
Used by `tia gen` (build from zero) and `tia patch` (upsert into existing).

  projectName     string  gen: required. Project name.
  projectPath     string  patch: required. Path to the .apXX to open.
  directoryPath   string  gen: output folder (default data/temp; per-user temp fallback).
  plcName         string  default PLC_1.
  plcFamily       string  default S7-1500.
  plcMlfb         string  exact order number (optional).
  hmiName         string  omit to skip all HMI.
  hmiFamily       string  default WinCCUnifiedPC.
  hmiSoftwarePath string  blank = auto-probe.
  connectionName  string  default HMI_Connection_1.
  udt[]           objects same shape as BuildPlcUdt / BuildAndImportPlcArtifact.
  globalDb[]      objects same shape as BuildPlcGlobalDb.
  tagTable[]      objects same shape as BuildPlcTagTable.
  sclSourceFiles[] strings .scl external-source file paths.
  ladDocs[]       {importPath, name}  S7DCL document import.
  hmiScreens[]    {screenName, width, height, designJson(object)}.
  hmiTags[]       {tagTableName?, tagName, hmiDataType?, plcTag?, address?}.
  compile         bool   default true.
  save            bool   default true.

NOTES
  * Set width/height to the panel's native resolution or the screen is clipped.
  * Use absolute addresses (%M..) for hmiTags to pass read-back verification.
  * patch --no-overwrite protects hand-edited LAD code blocks (imported as None);
    UDT/DB/tag tables always re-sync to the spec.";
    }
}
