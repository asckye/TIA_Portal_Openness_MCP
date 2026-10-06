using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Doctor: one-call environment diagnosis for non-experts / fresh machines.
    // Ported from the pre-split repo where it shipped alongside the lite profile;
    // SKILL.md documents it, so the tool must exist in every released build.
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetEnvironmentDiagnostics"), Description("[L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem. When fix=true (default) it ENSURES Openness group membership (adds the current user; may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready.")]
        public static Task<CallToolResult> GetEnvironmentDiagnosticsV4(
            [Description("fix: when true (default), ensure Openness group membership (adds user, may prompt UAC). false = read-only diagnosis, no prompt.")] bool fix = true)
            => SessionToolContract.RunAsync("GetEnvironmentDiagnostics", fix, false, () => Doctor(fix));

        public static async Task<ResponseDoctor> Doctor(
            [Description("fix: when true (default), ensure Openness group membership (adds user, may prompt UAC). false = read-only diagnosis, no prompt.")] bool fix = true)
        {
            try
            {
                var checks = new List<DoctorCheck>();

                // 1) Environment prerequisites, shared with `tia doctor` so the two cannot drift.
                //    Covers TIA install, Openness assembly resolution (TIA can be installed WITHOUT
                //    Openness — the old check said OK and the engine then died on first call),
                //    engine/TIA version match, .NET Framework 4.8, and Windows MOTW blocking.
                int? inUse = Engineering.TiaMajorVersion == 0 ? (int?)null : Engineering.TiaMajorVersion;
                int? detected = Engineering.DetectTiaMajorVersion();
                foreach (var c in Runtime.EnvironmentDoctor.Run(EngineRouter.CompiledTiaMajorVersion, inUse ?? detected))
                {
                    checks.Add(new DoctorCheck
                    {
                        Name = c.NameEn,
                        Ok = c.Ok,
                        Detail = c.DetailEn,
                        Fix = c.FixEn
                    });
                }
                bool envOk = checks.All(c => c.Ok);
                string? firstEnvProblem = checks.FirstOrDefault(c => !c.Ok)?.Name;
                bool isolatedParent = Isolation.IsolatedWorkerHost.Current != null && !Isolation.IsolatedWorkerHost.IsChild;

                // 2) Openness group membership (+ optional auto-fix)
                bool groupOk;
                if (!Runtime.OpennessReadiness.Ready)
                {
                    groupOk = Runtime.OpennessReadiness.GroupOk == true;
                    checks.Add(new DoctorCheck
                    {
                        Name = "TIA Openness startup",
                        Ok = false,
                        Detail = Runtime.OpennessReadiness.Cause,
                        Fix = Runtime.OpennessReadiness.FixEn
                    });
                }
                else if (isolatedParent)
                {
                    groupOk = Runtime.EnvironmentDoctor.CurrentUserInOpennessGroup();
                }
                else if (fix)
                {
                    try { groupOk = await Siemens.Openness.IsUserInGroup(); }
                    catch /* swallow(env-probe): a failed membership check or repair is reported as groupOk=false */ { groupOk = false; }
                }
                else
                {
                    try { groupOk = Siemens.Openness.IsUserInGroupNoFix(); }
                    catch /* swallow(env-probe): unavailable membership information is reported as groupOk=false */ { groupOk = false; }
                }
                checks.Add(new DoctorCheck
                {
                    Name = "Openness user group",
                    Ok = groupOk,
                    Detail = groupOk ? "current user is in 'Siemens TIA Openness' group" : "current user NOT in 'Siemens TIA Openness' group",
                    Fix = groupOk ? null : "Run GetEnvironmentDiagnostics with fix=true (prompts UAC to add you), or manually add your Windows user to the 'Siemens TIA Openness' local group and sign out/in. Admin rights required."
                });

                if (isolatedParent && Runtime.OpennessReadiness.Ready)
                {
                    var failedPrerequisite = checks.FirstOrDefault(check => !check.Ok && check.Name != "Openness user group"
                        && check.Name != "TIA connection / project");
                    if (failedPrerequisite != null)
                    {
                        var source = Runtime.EnvironmentDoctor.Run(EngineRouter.CompiledTiaMajorVersion, inUse ?? detected)
                            .FirstOrDefault(check => !check.Ok);
                        var cause = source?.DetailEn ?? failedPrerequisite.Detail ?? "TIA Openness environment is not ready.";
                        var repair = source?.FixEn ?? failedPrerequisite.Fix ?? "Run `tia doctor` for repair steps.";
                        Runtime.OpennessReadiness.MarkUnavailable(cause, repair,
                            source?.FixZh ?? source?.FixEn ?? repair, groupOk);
                    }
                    else if (!groupOk)
                    {
                        const string cause = "Current user is not in the required Siemens TIA Openness group.";
                        const string repair = "Add the current Windows user to the local 'Siemens TIA Openness' group, sign out and back in, then restart the MCP client.";
                        Runtime.OpennessReadiness.MarkUnavailable(cause, repair, repair, false);
                    }
                    else Runtime.OpennessReadiness.MarkReady(true);
                }

                // 3) Connection + project state
                bool connected = false; string? projectName = null;
                try { if (Runtime.OpennessReadiness.Ready) { var st = EngineServices.Get<Siemens.Portal>().GetState(); connected = st?.IsConnected ?? false; projectName = st?.Project; } }
                catch { /* swallow(probe-optional): unavailable session state keeps the disconnected diagnostic */ }
                bool hasProject = !string.IsNullOrWhiteSpace(projectName) && projectName != "-";
                checks.Add(new DoctorCheck
                {
                    Name = "TIA connection / project",
                    Ok = connected,
                    Detail = connected ? (hasProject ? $"connected, project '{projectName}' open" : "connected, no project bound") : "not connected",
                    Fix = connected ? (hasProject ? null : "Call AttachOpenProject (if a project is open in TIA UI) or OpenProject/CreateProject.") : "Call ConnectPortal (first call may pop an Openness authorization dialog in TIA — click Yes)."
                });

                string next;
                if (!envOk) next = $"(fix first: {firstEnvProblem})";
                else if (!groupOk) next = "EnsureOpennessUserGroup";
                else if (!connected) next = "ConnectPortal";
                else if (!hasProject) next = "AttachOpenProject";
                else next = "GetProjectTree";

                bool ready = envOk && groupOk;
                var failed = checks.Where(c => !c.Ok).Select(c => c.Name).ToList();
                string summary = ready && connected && hasProject
                    ? "Environment healthy — project open, ready to work."
                    : ready
                        ? "Environment OK — connect/open a project next."
                        : $"Not ready. Fix: {string.Join("; ", failed)}.";

                return new ResponseDoctor
                {
                    Ready = ready,
                    Checks = checks,
                    RecommendedNextTool = next,
                    Summary = summary,
                    Message = summary,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetEnvironmentDiagnostics unexpected error: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
