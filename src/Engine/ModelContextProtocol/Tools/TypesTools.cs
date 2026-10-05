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
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class TypesTools
    {
        private readonly IEngineeringSession _session;
        private readonly TypesService _domain;

        public TypesTools(TypesService domain, IEngineeringSession session)
        {
            _domain = domain;
            _session = session;
        }

        [McpServerTool(Name = "GetPlcTypeInfo"), Description("[L2][PLC-Software]Get a type info from the plc software Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetTypeInfoV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath)
            => PlcToolContract.Run("GetPlcTypeInfo", false, true, () => GetTypeInfo(softwarePath, typePath));

        public ResponseTypeInfo GetTypeInfo(
            string softwarePath,
            string typePath)
        {
            try
            {
                var type = _session.GetType(softwarePath, typePath);
                if (type != null)
                {
                    var attributes = Helper.GetAttributeList(type);

                    return new ResponseTypeInfo
                    {
                        Message = $"Type info retrieved from '{typePath}' in '{softwarePath}'",
                        Name = type.Name,
                        TypeName = type.GetType().Name,
                        Namespace = type.Namespace,
                        IsConsistent = type.IsConsistent,
                        ModifiedDate = type.ModifiedDate,
                        IsKnowHowProtected = type.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = type.ToString(),
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException($"Type not found at '{typePath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving type info from '{typePath}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListPlcTypes"), Description("[L2][PLC-Software]Get a list of types from the plc software Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetTypesV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
            => PlcToolContract.Run("ListPlcTypes", false, true, () => GetTypes(softwarePath, regexName));

        public ResponseTypes GetTypes(
            string softwarePath,
            string regexName = "")
        {
            try
            {
                var list = _session.GetTypes(softwarePath, regexName);

                // null = 根本没查成（没连接/没打开项目）；空列表 = 这个 PLC 里确实没有。
                // 无连接或无项目必须走失败分支，不能返回空列表让离线调用被报告为「成功，0 个」。
                if (list == null)
                {
                    throw new McpException(
                        $"No TIA project is open, cannot list types of '{softwarePath}'. "
                        + "Call Connect / OpenProject (or AttachToOpenProject) first. "
                        + "This does NOT mean the PLC has no types.",
                        McpErrorCode.InvalidParams);
                }

                var responseList = new List<ResponseTypeInfo>();
                var read = new PlcListingRead();
                foreach (var type in list)
                    if (type != null) responseList.Add(Helper.ReadTypeInfo(type, read));

                if (list != null)
                {
                    return new ResponseTypes
                    {
                        Message = $"Types with regex '{regexName}' retrieved from '{softwarePath}'",
                        Items = responseList,
                        Meta = read.Metadata("PLC user data types and user type groups; system type groups excluded")
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving user defined types with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving user defined types with regex '{regexName}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcType"), Description("[L2][PLC-Software]Export a type from the plc software Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportTypeV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the directory where to export the type; output file will be '<type name>.xml'")] string exportPath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
            => PlcToolContract.Run("ExportPlcType", true, true, () => ExportType(softwarePath, exportPath, typePath, preservePath));

        public ResponseExportType ExportType(
            string softwarePath,
            string exportPath,
            string typePath,
            bool preservePath = false)
        {
            try
            {
                var type = _session.ExportType(softwarePath, typePath, exportPath, preservePath);
                if (type != null)
                {
                    return new ResponseExportType
                    {
                        Message = $"Type exported from '{typePath}' to '{_session.LastExportedFile ?? exportPath}'",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting type from '{typePath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        throw new McpException(("Type not found." + EngineeringLookupHints.BuildTypeDidYouMean(softwarePath, typePath)).Trim(), McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                        throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export type.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";
                            Logger?.LogError(pex, "MCP ExportPlcType failed for {SoftwarePath} {TypePath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["typePath"], pex.Data?["exportPath"]);
                            throw new McpException(msg, McpErrorCode.InternalError);
                        }
                }
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type from '{typePath}' to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public ResponseTempExport ExportTypeToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("typePath: full type path inside PLC software")] string typePath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = _domain.ExportTypeToTemp(softwarePath, typePath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Type exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException("Failed exporting type to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcType"), Description("[L1][PLC-Software]Import a type from file into the plc software Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportTypeV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the type")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the type")] string importPath)
            => PlcToolContract.Run("ImportPlcType", true, true, () => ImportType(softwarePath, groupPath, importPath));

        public ResponseImportType ImportType(
            string softwarePath,
            string groupPath,
            string importPath)
        {
            try
            {
                _session.ImportType(softwarePath, groupPath, importPath);
                return new ResponseImportType
                {
                    Message = $"Type imported from '{importPath}' to '{groupPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing type from '{importPath}' to '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing type from '{importPath}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SeedProjectFromReference"), Description("[L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SeedProjectFromReferenceV4(
            [Description("plcSoftwarePath: path in the project structure to the PLC software")] string plcSoftwarePath,
            [Description("hmiSoftwarePath: path in the project structure to the HMI software")] string hmiSoftwarePath,
            [Description("referenceDir: directory containing manifest.json and subfolders (plc/blocks, plc/types, hmi/screens, hmi/tags)")] string referenceDir,
            [Description("placeholders: JsonObject key-values for replacement in XML, e.g. {\"PLC_NAME\":\"PLC_1\"}")] JsonObject? placeholders = null)
            => PlcToolContract.Run("SeedProjectFromReference", true, true, () => SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders));

        public ResponseSeed SeedProjectFromReference(
            string plcSoftwarePath,
            string hmiSoftwarePath,
            string referenceDir,
            JsonObject? placeholders = null)
        {
            try
            {
                var res = _domain.SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders);
                // envelope: legacy-existing-meta
                res.Meta ??= new JsonObject();
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["success"] = (res.Failed == null || !res.Failed.Any());
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error seeding project from reference: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcTypes"), Description("[L2][PLC-Software]Export types from the plc software to path Current native policy; V4 safety behavior is not yet accepted.")]
        public Task<CallToolResult> ExportTypesV4(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the types")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
            => PlcToolContract.RunAsync("ExportPlcTypes", true, true, async () => await ExportTypes(server, context, softwarePath, exportPath, regexName, preservePath));

        public async Task<ResponseExportTypes> ExportTypes(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            string softwarePath,
            string exportPath,
            string regexName = "",
            bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context?.Params?.ProgressToken;

            try
            {
                // First, get the list of types to determine total count
                Logger?.LogInformation($"Starting export of types from '{softwarePath}' to '{exportPath}'");

                var allTypes = await Task.Run(() => _session.GetTypes(softwarePath, regexName));
                var totalTypes = allTypes?.Count ?? 0;

                if (totalTypes == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No types found to export",
                            progressToken
                        });
                    }

                    return new ResponseExportTypes
                    {
                        Message = $"No types found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseTypeInfo>(),
                        Meta = ResponseMeta.Basic(DateTime.Now, true, ("totalTypes", 0), ("exportedTypes", 0), ("duration", (DateTime.Now - startTime).TotalSeconds))
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalTypes,
                        Message = $"Starting export of {totalTypes} types...",
                        progressToken
                    });
                }

                // Export types asynchronously
                var exportedTypes = await Task.Run(() => _domain.ExportTypes(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) types for reporting
                var inconsistentTypeInfos = new List<ResponseTypeInfo>();
                if (allTypes != null)
                {
                    foreach (var t in allTypes)
                    {
                        if (t != null && t.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(t);
                            inconsistentTypeInfos.Add(new ResponseTypeInfo
                            {
                                Name = t.Name,
                                TypeName = t.GetType().Name,
                                Namespace = t.Namespace,
                                IsConsistent = t.IsConsistent,
                                ModifiedDate = t.ModifiedDate,
                                IsKnowHowProtected = t.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = t.ToString()
                            });
                        }
                    }
                }

                // Send progress update after export completion
                if (exportedTypes != null && progressToken != null)
                {
                    var exportedCount = exportedTypes.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalTypes,
                        Message = $"Exported {exportedCount} of {totalTypes} types",
                        progressToken
                    });
                }

                if (exportedTypes != null)
                {
                    var responseList = new List<ResponseTypeInfo>();
                    var processedCount = 0;

                    foreach (var type in exportedTypes)
                    {
                        if (type != null)
                        {
                            var attributes = Helper.GetAttributeList(type);

                            responseList.Add(new ResponseTypeInfo
                            {
                                Name = type.Name,
                                TypeName = type.GetType().Name,
                                Namespace = type.Namespace,
                                IsConsistent = type.IsConsistent,
                                ModifiedDate = type.ModifiedDate,
                                IsKnowHowProtected = type.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = type.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalTypes,
                            Message = $"Export completed: {processedCount} types exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Type export completed: {processedCount} types exported in {duration:F2} seconds");

                    return new ResponseExportTypes
                    {
                        Message = $"Export completed: {processedCount} types with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentTypeInfos,
                        // envelope: legacy-multiple-dynamic-fields
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalTypes"] = totalTypes,
                            ["exportedTypes"] = processedCount,
                            ["inconsistentTypes"] = inconsistentTypeInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Type export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    { /* swallow(teardown): Progress notification failure must not replace the original export or import error. */
                        // Ignore notification errors during error handling
                    }
                }

                Logger?.LogError(ex, $"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting types '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public ResponseTempExport ExportTypesToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = _domain.ExportTypesToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Types exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                throw new McpException("Failed exporting types to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting types to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
