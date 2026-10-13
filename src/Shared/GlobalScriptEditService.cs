using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class GlobalScriptEditService : UnifiedPortService
    {
        public GlobalScriptEditService() { }
        internal GlobalScriptEditService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage UpdateUnifiedGlobalScript(string softwarePath, string expectedProject, string moduleName,
            string scriptCode, bool dryRun = true, string expectedToken = "")
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedGlobalScript", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ModuleName = moduleName, ScriptCode = scriptCode, DryRun = dryRun, ExpectedToken = expectedToken }, dryRun);
    }
}
