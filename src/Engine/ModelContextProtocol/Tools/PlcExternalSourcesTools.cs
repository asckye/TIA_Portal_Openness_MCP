using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcExternalSourcesTools
    {
        private readonly IEngineeringSession _session;
        private readonly PlcExternalSourcesService _domain;

        public PlcExternalSourcesTools(PlcExternalSourcesService domain, IEngineeringSession session)
        {
            _domain = domain;
            _session = session;
        }



        public ResponseStringList GetPlcExternalSources(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = _domain.GetPlcExternalSources(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC external sources listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC external sources: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }



        public ResponseMessage ImportPlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            try
            {
                _domain.ImportPlcExternalSource(softwarePath, groupPath, filePath);
                return new ResponseMessage
                {
                    Message = "PLC external source imported",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC external source [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public ResponseMessage DeletePlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from ListPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            try
            {
                _domain.DeletePlcExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"PLC external source '{externalSourceName}' deleted or was not present",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed deleting PLC external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error deleting PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public ResponseMessage GenerateBlocksFromExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from ListPlcExternalSources")] string externalSourceName)
        {
            try
            {
                _domain.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"Blocks generated from external source '{externalSourceName}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed generating blocks from external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating blocks from external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }







        [McpServerTool(Name = "ListPlcExternalSources"), Description("[L2][PLC-Software]List PLC external source names (best-effort)" + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GetPlcExternalSourcesV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            return PlcExchangeContract.Run("ListPlcExternalSources", () => GetPlcExternalSources(softwarePath), write: false, current: true);
        }



        [McpServerTool(Name = "ImportPlcExternalSource"), Description("[L2][PLC-Software]Import one PLC external source file into a group (best-effort)" + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportPlcExternalSourceV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            return PlcExchangeContract.Run("ImportPlcExternalSource", () => ImportPlcExternalSource(softwarePath, groupPath, filePath), write: true, current: true);
        }

        [McpServerTool(Name = "DeletePlcExternalSource"), Description("[L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeletePlcExternalSourceV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from ListPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            return PlcExchangeContract.Run("DeletePlcExternalSource", () => DeletePlcExternalSource(softwarePath, externalSourceName), write: true, current: true);
        }

        [McpServerTool(Name = "GenerateBlocksFromExternalSource"), Description("[L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort)" + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GenerateBlocksFromExternalSourceV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from ListPlcExternalSources")] string externalSourceName)
        {
            return PlcExchangeContract.Run("GenerateBlocksFromExternalSource", () => GenerateBlocksFromExternalSource(softwarePath, externalSourceName), write: true, current: true);
        }





    }
}
