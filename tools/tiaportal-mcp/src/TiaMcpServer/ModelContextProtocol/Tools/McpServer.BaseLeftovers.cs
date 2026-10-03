using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseMessage ReadPortalInfo(
            bool includeProcesses=true,
            bool includeSessions=true,
            bool includeProducts=true)
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).ReadPortalInfo(includeProcesses, includeSessions, includeProducts);

        public static ResponseMessage ReadObjectIdentifier(
            string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            string identifier="")
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).ReadObjectIdentifier(kind, devicePathJson, itemPathJson, softwarePath, objectPath, identifier);

        public static ResponseMessage ShowObjectInEditor(
            string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).ShowObjectInEditor(kind, devicePathJson, itemPathJson, softwarePath, objectPath, dryRun);

        public static ResponseMessage RunToolsInTransaction(
            string callsJson,
            string text,
            bool confirmChange=false,
            bool dryRun=true)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).RunToolsInTransaction(callsJson, text, confirmChange, dryRun);
    }
}
