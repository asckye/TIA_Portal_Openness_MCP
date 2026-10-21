using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.Cli;
using Runtime = TiaMcpServer.Runtime;

namespace TiaMcp.FoundationHost
{
    // Host-only commands share their output across all release keys. No project operation
    // or native object enters this command surface.
    internal static class HostCliCommands
    {
        internal static int Run(HostCliOptions command)
        {
            var args = command.Arguments;
            try
            {
                switch (args[0])
                {
                    case "doctor":
                        using (var probe = new HostDoctorProbe(command.Host)) return DoctorCli(args, probe);
                    case "config": return Config(args, command.Host.ReleaseKey);
                    case "install-plc-tools": return InstallPlcToolsCommand.Run(args);
                    case "audit": return TiaOpenness.Shared.AuditCli.Verify(Opt(args, "--path") is string path
                        ? new[] { path } : TiaOpenness.Shared.DataLocations.Current.AuditReadRoots, Console.Out);
                    case "schema": Console.WriteLine(SchemaText); return 0;
                    case "version": Console.WriteLine("tia " + AssemblyVersion()); return 0;
                    default: Console.WriteLine(UsageText); return 0;
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ERROR: " + error.Message);
                return error is TiaOpenness.Shared.BundleResourceUnavailableException ? 70 : 3;
            }
        }

        private static int Config(string[] args, string ver)
        {
            string exe = McpConfigInstaller.ExeForVersion(ver);
            // A plain config uses the host's default profile.
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

            return WriteConfig(args, ver, exe, McpConfigInstaller.KnownHosts(), Console.Out, Console.Error);
        }

        internal static int WriteConfig(string[] args, string ver, string exe, IEnumerable<McpConfigInstaller.Host> hosts,
            TextWriter output, TextWriter diagnostics)
        {
            string? only = Opt(args, "--host"); // claude|claude-code|cursor|vscode (default: all installed)
            bool full = Flag(args, "--full");
            int done = 0, failed = 0;
            foreach (var h in hosts)
            {
                bool targeted = !string.IsNullOrEmpty(only) && MatchesHost(h.Name, only!);
                if (!string.IsNullOrEmpty(only) && !targeted) continue;

                // Without an explicit --host, only touch hosts that look installed —
                // don't fabricate config files for IDEs the user doesn't have.
                bool installed = System.IO.File.Exists(h.ConfigPath) ||
                                 System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(h.ConfigPath));
                if (!targeted && !installed)
                {
                    output.WriteLine("  [skip]   " + h.Name + " (not detected on this machine)");
                    continue;
                }

                try { output.WriteLine("  [ok]     " + h.Name + ": " + McpConfigInstaller.Apply(h.ConfigPath, exe, ver, h.Style, full)); done++; }
                catch (Exception ex) { diagnostics.WriteLine("  [failed] " + h.Name + ": " + ex.Message); failed++; }
            }

            output.WriteLine(done > 0
                ? $"Configured {done} host(s) for TIA {TiaMcp.Versioning.TiaVersionCatalog.Get(ver).DisplayName} -> {exe}{(full ? " [full profile: all tools — exceeds VS Code/Copilot's 128 and Windsurf's 100 tool cap]" : " [default lite profile: ~48 core tools; the rest stay reachable via FindTools/CallTool]")}. Restart the AI client to load it. (original config backed up as *.bak)"
                : "No host config written. Targeted host not found, or use `config --print` to copy the snippet manually.");
            output.WriteLine("For other hosts, run `config --print` and paste the matching snippet.");
            return failed > 0 && done == 0 ? 2 : 0;
        }

        private static int DoctorCli(string[] args, HostDoctorProbe probe) => Doctor(args, probe, Console.Out, Console.Error);

        // Without --json the report goes to the output unchanged. With --json the same report lines, the check rows and the
        // group repair outcome go into one JSON object instead; the exit code is the same in both modes.
        internal static int Doctor(string[] args, IHostDoctorProbe probe, TextWriter output, TextWriter errors)
        {
            bool fix = Flag(args, "--fix");
            bool json = Flag(args, "--json");
            bool zh = Runtime.EnvironmentDoctor.PreferChinese;
            using var captured = new StringWriter();
            TextWriter console = json ? captured : output;
            var rows = new JsonArray();
            var clients = new JsonArray();
            string? logs = null, diagnostics = null;

            console.WriteLine(zh
                ? "tia doctor —— 环境体检" + (fix ? "（修复模式）" : "（只读；加 --fix 可自动把当前用户加入 Openness 组）")
                : "tia doctor — environment check" + (fix ? " (fix mode)" : " (read-only; pass --fix to auto-add the Openness group)"));

            bool ready = true;
            try
            {
                logs = probe.LogDirectory();
                console.WriteLine("Logs: " + logs);
                diagnostics = probe.DiagnosticsDirectory();
                console.WriteLine("Diagnostics: " + diagnostics);
            }
            catch (IOException error) { errors.WriteLine(error.Message); ready = false; }

            void Line(string id, bool gating, bool ok, string name, string detail, string? fixHint)
            {
                console.WriteLine($"  [{(ok ? " ok " : "FAIL")}] {name}: {detail}");
                if (!ok && !string.IsNullOrEmpty(fixHint))
                    console.WriteLine($"         {(zh ? "修法" : "fix")}: {fixHint}");
                rows.Add(new JsonObject { ["id"] = id, ["ok"] = ok, ["gating"] = gating, ["name"] = name, ["detail"] = detail,
                    ["fix"] = !ok && !string.IsNullOrEmpty(fixHint) ? fixHint : null });
            }

            var detected = probe.DetectedMajor;
            int compiled = TiaMcp.Versioning.TiaVersionCatalog.Get(probe.ReleaseKey).MajorVersion;

            int effective = compiled;
            bool supported = TiaMcp.Versioning.TiaVersionCatalog.Runnable.Any(v => v.MajorVersion == effective)
                && (!detected.HasValue || TiaMcp.Versioning.TiaVersionCatalog.Runnable.Any(v => v.MajorVersion == detected.Value));
            string selectedName = TiaMcp.Versioning.TiaVersionCatalog.Get(probe.ReleaseKey).DisplayName;
            Line("release-support", true, supported, "Engine version support", "Selected " + selectedName + "; detected " +
                (detected.HasValue ? detected.Value == effective ? selectedName : "V" + detected.Value : "unknown"),
                "All eight releases use FoundationHost; select a supported --release-key.");
            ready &= supported;

            foreach (var c in probe.Checks())
            {
                Line(c.Id, c.Gating, c.Ok, c.Name(zh), c.Detail(zh), c.Fix(zh));
                if (c.Gating) ready &= c.Ok;
            }

            // Openness group is the one check that can also repair itself, so it stays here rather
            // than in the shared read-only set.
            bool groupOk; string groupDetail; string? failure = null;
            try
            {
                groupOk = fix
                    ? probe.Group(true)
                    : probe.Group(false);
                groupDetail = groupOk
                    ? (zh ? "当前用户已在 'Siemens TIA Openness' 组" : "current user is in 'Siemens TIA Openness'")
                    : (zh ? "当前用户不在 'Siemens TIA Openness' 组" : "current user NOT in 'Siemens TIA Openness'");
            }
            catch (Exception ex)
            {
                groupOk = false;
                failure = ex.Message;
                groupDetail = (zh ? "检查失败：" : "check failed: ") + ex.Message;
            }
            Line("openness-group", true, groupOk, zh ? "Openness 用户组" : "Openness user group", groupDetail,
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
                console.WriteLine($"  [{mark}] {(zh ? "AI 客户端配置" : "AI host config")} — {h.Name}: {state}");
                if (present && !exeOk)
                    console.WriteLine("         " + (zh
                        ? $"修法: 运行 `tia config --host {h.Name.Split(' ')[0].ToLowerInvariant()}` 重新指向本交付包的引擎。"
                        : $"fix: run `tia config --host {h.Name.Split(' ')[0].ToLowerInvariant()}` to repoint it at this bundle's engine."));
                clients.Add(new JsonObject { ["name"] = h.Name, ["registered"] = present, ["command"] = cmd, ["commandExists"] = exeOk });
            }
            console.WriteLine(zh
                ? "         （一次性写入所有检测到的客户端：tia config）"
                : "         (register into all detected hosts with: tia config)");

            console.WriteLine(ready
                ? (zh ? "READY —— 环境正常。下一步：重启 AI 客户端，让它调用 InitializeEnvironment。"
                      : "READY — environment OK. Next: restart your AI client and ask it to call InitializeEnvironment.")
                : (zh ? "NOT READY —— 请按上面的『修法』处理 FAIL 项，然后重新运行 tia doctor。"
                      : "NOT READY — fix the FAIL items above, then run `tia doctor` again."));
            if (json)
            {
                var (outcome, code) = HostDoctorGroupOutcome.Classify(fix, groupOk, failure, probe.FixReportsOutcome);
                var report = captured.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
                if (report.Count > 0 && report[^1].Length == 0) report.RemoveAt(report.Count - 1);
                output.WriteLine(new JsonObject
                {
                    ["releaseKey"] = probe.ReleaseKey, ["fix"] = fix, ["ready"] = ready, ["logs"] = logs, ["diagnostics"] = diagnostics,
                    ["checks"] = rows,
                    ["group"] = new JsonObject { ["ok"] = groupOk, ["outcome"] = outcome, ["code"] = code, ["message"] = failure },
                    ["clients"] = clients,
                    ["report"] = new JsonArray(report.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray()),
                }.ToJsonString());
            }
            return ready ? 0 : 2;
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

        internal const string UsageText =
@"tia — TIA Portal host diagnostics and local setup (all eight releases).

USAGE
  TiaMcp.FoundationHost.exe <verb> [options] [--release-key KEY]
  tia doctor [--fix] [--json]                             Inspect installation, API, worker readiness,
                                                          .NET Framework, blocked files, group and AI host configs
  tia audit verify [--path ABSOLUTE_DIRECTORY]              Verify audit chains; JSON, exit 0 intact / 3 broken
  tia schema                                              Print the legacy spec field reference
  tia version
  tia config [--host claude|claude-code|cursor|vscode|codex|gemini|windsurf|cline] [--print] [--full]
                                                          Register the selected FoundationHost in AI clients
  tia install-plc-tools [--python PATH] [--environment-path DIR]
                                                          Install companion dependencies for Python 3.12+
  tia help

RELEASES: 14sp1, 15.1, 16, 17, 18, 19, 20, 21. Original V14/V15 are unsupported.
  --tia-version KEY and --tia-major-version N are aliases for --release-key.
  The packaged release-key.txt selects the default; development output defaults to 21.
  --bundle-root ABSOLUTE_PATH overrides TIA_MCP_BUNDLE_ROOT and installation anchors.
  doctor accepts --tia-portal-location PATH, --public-api PATH, --worker-exe PATH,
  --plc-worker and the worker timeout flags. --fix may prompt UAC; sign out/in afterwards.
  doctor --json prints one JSON object instead of the report: ready, the checks, the group repair
  outcome and the report lines; the exit codes do not change.
  help, version, schema, audit, config and install-plc-tools need no TIA installation.

RETIRED PROJECT COMMANDS
  gen / patch -> project generation framework (PlanProjectGeneration / ApplyGenerationPlan)
  describe -> GetProjectTree; compile -> CompilePlcDiagnostics
  export / import -> matching PLC/HMI export/import MCP tools (FindTools / GetToolUsage)
  prewarm -> ConnectPortal. The engine keeps these verbs temporarily and writes guidance to stderr.
  schema describes the legacy spec; use the generation framework for new projects.

EXIT CODES: 0 success; 2 rejected / doctor not ready / no successful config writes;
  3 failed; 4 partial; 5 unknown; 64 syntax; 70 unavailable runtime resources.
  install-plc-tools preserves pip's nonzero exit status.
  The host subset never changes a TIA project. MCP project operations retain their V4 results.

" + HostOptions.WorkerTimeoutHelp;

        private const string SchemaText =
@"PROJECT SPEC (YAML or JSON). JSON is canonical; YAML is for humans.
Used by `tia gen` (build from zero) and `tia patch` (upsert into existing).

  projectName     string  gen: required. Project name.
  projectPath     string  patch: required. Path to the .apXX to open.
  directoryPath   string  gen: output folder (default data/temp; per-user temp fallback).
  plcName         string  default PLC_1.
  plcFamily       string  label only; no device is chosen from it.
  plcMlfb         string  gen: required. Exact order number or full TypeIdentifier.
  plcVersion      string  exact catalog version; empty with a full TypeIdentifier.
  hmiName         string  omit to skip all HMI.
  hmiFamily       string  label only; no device is chosen from it.
  hmiMlfb         string  gen: required with hmiName; same rules as plcMlfb.
  hmiVersion      string  same rules as plcVersion.
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
