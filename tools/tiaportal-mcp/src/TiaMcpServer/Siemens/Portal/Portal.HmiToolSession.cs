using System;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal : IHmiToolSession
    {
        private readonly MigrationPages migrationPages = new MigrationPages();

        object? IHmiToolSession.CurrentProject => CurrentProject;
        MigrationPages IHmiToolSession.MigrationPages => migrationPages;
        JsonObject? IHmiToolSession.HmiReadFault => _hmiReadFault;
        ResponseMessage IHmiToolSession.RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject)
            => RunHmiStepTool(toolName, action, requiresProject);
        IDisposable IHmiToolSession.AcquireHmiEditAccess() => AcquireHmiEditAccess();
        object IHmiToolSession.ResolveHmiSoftwareOrThrow(string hmiSoftwarePath) => ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
        void IHmiToolSession.RecordHmiReadFault(JsonObject meta) => RecordHmiReadFault(meta);
    }
}
