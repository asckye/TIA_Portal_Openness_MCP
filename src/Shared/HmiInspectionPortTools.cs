using TiaMcp.Logic.V4.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    // Only this inspection/reflection group uses this response adapter. Input
    // admission belongs to the shared catalog; native work stays in the services.


    [McpServerToolType]
    internal sealed class HmiInspectionPortTools
    {
        private readonly HmiInspectionPortService _hmiInspection;
        public HmiInspectionPortTools(HmiInspectionPortService service) => _hmiInspection = service;
        [McpServerTool(Name="GetHmiScreenSnapshot"),Description("[L2][HMI]Read a bounded screen object graph. Prefer absolute /Group/Screen paths to avoid a project-wide name search. apiCallSuccess and dataComplete are separate; inspect failures, quarantined getters and limits. Connection faults stop traversal and block further HMI step operations until an explicit successful AttachOpenProject; inspect logs first, do not automatically rebind/retry. Not a restorable backup. maxDepth 1..12; maxNodes 1..10000. Large responses use GetExportContent.")]
        public CallToolResult ReadHmiScreenSnapshotV4(
            string softwarePath,
            string screenPath,
            int maxDepth=6,
            [Description("maxNodes: cap on nodes returned.")] int maxNodes=2000)
            => HmiInspectionContract.Run("GetHmiScreenSnapshot", false, false, () => ReadHmiScreenSnapshot(softwarePath, screenPath, maxDepth, maxNodes));

        public ResponseMessage ReadHmiScreenSnapshot(
            string softwarePath,
            string screenPath,
            int maxDepth=6,
            [Description("maxNodes: cap on nodes returned.")] int maxNodes=2000)
        =>_hmiInspection.ReadHmiScreenSnapshot(softwarePath,screenPath,maxDepth,maxNodes);

        [McpServerTool(Name="ListHmiScreenPaths"),Description("[L2][HMI]List full screen group paths, including nested folders. Paths start with / and URI-escape each name segment. offset/limit paginate the list; exact paths disambiguate same-named screens.")]
        public CallToolResult ListHmiScreenPathsV4(string softwarePath,int offset=0,int limit=100)
            => HmiInspectionContract.Run("ListHmiScreenPaths", false, false, () => ListHmiScreenPaths(softwarePath, offset, limit), offset: offset, pageSize: limit);

        public ResponseMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100)
        =>_hmiInspection.ListHmiScreenPaths(softwarePath,offset,limit);
    }
}
