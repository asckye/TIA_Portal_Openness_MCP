using System;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // The HMI tool boundary uses opaque handles so offline services need no Siemens SDK.
    internal interface IHmiToolSession
    {
        object? CurrentProject { get; }
        MigrationPages MigrationPages { get; }
        JsonObject? HmiReadFault { get; }
        ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject = true);
        IDisposable AcquireHmiEditAccess();
        object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath);
        void RecordHmiReadFault(JsonObject meta);
    }
}
