using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class RuntimeSettingsService : UnifiedPortService
    {
        public RuntimeSettingsService() { }
        internal RuntimeSettingsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadUnifiedRuntimeSettings(string softwarePath, string expectedProject, string fieldsJson = "[\"StartScreen\",\"ScreenResolution\"]")
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedRuntimeSettings", SoftwarePath = softwarePath, ExpectedProject = expectedProject, FieldsJson = fieldsJson }, true);
        public ResponseMessage UpdateUnifiedRuntimeSettings(string softwarePath, string expectedProject, string changesJson, bool dryRun = true, string expectedToken = "")
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedRuntimeSettings", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ChangesJson = changesJson, DryRun = dryRun, ExpectedToken = expectedToken }, dryRun);
    }
}
