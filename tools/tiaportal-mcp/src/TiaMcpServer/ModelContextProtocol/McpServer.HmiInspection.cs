using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ArchiveSavedProject"),Description("[L2][Project][FILE-WRITE]Create a native compressed .zap20/.zap21 archive of an already saved project. Default dryRun=true. Rejects unsaved state, unsupported sessions and existing target files. Does not save, change project path, close the project, or test retrieval.")]
        public static ResponseMessage ArchiveSavedProject(string archivePath,bool dryRun=true)
            =>Portal.ArchiveSavedProject(archivePath,dryRun);
        [McpServerTool(Name="ReadUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified]Read an EXISTING button event's script, global definitions, Async and content token. Never creates events. screenPath is a unique name or /Group/Screen from ListHmiScreenPaths.")]
        public static ResponseMessage ReadUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType)
            =>Portal.ReadUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType);

        [McpServerTool(Name="DeleteUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified][WRITE]Delete exactly one button event. Default dryRun=true; real deletion requires a matching expectedToken from read/preview. onlyIfEmpty defaults true. No save, no other event is targeted.")]
        public static ResponseMessage DeleteUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType,bool dryRun=true,string expectedToken="",bool onlyIfEmpty=true)
            =>Portal.DeleteUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType,dryRun,expectedToken,onlyIfEmpty);

        [McpServerTool(Name="ReadUnifiedHmiDynamization"),Description("[L2][HMI-Unified]Read exactly one screen item dynamization by PropertyName. Returns a bounded snapshot with coverage and a token; never creates objects.")]
        public static ResponseMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
            =>Portal.ReadUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName);

        [McpServerTool(Name="DeleteUnifiedHmiDynamization"),Description("[L2][HMI-Unified][WRITE]Delete exactly one property dynamization; default preview only. Real deletion requires expectedToken and complete readback. Does not save or delete events.")]
        public static ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            =>Portal.DeleteUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName,dryRun,expectedToken);

        [McpServerTool(Name="DeleteEmptyUnifiedHmiScreenGroup"),Description("[L2][HMI-Unified][WRITE]Delete an empty screen group by absolute /Group/Subgroup path. Default preview only. Nonempty, ambiguous or root targets are rejected; no recursive deletion or save.")]
        public static ResponseMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
            =>Portal.DeleteEmptyUnifiedHmiScreenGroup(softwarePath,groupPath,dryRun);

        [McpServerTool(Name="ReadHmiScreenSnapshot"),Description("[L2][HMI]Read a bounded screen object graph with per-node status, paths, limits and coverage. Not a restorable backup. maxDepth 1..12; maxNodes 1..10000. Large responses use the existing GetExport mechanism.")]
        public static ResponseMessage ReadHmiScreenSnapshot(string softwarePath,string screenPath,int maxDepth=6,int maxNodes=2000)
            =>Portal.ReadHmiScreenSnapshot(softwarePath,screenPath,maxDepth,maxNodes);

        [McpServerTool(Name="ListHmiScreenPaths"),Description("[L2][HMI]List full screen group paths, including nested folders. Paths start with / and URI-escape each name segment. offset/limit paginate the list; exact paths disambiguate same-named screens.")]
        public static ResponseMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100)
            =>Portal.ListHmiScreenPaths(softwarePath,offset,limit);
    }
}
