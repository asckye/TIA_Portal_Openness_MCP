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


using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcTablesTools
    {
        private readonly PlcTablesService _tables;
        private readonly IEngineeringSession _session;

        public PlcTablesTools(PlcTablesService tables, IEngineeringSession session)
        {
            _tables = tables;
            _session = session;
        }


        [McpServerTool(Name = "ListPlcTagTables"), Description("[L2][PLC-Software] List all PLC tag table names. Requires: ConnectPortal + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetPlcTagTablesV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
            => PlcToolContract.Run("ListPlcTagTables", false, true, () => GetPlcTagTables(softwarePath));

        public ResponseStringList GetPlcTagTables(
            string softwarePath)
        {
            try
            {
                var items = _session.GetPlcTagTables(softwarePath, out var walk);
                if (items != null)
                {
                    // 空清单有三种完全不同的成因：这个 PLC 确实没有表 / TagTables 属性
                    // 在这个版本上叫别的名字 / 读属性时抛了异常被吞掉。三者返回的东西
                    // 一模一样，用户报「枚举返回空但删除工具能找到同一张表」时我们手上
                    // 没有任何证据。所以空清单必须把「走过了什么」一并带回来。
                    var meta = ResponseMeta.Basic(DateTime.Now, true);
                    if (items.Count == 0)
                    {
                        meta["walkedGroupType"] = walk.RootGroupType;
                        meta["tagTablesPropertyFound"] = walk.TagTablesPropertyFound;
                        meta["tagTablesPropertyError"] = walk.TagTablesPropertyError;
                        meta["groupsVisited"] = walk.GroupsVisited;
                        meta["notes"] = new JsonArray(
                            walk.Notes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                    }

                    return new ResponseStringList
                    {
                        Message = items.Count > 0
                            ? $"PLC tag tables listed for '{softwarePath}'"
                            : $"'{softwarePath}' returned no tag tables during enumeration. This **does not necessarily** mean there are no tables: "
                              + "a failed property read has the same appearance, so Meta retains the evidence from this enumeration"
                              + "（walkedGroupType / tagTablesPropertyFound / tagTablesPropertyError / groupsVisited / notes）。"
                              + "If tables are expected, provide these fields to the maintainer.",
                        Items = items,
                        Meta = meta
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC tag tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcTagTable"), Description("[L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportPlcTagTableV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("tagTableName: PLC tag table name")] string tagTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
            => PlcToolContract.Run("ExportPlcTagTable", true, true, () => ExportPlcTagTable(softwarePath, tagTableName, exportPath));

        public ResponseExportFile ExportPlcTagTable(
            string softwarePath,
            string tagTableName,
            string exportPath)
        {
            try
            {
                var ok = _session.ExportPlcTagTable(softwarePath, tagTableName, exportPath, out var reason);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC tag table '{tagTableName}' exported",
                        ExportPath = exportPath,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException(
                    $"Failed exporting PLC tag table '{tagTableName}' from '{softwarePath}'" +
                    (string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason),
                    McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTable"), Description("[L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort) Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportPlcTagTableV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of PLC tag table XML")] string importPath)
            => PlcSingleImportRecovery.Run(_session, "ImportPlcTagTable", softwarePath, folderPath, importPath, "TagTable",
                () => PlcToolContract.Run("ImportPlcTagTable", true, true, () => ImportPlcTagTable(softwarePath, folderPath, importPath)));

        public ResponseMessage ImportPlcTagTable(
            string softwarePath,
            string folderPath,
            string importPath)
        {
            try
            {
                _session.ImportPlcTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"PLC tag table imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }





        [McpServerTool(Name = "ListPlcWatchTables"), Description("[L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetPlcWatchTablesV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
            => PlcToolContract.Run("ListPlcWatchTables", false, true, () => GetPlcWatchTables(softwarePath));

        public ResponseStringList GetPlcWatchTables(
            string softwarePath)
        {
            try
            {
                var items = _tables.GetPlcWatchTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC watch tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC watch tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public ResponseMessage SetForceTableEntry(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the force table to configure (created if not existing)")] string tableName,
            [Description("address: variable address to force, e.g. 'DB1.DBX0.0', '%M0.0'")] string address,
            [Description("forceValue: value to force, e.g. 'TRUE', '42'")] string forceValue)
        {
            try
            {
                return _tables.EnsureForceTableEntry(softwarePath, tableName, address, forceValue);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting force table entry: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTable"), Description("[L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportPlcWatchTableV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("watchTableName: PLC watch table name")] string watchTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
            => PlcToolContract.Run("ExportPlcWatchTable", true, true, () => ExportPlcWatchTable(softwarePath, watchTableName, exportPath));

        public ResponseExportFile ExportPlcWatchTable(
            string softwarePath,
            string watchTableName,
            string exportPath)
        {
            try
            {
                var ok = _tables.ExportPlcWatchTable(softwarePath, watchTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC watch table '{watchTableName}' exported",
                        ExportPath = exportPath,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"Failed exporting PLC watch table '{watchTableName}' from '{softwarePath}'" + _session.AvailablePlcPathsSuffix(), McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

    }
}
