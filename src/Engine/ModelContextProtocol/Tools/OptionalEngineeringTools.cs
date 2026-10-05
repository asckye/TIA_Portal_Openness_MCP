using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class OptionalEngineeringTools
    {
        private readonly OptionalEngineeringService _optionalEngineering;

        public OptionalEngineeringTools(OptionalEngineeringService optionalEngineering) => _optionalEngineering = optionalEngineering;

        [McpServerTool(Name="ListSivarcRules"), Description("[L2][HMI][READ] Exact SiVArc rule category and property-only path; live scalar pagination with schema, complex values excluded. Native behaviorPolicy=current; V4 native acceptance is pending. No generation.")]
        public CallToolResult ListSivarcRules(string category, PropertyStep[] objectPath = null!, int offset=0, int limit=100)
            => OptionalPackageContract.Run("ListSivarcRules", false, () =>
            {
                string path = V4Json.Serialize(objectPath ?? Array.Empty<PropertyStep>());
                EngineeringObjectAddress.Parse(path);
                return _optionalEngineering.ReadSiVArcRules(category, path, offset, limit);
            }, offset, limit);
        [McpServerTool(Name="ManageSivarcRule"), Description("[L2][HMI][WRITE] Native SiVArc rule/folder/table composition create/update/delete with exact collection path and name. Nonempty container deletion refused. Native behaviorPolicy=current; V4 native acceptance is pending. Default preview; no generation/save/compile/download.")]
        public CallToolResult ManageSivarcRule(
            string category,
            [Description("Exact property/name steps to the rule collection; index selection is not supported.")] PropertyStep[] collectionPath,
            string name,
            [Description("create | update | delete. ")] string action,
            AttributeMap<Scalar> properties = null!,
            bool dryRun=true)
            => OptionalPackageContract.Run("ManageSivarcRule", !dryRun, () =>
            {
                string path = V4Json.Serialize(collectionPath);
                EngineeringObjectAddress.Parse(path);
                string changes = V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));
                return _optionalEngineering.ManageSiVArcRule(category, path, name, action, changes, dryRun);
            });
    }
}
