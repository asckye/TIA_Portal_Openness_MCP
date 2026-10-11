using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectSecurityTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectSecurityTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name="ManageMultiuserSession"), Description("[L2][Project][WRITE] Multiuser/project-server access by exact alias: read (servers, open local sessions, bound session up-to-date flag and markings), listServerProjects, readLockState, listLocalSessions (serverName + projectName), connectServer (serverName, protocol Https/Http, host, port), disconnectServer, commit (CloseAndCommit with commitComment; closes the bound session). Mutations default to preview and need dryRun=false AND confirmChange=true. Never opens/creates local sessions or saves implicitly.")]
        public CallToolResult ManageMultiuserSession(
            [Description("read | listServerProjects | readLockState | listLocalSessions | connectServer | disconnectServer | commit. ")] string action="read",
            [Description("serverName: multiuser server name.")] string serverName="",
            [Description("projectName: exact project name.")] string projectName="",
            [Description("protocol: Https or Http.")] string protocol="Https",
            [Description("host: server host name or IP address.")] string host="",
            [Description("port: TCP port (0 = default).")] int port=0,
            [Description("commitComment: comment text of the commit.")] string commitComment="",
            [Description("offset: zero-based starting row for bound-session markings, server projects or local sessions; must be non-negative.")] int offset=0,
            [Description("limit: maximum page size for bound-session markings, server projects or local sessions (1..500, default 100).")] int limit=100,
            [Description("confirmChange: true is required together with dryRun=false to connect/disconnect a server or commit the bound session.")] bool confirmChange=false,
            [Description("dryRun: true previews connectServer, disconnectServer and commit; false executes those mutations only with confirmChange=true. Read actions remain read-only.")] bool dryRun=true)
            => SecurityToolContract.Invoke("ManageMultiuserSession", dryRun || action == "read" || action == "listServerProjects" || action == "readLockState" || action == "listLocalSessions", action == "commit",
                () => _service.ManageMultiuserSession(action,serverName,projectName,protocol,host,port,commitComment,offset,limit,confirmChange,dryRun));
}
