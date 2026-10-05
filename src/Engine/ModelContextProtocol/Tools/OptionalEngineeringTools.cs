using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class OptionalEngineeringTools
    {
        private readonly OptionalEngineeringService _optionalEngineering;

        public OptionalEngineeringTools(OptionalEngineeringService optionalEngineering) => _optionalEngineering = optionalEngineering;

        [McpServerTool(Name="ReadSiVArcRules"), Description("[L2][HMI][READ] Exact SiVArc rule category and property-only JSON path; live scalar pagination with schema, complex values excluded. No generation.")]
        public ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
            => _optionalEngineering.ReadSiVArcRules(category,objectPathJson,offset,limit);
        [McpServerTool(Name="ManageSiVArcRule"), Description("[L2][HMI][WRITE] Native SiVArc rule/folder/table composition create/update/delete with exact collection path and name. Nonempty container deletion refused. Default preview; no generation/save/compile/download.")]
        public ResponseMessage ManageSiVArcRule(
            string category,
            [Description("collectionPathJson: JSON array path of the rule collection.")] string collectionPathJson,
            string name,
            [Description("create | update | delete. ")] string action,
            string propertiesJson="{}",
            bool dryRun=true)
            => _optionalEngineering.ManageSiVArcRule(category,collectionPathJson,name,action,propertiesJson,dryRun);
    }
}
