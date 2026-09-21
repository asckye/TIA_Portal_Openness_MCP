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


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: plc software. 2.8.0 split by family into McpServer.PlcSoftware.<Family>.cs; behavior unchanged.
    public static partial class McpServer
    {
        #region plc software

        [McpServerTool(Name = "GetSoftwareInfo"), Description("[L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: Connect + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy.")]
        public static ResponseSoftwareInfo GetSoftwareInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var software = Portal.GetPlcSoftware(softwarePath);
                if (software != null)
                {

                    var attributes = Helper.GetAttributeList(software);

                    return new ResponseSoftwareInfo
                    {
                        Message = $"Software info retrieved from '{softwarePath}'",
                        Name = software.Name,
                        Attributes = attributes,
                        Description = software.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Software not found at '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software info from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeObjectProperty"), Description("[L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path.")]
        public static ResponseObjectDescribe DescribeObjectProperty(
            [Description("objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("objectPath: object path")] string objectPath,
            [Description("propertyPath: dotted property path, e.g. 'Connections' or 'PressedStateTags'")] string propertyPath,
            [Description("softwarePath: required for Block/Type")] string softwarePath = "",
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers);
                // success 原来写的是「成员表非空」。那是把**空**当成了**失败**：
                // 一个真实存在、但确实没有成员的对象会被报成 success=false，
                // 调用方于是去"修"一个根本没坏的东西。走到这一行就说明对象已经解析到了
                // （解析不到在 Portal 层就抛了），这就是成功；空不空看 memberCount。
                res.Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = true,
                    ["memberCount"] = res.Members?.Count() ?? 0
                };
                return res;
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing property '{propertyPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CompileSoftware"), Description("[L1][PLC-Software] Compile all blocks in the PLC software. Requires: Connect + OpenProject. Returns basic success/failure. For structured error/warning details use CompileAndDiagnosePlc instead. Must compile before ExportBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches.")]
        public static ResponseCompile CompileSoftware(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "")
        {
            try
            {
                var compileWatch = System.Diagnostics.Stopwatch.StartNew();
                var result = WithAutoOffline(() => Portal.CompileSoftware(softwarePath, password));
                var compileMs = compileWatch.ElapsedMilliseconds;
                var collected = CollectCompilerMessages(result.Messages);
                var summary = collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount);
                summary["compileElapsedMs"] = compileMs;
                summary["softwarePath"] = softwarePath;
                summary["timestamp"] = DateTime.Now;

                return new ResponseCompile
                {
                    Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; root counts and diagnostics scopes are in Meta.",
                    State = summary["effectiveState"]!.ToString(),
                    ErrorCount = collected.HasError && result.ErrorCount == 0 ? (int?)null : result.ErrorCount,
                    WarningCount = collected.HasWarning && result.WarningCount == 0 ? (int?)null : result.WarningCount,
                    Messages = collected.Raw,
                    Meta = summary
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "GetSoftwareTree"), Description("[L1][PLC-Software] Get the PLC user block/type hierarchy as ASCII tree; system block groups and external sources are excluded. Inspect meta.dataComplete for unreadable attributes. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportBlock/ImportBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy.")]
        public static ResponseSoftwareTree GetSoftwareTree(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var tree = Portal.GetSoftwareTree(softwarePath, out var metadata);

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseSoftwareTree
                    {
                        Message = $"Software tree retrieved from '{softwarePath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = metadata
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving software tree from '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software tree from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
