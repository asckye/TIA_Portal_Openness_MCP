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
    internal sealed class HmiInspectionTools
    {
        private readonly HmiInspectionService _hmiInspection;

        public HmiInspectionTools(HmiInspectionService service) => _hmiInspection = service;

        [McpServerTool(Name="ArchiveSavedProject"),Description("[L2][Project][FILE]Create a native compressed .zap20/.zap21 archive of an already saved project. Default dryRun=true. Rejects unsaved state, unsupported sessions and existing target files. Does not save, change project path, close the project, or test retrieval. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ArchiveSavedProjectV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
            => HmiInspectionContract.Run("ArchiveSavedProject", !dryRun, true, () => ArchiveSavedProject(archivePath, dryRun));

        public ResponseMessage ArchiveSavedProject(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
        =>_hmiInspection.ArchiveSavedProject(archivePath,dryRun);
        [McpServerTool(Name="GetUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified]Read an EXISTING button event's script, global definitions, Async and content token. Never creates events. screenPath is a unique name or /Group/Screen from ListHmiScreenPaths.")]
        public CallToolResult ReadUnifiedHmiButtonEventV4(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType)
            => HmiInspectionContract.Run("GetUnifiedHmiButtonEvent", false, false, () => ReadUnifiedHmiButtonEvent(softwarePath, screenPath, buttonName, eventType));

        public ResponseMessage ReadUnifiedHmiButtonEvent(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType)
        =>_hmiInspection.ReadUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType);

        [McpServerTool(Name="DeleteUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified][WRITE]Delete exactly one button event. Default dryRun=true; real deletion requires a matching expectedToken from read/preview. onlyIfEmpty defaults true. No save, no other event is targeted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteUnifiedHmiButtonEventV4(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType,
            bool dryRun=true,
            string expectedToken="",
            [Description("onlyIfEmpty: true deletes the event only when it has no script.")] bool onlyIfEmpty=true)
            => HmiInspectionContract.Run("DeleteUnifiedHmiButtonEvent", !dryRun, true, () => DeleteUnifiedHmiButtonEvent(softwarePath, screenPath, buttonName, eventType, dryRun, expectedToken, onlyIfEmpty));

        public ResponseMessage DeleteUnifiedHmiButtonEvent(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType,
            bool dryRun=true,
            string expectedToken="",
            [Description("onlyIfEmpty: true deletes the event only when it has no script.")] bool onlyIfEmpty=true)
        =>_hmiInspection.DeleteUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType,dryRun,expectedToken,onlyIfEmpty);

        [McpServerTool(Name="GetUnifiedHmiDynamization"),Description("[L2][HMI-Unified]Read exactly one screen item dynamization by PropertyName. Returns a bounded snapshot with coverage and a token; never creates objects.")]
        public CallToolResult ReadUnifiedHmiDynamizationV4(string softwarePath,string screenPath,string itemName,string propertyName)
            => HmiInspectionContract.Run("GetUnifiedHmiDynamization", false, false, () => ReadUnifiedHmiDynamization(softwarePath, screenPath, itemName, propertyName));

        public ResponseMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
        =>_hmiInspection.ReadUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName);

        [McpServerTool(Name="DeleteUnifiedHmiDynamization"),Description("[L2][HMI-Unified][WRITE]Delete exactly one property dynamization; default preview only. Real deletion requires expectedToken and complete readback. Does not save or delete events. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteUnifiedHmiDynamizationV4(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => HmiInspectionContract.Run("DeleteUnifiedHmiDynamization", !dryRun, true, () => DeleteUnifiedHmiDynamization(softwarePath, screenPath, itemName, propertyName, dryRun, expectedToken));

        public ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
        =>_hmiInspection.DeleteUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName,dryRun,expectedToken);

        [McpServerTool(Name="DeleteEmptyUnifiedHmiScreenGroup"),Description("[L2][HMI-Unified][WRITE]Delete an empty screen group by absolute /Group/Subgroup path. Default preview only. Nonempty, ambiguous or root targets are rejected; no recursive deletion or save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteEmptyUnifiedHmiScreenGroupV4(string softwarePath,string groupPath,bool dryRun=true)
            => HmiInspectionContract.Run("DeleteEmptyUnifiedHmiScreenGroup", !dryRun, true, () => DeleteEmptyUnifiedHmiScreenGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
        =>_hmiInspection.DeleteEmptyUnifiedHmiScreenGroup(softwarePath,groupPath,dryRun);

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
