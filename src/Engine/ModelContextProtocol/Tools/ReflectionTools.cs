using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
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
using TiaMcpServer.Siemens;


using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ReflectionTools
    {
        private readonly ReflectionService _reflection;

        public ReflectionTools(ReflectionService service) => _reflection = service;

        [McpServerTool(Name = "DescribeObjectProperty"), Description("[L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path.")]
        public CallToolResult DescribeObjectPropertyV4(
            [Description("objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("objectPath: object path")] string objectPath,
            [Description("propertyPath: dotted property path, e.g. 'Connections' or 'PressedStateTags'")] string propertyPath,
            [Description("softwarePath: required for Block/Type")] string softwarePath = "",
            [Description("maxMembers: max member count")] int maxMembers = 200)
            => HmiInspectionContract.Run("DescribeObjectProperty", false, false, () => DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers));

        public ResponseObjectDescribe DescribeObjectProperty(
            [Description("objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("objectPath: object path")] string objectPath,
            [Description("propertyPath: dotted property path, e.g. 'Connections' or 'PressedStateTags'")] string propertyPath,
            [Description("softwarePath: required for Block/Type")] string softwarePath = "",
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = _reflection.DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers);
                // 对象解析成功即为成功；成员表可以为空，实际数量由 memberCount 表示。
                res.Meta = ResponseMeta.Basic(DateTime.Now, true, ("memberCount", res.Members?.Count() ?? 0));
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

        [McpServerTool(Name = "DescribeObject"), Description("[L2][Reflection]Describe an Openness object via reflection. Use this first when a natural-language TIA operation has no direct MCP tool. objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")]
        public CallToolResult DescribeObjectV4(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
            => HmiInspectionContract.Run("DescribeObject", false, false, () => DescribeObject(objectKind, objectPath, softwarePath, maxMembers));

        public ResponseObjectDescribe DescribeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return _reflection.DescribeObject(objectKind, objectPath, softwarePath, maxMembers);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetObjectProperty"), Description("[L2][Reflection]Get an Openness object property by dotted path. Use after DescribeObject/DescribeObjectProperty to safely inspect current state before writing.")]
        public CallToolResult GetObjectPropertyV4(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Property path, e.g. Name or BlockGroup.Groups")] string propertyPath,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "")
            => HmiInspectionContract.Run("GetObjectProperty", false, false, () => GetObjectProperty(objectKind, objectPath, propertyPath, softwarePath));

        public ResponseObjectValue GetObjectProperty(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Property path, e.g. Name or BlockGroup.Groups")] string propertyPath,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "")
        {
            try
            {
                return _reflection.GetObjectProperty(objectKind, objectPath, propertyPath, softwarePath);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading property: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListObjectChildren"), Description("[L2][Reflection]List child items from an enumerable Openness property, e.g. Devices, DeviceItems, Connections, Screens, Blocks. Use to discover paths instead of guessing.")]
        public CallToolResult ListObjectChildrenV4(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Enumerable property name/path, e.g. Devices, DeviceItems, BlockGroup.Blocks")] string collectionProperty,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max child items to return")] int limit = 200)
            => HmiInspectionContract.Run("ListObjectChildren", false, false, () => ListObjectChildren(objectKind, objectPath, collectionProperty, softwarePath, limit));

        public ResponseObjectChildren ListObjectChildren(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Enumerable property name/path, e.g. Devices, DeviceItems, BlockGroup.Blocks")] string collectionProperty,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max child items to return")] int limit = 200)
        {
            try
            {
                return _reflection.ListObjectChildren(objectKind, objectPath, collectionProperty, softwarePath, limit);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing children: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeObject"), Description("[L2][Reflection]Invoke an Openness method via reflection. Default is read-oriented; set allowWrite=true only after DescribeObject confirms the target method/signature. This is the generic bridge for public API operations not yet wrapped by MCP.")]
        public CallToolResult InvokeObjectV4(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, e.g. [\"AttrName\"]. Empty for no args. DirectoryInfo/FileInfo parameters accept path strings on the MCP machine.")] NativeValue[]? args = null,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
            => HmiInspectionContract.Run("InvokeObject", allowWrite, allowWrite, () => InvokeObject(objectKind, objectPath, methodName, args == null ? null : args.Select(value => value.Json).ToArray(), softwarePath, allowWrite));

        public ResponseObjectValue InvokeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, e.g. [\"AttrName\"]. Empty for no args. DirectoryInfo/FileInfo parameters accept path strings on the MCP machine.")] System.Text.Json.JsonElement[]? args = null,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
        => InvokeObject(objectKind, objectPath, methodName, ToArgsArray(args), softwarePath, allowWrite);

        // Internal overload (no tool attribute): existing in-process callers pass a JsonArray directly.
        public ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args, string softwarePath = "", bool allowWrite = false)
        {
            try
            {
                return _reflection.InvokeObject(objectKind, objectPath, methodName, args, softwarePath, allowWrite);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking method: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeService"), Description("[L2][Reflection]GetService bridge: describe a service object (by type name suffix) from a target object.")]
        public CallToolResult DescribeServiceV4(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Service type suffix, e.g. PlcChecksumProvider or ICompilable")] string serviceTypeSuffix,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
            => HmiInspectionContract.Run("DescribeService", false, false, () => DescribeService(objectKind, objectPath, serviceTypeSuffix, softwarePath, maxMembers));

        public ResponseObjectDescribe DescribeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Service type suffix, e.g. PlcChecksumProvider or ICompilable")] string serviceTypeSuffix,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return _reflection.DescribeService(objectKind, objectPath, serviceTypeSuffix, softwarePath, maxMembers);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing service: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeService"), Description("[L2][Reflection]GetService bridge: invoke a method on a service object (by type name suffix) from a target object.")]
        public CallToolResult InvokeServiceV4(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Service type suffix, e.g. PlcChecksumProvider or ICompilable")] string serviceTypeSuffix,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, empty for no args")] NativeValue[]? args = null,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
            => HmiInspectionContract.Run("InvokeService", allowWrite, allowWrite, () => InvokeService(objectKind, objectPath, serviceTypeSuffix, methodName, args == null ? null : args.Select(value => value.Json).ToArray(), softwarePath, allowWrite));

        public ResponseObjectValue InvokeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScriptModule|HmiScripts")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. For HmiScriptModule: exact module name or /Scripts/URI-escaped-name with softwarePath; HmiScripts: /Scripts with softwarePath.")] string objectPath,
            [Description("Service type suffix, e.g. PlcChecksumProvider or ICompilable")] string serviceTypeSuffix,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, empty for no args")] System.Text.Json.JsonElement[]? args = null,
            [Description("softwarePath required for Block/Type and HmiScriptModule/HmiScripts")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
        {
            try
            {
                return _reflection.InvokeService(objectKind, objectPath, serviceTypeSuffix, methodName, ToArgsArray(args), softwarePath, allowWrite);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking service method: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Reflection-bridge args arrive as a native JSON array. We accept JsonElement[] (not JsonArray) so the
        // generated tool schema is a well-formed `type:array` WITH `items`, which strict clients (VS Code) require;
        // a bare JsonArray param emits `type:array` without items and gets rejected. Rebuild a JsonArray for Portal.
        private static JsonArray? ToArgsArray(System.Text.Json.JsonElement[]? args)
            => args == null ? null : new JsonArray(args.Select(e => JsonNode.Parse(e.GetRawText())).ToArray());
    }
}
