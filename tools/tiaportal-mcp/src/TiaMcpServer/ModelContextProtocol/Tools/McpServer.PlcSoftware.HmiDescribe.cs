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
    // Partial: plc software. Family file split out of McpServer.PlcSoftware.cs (2.8.0); behavior unchanged.
    public static partial class McpServer
    {
        #region plc software - HmiDescribe

        [McpServerTool(Name = "GetHmiProgramInfo"), Description("[L2][HMI] Get HMI software type (Classic/Basic/Unified), version, and list of all screen names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'HMI_RT_1'). Use to confirm HMI type before choosing Classic vs Unified tool variants.")]
        public static ResponseHmiProgramInfo GetHmiProgramInfo(
            [Description("softwarePath: path in the project structure to the HMI software (see GetProjectTree)")] string softwarePath)
        {
            try
            {
                var info = Portal.GetHmiProgramInfo(softwarePath);
                if (info != null)
                {
                    return new ResponseHmiProgramInfo
                    {
                        Message = $"HMI program info retrieved from '{softwarePath}'",
                        Name = info.Value.Name,
                        ProgramType = info.Value.ProgramType,
                        Screens = info.Value.Screens,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }

                throw new McpException($"HMI program not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving HMI program info from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiSoftware"), Description("[L2][HMI]Describe the HMI software object (members/methods) via reflection. Useful to discover Export/Import/Create APIs.")]
        public static ResponseObjectDescribe DescribeHmiSoftware(
            [Description("softwarePath: path in the project structure to the HMI software (e.g. 'HMI_RT_1')")] string softwarePath,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiSoftware(softwarePath, maxMembers);
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
                throw new McpException($"Unexpected error describing HMI software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiScreen"), Description("[L2][HMI]Describe one HMI screen object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiScreen(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreen(softwarePath, screenName, maxMembers);
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
                throw new McpException($"Unexpected error describing HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTagTable"), Description("[L2][HMI]Describe one HMI tag table object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiTagTable(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTagTable(softwarePath, tagTableName, maxMembers);
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
                throw new McpException($"Unexpected error describing HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTag"), Description("[L2][HMI]Describe one HMI tag object (members/methods) by name under an HMI tag table.")]
        public static ResponseObjectDescribe DescribeHmiTag(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("tagName: tag name")] string tagName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTag(softwarePath, tagTableName, tagName, maxMembers);
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
                throw new McpException($"Unexpected error describing HMI tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CompileAndDiagnoseHmi"), Description("[L1][HMI] Compile an HMI and return structured errors/warnings, the HMI counterpart of CompileAndDiagnosePlc. Use it after generating screens/tags so you can read the diagnostics and fix them yourself instead of asking the engineer to compile in the TIA UI. WinCC Unified: HmiSoftware is not compilable on its own, so the owning device is compiled (same as the TIA UI does) and hardware diagnostics may appear alongside screen ones. Classic (Comfort/KTP): the HMI software itself is compiled. Requires: Connect + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'.")]
        public static ResponseCompileDiagnose CompileAndDiagnoseHmi(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath)
            => CompileAndDiagnoseCore(softwarePath, "");

        [McpServerTool(Name = "DescribeHmiScreenItem"), Description("[L2][HMI]Describe one HMI screen item (widget) by name under an HMI screen.")]
        public static ResponseObjectDescribe DescribeHmiScreenItem(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("itemName: widget name, e.g. 'BTN_Start'")] string itemName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreenItem(softwarePath, screenName, itemName, maxMembers);
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
                throw new McpException($"Unexpected error describing HMI screen item '{itemName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
