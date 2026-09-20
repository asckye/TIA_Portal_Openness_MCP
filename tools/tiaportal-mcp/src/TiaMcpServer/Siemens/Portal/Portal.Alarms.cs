using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: alarms. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region alarms

        // 2.7.35: typed AlarmClassExportImportResultMessage rows (Message + per-message State).
        private static JsonArray AlarmClassMessages(AlarmClassExportImportResult? result)
        {
            var rows = new JsonArray();
            if (result?.Messages == null) return rows;
            foreach (AlarmClassExportImportResultMessage message in EngineeringGroupOperations.Items(result.Messages).Cast<AlarmClassExportImportResultMessage>())
                rows.Add(new JsonObject { ["message"] = message.Message, ["state"] = message.State.ToString() });
            return rows;
        }

        public ResponseMessage ExportAlarmClasses(string softwarePath, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                // Official "Export/Import of Alarm classes": the provider lives on ProjectBase; older TIA versions answered it on the PLC too.
                var provider = plc.GetService<AlarmClassDataProvider>() ?? _project?.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC or project." };

                // 2.7.46 real project: any other extension is refused by TIA with "invalid file extension" (official page: the format is .DAT).
                if (!exportPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    return new ResponseMessage { Message = "exportPath must end in .DAT (official AlarmClassDataProvider format, e.g. D:\\AlarmClasses.DAT); got '" + exportPath + "'.", Meta = new JsonObject { ["success"] = false } };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                AlarmClassExportImportResult result = provider.Export(new FileInfo(exportPath));
                var state = result?.State.ToString() ?? "Unknown";
                var errCount = result?.ErrorCount ?? 0;
                bool ok = state == "Success" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes exported to '{exportPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class export failed. State={state}, Errors={errCount}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["errorCount"] = errCount, ["warningCount"] = result?.WarningCount ?? 0, ["messages"] = AlarmClassMessages(result) }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportAlarmClasses(string softwarePath, string importPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = plc.GetService<AlarmClassDataProvider>() ?? _project?.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC or project." };

                if (!importPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || !File.Exists(importPath))
                    return new ResponseMessage { Message = "importPath must be an existing .DAT file written by ExportAlarmClasses (official AlarmClassDataProvider format); got '" + importPath + "'.", Meta = new JsonObject { ["success"] = false } };
                AlarmClassExportImportResult result = provider.Import(new FileInfo(importPath));
                var state = result?.State.ToString() ?? "Unknown";
                var errCount = result?.ErrorCount ?? 0;
                bool ok = state == "Success" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes imported from '{importPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class import failed. State={state}, Errors={errCount}.",
                    Meta = new JsonObject { ["importPath"] = importPath, ["state"] = state, ["errorCount"] = errCount, ["warningCount"] = result?.WarningCount ?? 0, ["messages"] = AlarmClassMessages(result) }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        // 2.7.46: typed PlcAlarmTextListProvider (the reflective ExportToXlsx / ImportFromXlsx lookup on PlcAlarmTextlistGroup never found the
        // methods - they live on the provider service). Real project (fresh 1515F, no text lists): TIA throws TextListNotFoundException.
        public ResponseMessage ExportAlarmTextLists(string softwarePath, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };
            try
            {
                var provider = plc.GetService<PlcAlarmTextListProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextListProvider service not available on this PLC.", Meta = new JsonObject { ["success"] = false } };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TextListXlsxResult result = provider.ExportToXlsx(new FileInfo(exportPath));
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result?.State != TextListXlsxResultState.Error;
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm text lists exported to '{exportPath}' (State={state})." : $"Alarm text list export reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["fileExists"] = File.Exists(exportPath), ["success"] = ok }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.GetBaseException().Message} (a PLC without any alarm text list answers TextListNotFoundException - create one in TIA or via ManagePlcAlarmTextList createFromMasterCopy first).", Meta = new JsonObject { ["success"] = false } };
            }
        }

        public ResponseMessage ImportAlarmTextLists(string softwarePath, string importPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };
            try
            {
                var provider = plc.GetService<PlcAlarmTextListProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextListProvider service not available on this PLC.", Meta = new JsonObject { ["success"] = false } };
                if (!File.Exists(importPath)) return new ResponseMessage { Message = $"Import file not found: {importPath}", Meta = new JsonObject { ["success"] = false } };
                TextListXlsxResult result = provider.ImportFromXlsx(new FileInfo(importPath), ImportOptions.None);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result?.State != TextListXlsxResultState.Error;
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm text lists imported from '{importPath}' (State={state}); compile afterwards." : $"Alarm text list import reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = new JsonObject { ["importPath"] = importPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["success"] = ok }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.GetBaseException().Message}", Meta = new JsonObject { ["success"] = false } };
            }
        }

        // 2.7.46: typed ExportInstanceTextsToXlsx(file, languages, option) - the reflective call passed null languages and only reported
        // the TargetInvocationException wrapper text; all active project languages are exported now.
        public ResponseMessage ExportAlarmInstanceTexts(string softwarePath, string exportPath, bool includeInfoText = true, bool includeAdditionalTexts = true, bool includeAlarmClass = true)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };
            try
            {
                var provider = plc.GetService<PlcAlarmTextProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextProvider service not available for this PLC.", Meta = new JsonObject { ["success"] = false } };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                var option = PlcAlarmTextXlsxExportOption.None;
                if (includeInfoText) option |= PlcAlarmTextXlsxExportOption.IncludeInfoText;
                if (includeAdditionalTexts) option |= PlcAlarmTextXlsxExportOption.IncludeAdditionalTexts;
                if (includeAlarmClass) option |= PlcAlarmTextXlsxExportOption.IncludeAlarmClass;
                var languages = EngineeringGroupOperations.Items(_project!.LanguageSettings.ActiveLanguages).Cast<Language>().ToList();
                PlcAlarmTextXlsxResult result = provider.ExportInstanceTextsToXlsx(new FileInfo(exportPath), languages, option);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result?.State != PlcAlarmTextXlsxResultState.Error;
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm instance texts exported to '{exportPath}' (State={state}, languages {string.Join(", ", languages.Select(l => l.Culture?.Name))})." : $"Alarm instance text export reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["fileExists"] = File.Exists(exportPath), ["option"] = option.ToString(), ["success"] = ok }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmInstanceTexts failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.GetBaseException().Message}", Meta = new JsonObject { ["success"] = false } };
            }
        }

        #endregion
    }
}
