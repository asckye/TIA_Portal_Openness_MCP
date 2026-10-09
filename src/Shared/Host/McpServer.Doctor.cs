#if !TIA_ENGINE_PORTED
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcp.Logic.V4;
using TiaMcpServer.Siemens;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    // Doctor: one-call environment diagnosis for non-experts / fresh machines.
    // Ported from the pre-split repo where it shipped alongside the lite profile;
    // SKILL.md documents it, so the tool must exist in every released build.
    public static partial class McpServer
    {
        private sealed class DoctorResponseWithFixZh : ResponseDoctor
        {
            public string? RecommendedFixZh { get; set; }
        }

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
                string? firstEnvFixZh = null;
                foreach (var c in Runtime.EnvironmentDoctor.Run(TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).MajorVersion, inUse ?? detected))
                {
                    if (!c.Ok && firstEnvFixZh == null) firstEnvFixZh = c.FixZh;
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
                else if (fix)
                {
                    try { groupOk = await ReadOpennessGroup(fix: true); }
                    catch /* swallow(env-probe): a failed membership check or repair is reported as groupOk=false */ { groupOk = false; }
                }
                else
                {
                    try { groupOk = await ReadOpennessGroup(fix: false); }
                    catch /* swallow(env-probe): unavailable membership information is reported as groupOk=false */ { groupOk = false; }
                }
                checks.Add(new DoctorCheck
                {
                    Name = "Openness user group",
                    Ok = groupOk,
                    Detail = groupOk ? "current user is in 'Siemens TIA Openness' group" : "current user NOT in 'Siemens TIA Openness' group",
                    Fix = groupOk ? null : "Run GetEnvironmentDiagnostics with fix=true (prompts UAC to add you), or manually add your Windows user to the 'Siemens TIA Openness' local group and sign out/in. Admin rights required."
                });



                // 3) Connection + project state
                bool connected = false; string? projectName = null;
                try
                {
                    if (Runtime.OpennessReadiness.Ready)
                    {
                        var state = ReadPortalState();
                        connected = state.Connected;
                        projectName = state.ProjectName;
                    }
                }
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
                string? recommendedFixZh = ready ? null
                    : !Runtime.OpennessReadiness.Ready ? Runtime.OpennessReadiness.FixZh ?? Runtime.EnvironmentDoctor.DefaultFixZh
                    : !envOk ? firstEnvFixZh ?? Runtime.EnvironmentDoctor.DefaultFixZh
                    : !groupOk ? Runtime.EnvironmentDoctor.OpennessGroupFixZh
                    : Runtime.EnvironmentDoctor.DefaultFixZh;

                return new DoctorResponseWithFixZh
                {
                    Ready = ready,
                    Checks = checks,
                    RecommendedNextTool = next,
                    RecommendedFixZh = recommendedFixZh,
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

        // A no-inline boundary prevents the no-TIA diagnostic path from JIT-resolving Portal's
        // Siemens.Engineering field types. This helper runs only after readiness is established.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (bool Connected, string? ProjectName) ReadPortalState()
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<State>(HostToolServices.Observe("session.GetState", new JsonObject())!.ToJsonString());
            return (state?.IsConnected ?? false, state?.Project);
        }

        // Keep Siemens.Collaboration.Net references out of Doctor's no-TIA code path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task<bool> ReadOpennessGroup(bool fix)
        {
            return Task.FromResult((bool)HostToolServices.Observe(fix ? "group.fix" : "group", new JsonObject())!);
        }
    }
}

#endif
