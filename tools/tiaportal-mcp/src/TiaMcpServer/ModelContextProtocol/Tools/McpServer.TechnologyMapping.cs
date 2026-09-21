using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadTechnologyObjectTree"), Description("[L2][PLC-TechnologyObjects][READ] Typed read of the technology objects of one exact PLC: PlcSoftware.TechnologicalObjectGroup (or the user group at groupPath) as a TechnologicalInstanceDBGroup tree - Name, TechnologicalObjects (TechnologicalInstanceDB Name / Number / OfSystemLibElement / OfSystemLibVersion / IsConsistent / parameter count) and Groups recursive to maxDepth; includeParameters adds TechnologicalParameter Name / Value rows (first 500 per object); includeMotionView adds the typed Motion / Ident view of the root group's objects (actor / sensor / torque / encoder interfaces with modules, addresses, DB member paths, tags and channels; measuring input and output cam connections; master-value couplings; V21 interpreter TO / DB member mappings, superimposing axes and Ident device). No modification, no drive or motion command.")]
        public static ResponseMessage ReadTechnologyObjectTree(
            string softwarePath,
            string groupPath="",
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            [Description("includeMotionView: true also returns the motion view of each object.")] bool includeMotionView=false,
            int maxDepth=4)
            => Portal.ReadTechnologyObjectTree(softwarePath,groupPath,includeParameters,includeMotionView,maxDepth);
    }
}
