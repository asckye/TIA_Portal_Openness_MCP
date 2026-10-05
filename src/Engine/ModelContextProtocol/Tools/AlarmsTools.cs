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

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class AlarmsTools
    {
        private readonly AlarmsService _service;

        public AlarmsTools(AlarmsService service) => _service = service;

        [McpServerTool(Name = "ExportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm classes to a file. Alarm classes define severity, acknowledgment behavior, and display colors for alarms." +
            " The exported file can be edited and re-imported to update alarm class configurations." +
            " Use before bulk alarm class updates to create a backup.")]
        public ResponseMessage ExportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the export, e.g. 'C:\\Temp\\AlarmClasses.xml'")] string exportPath)
        {
            try { return _service.ExportAlarmClasses(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm classes from a previously exported file." +
            " Overwrites existing alarm class definitions. Run CompileSoftware after import.")]
        public ResponseMessage ImportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to import from")] string importPath)
        {
            try { return _service.ImportAlarmClasses(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export all PLC alarm text lists to an XLSX (Excel) file." +
            " Text lists contain the text strings shown for each alarm condition." +
            " Supports multi-language projects — all configured languages are exported." +
            " Typical use: export → translate in Excel → ImportAlarmTextLists.")]
        public ResponseMessage ExportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output, e.g. 'C:\\Temp\\AlarmTexts.xlsx'")] string exportPath)
        {
            try { return _service.ExportAlarmTextLists(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm text lists from an XLSX file." +
            " The file must match the format exported by ExportAlarmTextLists." +
            " Run CompileSoftware after import to validate alarm configuration.")]
        public ResponseMessage ImportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XLSX file")] string importPath)
        {
            try { return _service.ImportAlarmTextLists(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmInstanceTexts"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm instance texts to an XLSX file." +
            " Instance texts are the alarm messages tied to specific FB/FC instances (e.g. Motor_01.AlarmText)." +
            " Options control what additional columns are included in the export." +
            " Typical use: export → fill in alarm descriptions → ImportPlcAlarmInstanceTexts (PlcAlarmTextProvider.ImportInstanceTextsFromXlsx).")]
        public ResponseMessage ExportAlarmInstanceTexts(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output")] string exportPath,
            [Description("includeInfoText: include the Info Text column (default: true)")] bool includeInfoText = true,
            [Description("includeAdditionalTexts: include Additional Texts columns (default: true)")] bool includeAdditionalTexts = true,
            [Description("includeAlarmClass: include the Alarm Class column (default: true)")] bool includeAlarmClass = true)
        {
            try { return _service.ExportAlarmInstanceTexts(softwarePath, exportPath, includeInfoText, includeAdditionalTexts, includeAlarmClass); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm instance texts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }


        [McpServerTool(Name="ExchangePlcAlarmTextListsXlsx"), Description("[L2][PLC-Alarms][WRITE] Native XLSX exchange of PLC alarm text lists through PlcAlarmTextListProvider of the exact PLC or of a unit (unitName + unitKind): export writes a NEW absolute .xlsx (every user text list in every project language, or the filtered overload with textListNamesJson + culturesJson, both required together; TIA refuses system text lists and inactive languages natively) and returns the TextListXlsxResult state, log file and file hash; import (importOption None refuses existing lists, Override replaces their entries) needs confirmImport=true besides dryRun=false and an Offline PLC. No save/compile/download.")]
        public ResponseMessage ExchangePlcAlarmTextListsXlsx(
            string softwarePath,
            [Description("action: the operation to perform - export | import.")] string action,
            string filePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("textListNamesJson: JSON array of text list names ('[]' = all).")] string textListNamesJson="[]",
            [Description("culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages).")] string culturesJson="[]",
            [Description("importOption: import option - None | Override.")] string importOption="None",
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => _service.ExchangePlcAlarmTextListsXlsx(softwarePath,action,filePath,unitName,unitKind,textListNamesJson,culturesJson,importOption,confirmImport,dryRun);

        [McpServerTool(Name="ImportPlcAlarmInstanceTexts"), Description("[L2][PLC-Alarms][WRITE] Native PlcAlarmTextProvider.ImportInstanceTextsFromXlsx for one exact PLC from an existing absolute xlsx and a JSON array of exact project culture names (each must be a project language). Returns native state and log file path; text changes need separate export/readback. Default preview; execution requires Offline PLC. No save/compile/download.")]
        public ResponseMessage ImportPlcAlarmInstanceTexts(
            string softwarePath,
            string filePath,
            [Description("culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages).")] string culturesJson,
            bool dryRun=true)
            => _service.ImportPlcAlarmInstanceTexts(softwarePath,filePath,culturesJson,dryRun);

        [McpServerTool(Name="ManagePlcAlarmTextList"), Description("[L2][PLC-Alarms][WRITE] read/createFromMasterCopy/delete PLC alarm text lists (PlcAlarmTextlistGroup system+user lists; entries are not exposed by Openness). read paginates Name/ID/ListRange, optionally one exact name. createFromMasterCopy needs an open library name and exact master copy path; copyMode ThrowIfExists/Rename/Replace optional. delete refuses system lists and requires confirmDelete=true besides dryRun=false; absence verified. Default preview, Offline PLC for execution; no save/compile/download.")]
        public ResponseMessage ManagePlcAlarmTextList(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("action: read | createFromMasterCopy | delete.")] string action="read",
            [Description("name: exact alarm text list name (delete) or the name to give the copy (createFromMasterCopy).")] string name="",
            [Description("libraryName: for createFromMasterCopy - global library name ('' = the project library).")] string libraryName="",
            [Description("masterCopyPath: for createFromMasterCopy - 'Folder/Name' of the master copy.")] string masterCopyPath="",
            [Description("copyMode: for createFromMasterCopy - ThrowIfExists | Rename | Replace.")] string copyMode="",
            [Description("confirmDelete: must be true together with dryRun=false to delete.")] bool confirmDelete=false,
            [Description("offset: first list to return (paging).")] int offset=0,
            [Description("limit: maximum lists to return.")] int limit=100,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => _service.ManagePlcAlarmTextList(softwarePath,action,name,libraryName,masterCopyPath,copyMode,confirmDelete,offset,limit,dryRun);
    }
}
