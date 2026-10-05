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
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
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
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Export a single Technology Object (axis, cam, measuring input, etc.) to an XML file." +
            " The XML can be inspected, modified offline, and re-imported with ImportTechnologyObject." +
            " Use ListTechnologyObjects first to confirm the exact TO name. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportTechnologyObjectV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("toName: exact name of the technology object, e.g. 'Axis_1'")] string toName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\Axis_1.xml'")] string exportPath)
            => EngineeringToolContract.Run("ExportTechnologyObject", true, () => ExportTechnologyObject(softwarePath, toName, exportPath));

        public ResponseMessage ExportTechnologyObject(
            string softwarePath,
            string toName,
            string exportPath)
        {
            try { return _service.ExportTechnologyObject(softwarePath, toName, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting technology object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportTechnologyObjectsToDirectory"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Batch-export all (or regex-filtered) Technology Objects to XML files in a directory." +
            " Each TO is saved as '<TOName>.xml'. Returns lists of exported names and any failures." +
            " Use regexName to filter by TO name, e.g. 'Axis_.*' for all axes. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportTechnologyObjectsToDirectoryV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportDir: directory to write XML files to, e.g. 'C:\\Temp\\TOs'")] string exportDir,
            [Description("regexName: optional regex filter on TO name; empty = export all")] string regexName = "")
            => EngineeringToolContract.Run("ExportTechnologyObjectsToDirectory", true, () => ExportTechnologyObjectsToDirectory(softwarePath, exportDir, regexName));

        public ResponseImportBatch ExportTechnologyObjectsToDirectory(
            string softwarePath,
            string exportDir,
            string regexName = "")
        {
            try
            {
                var result = _service.ExportTechnologyObjectsToDirectory(softwarePath, exportDir, regexName);
                // Report the exported and failed counts even when the batch contains no objects.
                var exportedCount = result.Imported?.Count() ?? 0; var failedCount = result.Failed?.Count() ?? 0;
                result.Message = $"Exported {exportedCount} technology object(s) to '{exportDir}'. Failed={failedCount}" + (failedCount > 0 ? ": " + string.Join(" | ", result.Failed!.Take(5).Select(f => f.Path + " -> " + (f.Error ?? "").Split('\n')[0])) : "");
                // envelope: legacy-existing-meta
                result.Meta ??= new JsonObject { ["timestamp"] = DateTime.Now };
                result.Meta["success"] = failedCount == 0;
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error batch-exporting technology objects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportTechnologyObject"), Description("[L2][PLC-Software]Import one PLC Technology Object XML file into PLC software (best-effort) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportTechnologyObjectV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of Technology Object XML")] string importPath)
            => EngineeringToolContract.Run("ImportTechnologyObject", true, () => ImportTechnologyObject(softwarePath, folderPath, importPath));

        public ResponseMessage ImportTechnologyObject(
            string softwarePath,
            string folderPath,
            string importPath)
        {
            try
            {
                _service.ImportTechnologyObject(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"Technology object imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing technology object from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportTechnologyObjectsFromDirectory"), Description("[L2][PLC-Software]Batch import PLC technology object .xml files from a directory (best-effort) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportTechnologyObjectsFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing technology object XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
            => EngineeringToolContract.Run("ImportTechnologyObjectsFromDirectory", true, () => ImportTechnologyObjectsFromDirectory(softwarePath, folderPath, dir, regexName, overwrite));

        public ResponseImportBatch ImportTechnologyObjectsFromDirectory(
            string softwarePath,
            string folderPath,
            string dir,
            string regexName = "",
            bool overwrite = true)
        {
            try
            {
                var result = _service.ImportTechnologyObjectsFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} technology objects from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = result.Meta ?? ResponseMeta.Basic(DateTime.Now, (result.Failed == null || !result.Failed.Any()))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology objects from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name="GetTechnologyObjectTree"), Description("[L2][PLC-TechnologyObjects][READ] Typed read of the technology objects of one exact PLC: PlcSoftware.TechnologicalObjectGroup (or the user group at groupPath) as a TechnologicalInstanceDBGroup tree - Name, TechnologicalObjects (TechnologicalInstanceDB Name / Number / OfSystemLibElement / OfSystemLibVersion / IsConsistent / parameter count) and Groups recursive to maxDepth; includeParameters adds TechnologicalParameter Name / Value rows (first 500 per object); includeMotionView adds the typed Motion / Ident view of the root group's objects (actor / sensor / torque / encoder interfaces with modules, addresses, DB member paths, tags and channels; measuring input and output cam connections; master-value couplings; V21 interpreter TO / DB member mappings, superimposing axes and Ident device). No modification, no drive or motion command. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadTechnologyObjectTreeV4(
            string softwarePath,
            string groupPath="",
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            [Description("includeMotionView: true also returns the motion view of each object.")] bool includeMotionView=false,
            int maxDepth=4)
            => EngineeringToolContract.Run("GetTechnologyObjectTree", false, () => ReadTechnologyObjectTree(softwarePath, groupPath, includeParameters, includeMotionView, maxDepth));

        public ResponseMessage ReadTechnologyObjectTree(
            string softwarePath,
            string groupPath="",
            bool includeParameters=false,
            bool includeMotionView=false,
            int maxDepth=4)
            => _service.ReadTechnologyObjectTree(softwarePath,groupPath,includeParameters,includeMotionView,maxDepth);

        [McpServerTool(Name="ManageTechnologyObject"), Description("[L2][PLC-Software][WRITE] Native technology object read/create/delete/setParameter. Exact objectPath relative to TechnologicalObjectGroup, including user folders. create requires official typeIdentifier and version (official 'Overview of technology objects and versions': S7-1500 TO_PositioningAxis / TO_SpeedAxis / ... >= V5.0 with FW >= 2.8, PID_Compact >= V2.3; version as 'major.minor'). WARNING (real project, crash 9 in the handoff): Create(\"MCP_Axis\", \"TO_PositioningAxis\", 6.0) on a CPU 1515F-2 PN V2.9 in TIA V21 threw NonRecoverableException and TIA Portal exited, while TO_PositioningAxis / TO_SpeedAxis 5.0 and PID_Compact 2.3 were created normally on the same CPU - a version the CPU does not offer is not refused cleanly, so use the lowest version of the official table (S7-1500 motion 5.0, PID_Compact 2.3) and save the project before a create. setParameter takes exact parameter name and scalar value. dryRun=true default; writes require Offline. No save/compile/download; dependencies not analyzed. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageTechnologyObjectV4(
            string softwarePath,
            string objectPath,
            [Description("action: the operation to perform - read | create | delete | setParameter.")] string action,
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            string version="",
            [Description("parameter: exact parameter name.")] string parameter="",
            [Description("value: scalar parameter value (number, string, boolean or null); arrays and objects are refused.")] NativeValue value = default,
            bool dryRun=true)
            => EngineeringToolContract.Run("ManageTechnologyObject", !dryRun && action != "read" && action != "list", () => ManageTechnologyObject(softwarePath, objectPath, action, typeIdentifier, version, parameter, EngineeringToolContract.Value(value, action), dryRun));

        public ResponseMessage ManageTechnologyObject(
            string softwarePath,
            string objectPath,
            string action,
            string typeIdentifier="",
            string version="",
            string parameter="",
            string valueJson="null",
            bool dryRun=true)
            => _service.ManageTechnologyObject(softwarePath,objectPath,action,typeIdentifier,version,parameter,valueJson,dryRun);
    }
}
