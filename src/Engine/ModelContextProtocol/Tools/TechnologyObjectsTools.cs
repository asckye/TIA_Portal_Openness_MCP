using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class TechnologyObjectsTools
    {
        private readonly TechnologyObjectsService _service;

        public TechnologyObjectsTools(TechnologyObjectsService service) => _service = service;

        [McpServerTool(Name = "ListTechnologyObjects"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:ConnectPortal+OpenProject]" +
            " List all Technology Objects (TOs) in the PLC software: axes, cams, measuring inputs, etc. - root group AND user folders (Folder = '' for the root)." +
            " Returns each TO's Name, type (OfSystemLibElement), and firmware version (OfSystemLibVersion)." +
            " Use this to discover TO names before ExportTechnologyObject." +
            " No tool returns axis/TO parameter values directly — export the TO with ExportTechnologyObject and read the XML file." +
            " TOs are stored as TechnologicalInstanceDB instances in the TechnologicalObjectGroup. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetTechnologyObjectsV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
            => EngineeringToolContract.Run("ListTechnologyObjects", false, () => GetTechnologyObjects(softwarePath));

        public ResponseTechnologyObjectList GetTechnologyObjects(
            string softwarePath)
        {
            try
            {
                var items = _service.GetTechnologyObjects(softwarePath);
                var typed = items.Select(jo => new TechnologyObjectInfo
                {
                    Name = jo["Name"]?.GetValue<string>(),
                    OfSystemLibElement = jo["OfSystemLibElement"]?.GetValue<string>(),
                    OfSystemLibVersion = jo["OfSystemLibVersion"]?.GetValue<string>(),
                    TypeHint = jo["TypeHint"]?.GetValue<string>(),
                    Folder = jo["Folder"]?.GetValue<string>(),
                }).ToArray();

                return new ResponseTechnologyObjectList
                {
                    Ok = true,
                    SoftwarePath = softwarePath,
                    Count = typed.Length,
                    Items = typed,
                    Message = $"{typed.Length} technology object(s) found in '{softwarePath}'."
                };
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing technology objects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTechnologyObject"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:ConnectPortal+OpenProject]" +
            " Export a single Technology Object (axis, cam, measuring input, etc.) to an XML file." +
            " The XML can be inspected, modified offline, and re-imported with ImportTechnologyObject." +
            " Use ListTechnologyObjects first to confirm the exact TO name. Preview by default; inconsistent objects are refused in preview and apply. Compile first. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportTechnologyObjectV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("toName: exact name of the technology object, e.g. 'Axis_1'")] string toName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\Axis_1.xml'")] string exportPath,
            [Description("Preview consistency and destination before export; false executes.")] bool dryRun = true)
            => EngineeringToolContract.Run("ExportTechnologyObject", !dryRun, () => _service.ExportTechnologyObject(softwarePath, toName, exportPath, dryRun));

        public ResponseMessage ExportTechnologyObject(
            string softwarePath,
            string toName,
            string exportPath)
        {
            try { return _service.ExportTechnologyObject(softwarePath, toName, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting technology object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

    }
}
