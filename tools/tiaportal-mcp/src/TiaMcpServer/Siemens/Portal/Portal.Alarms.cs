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

        public ResponseMessage ExportAlarmTextLists(string softwarePath, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                // PlcAlarmTextlistGroup is a property on PlcSoftware
                var textListGroup = TryGetPropertyValue(plc, "PlcAlarmTextlistGroup", "AlarmTextlistGroup");
                if (textListGroup == null)
                    return new ResponseMessage { Message = "PlcAlarmTextlistGroup not accessible on this PLC." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                // ExportToXlsx(FileInfo) — overload with no filters
                var result = TryInvokeMethodByName(textListGroup, "ExportToXlsx", new FileInfo(exportPath));
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm text lists exported to '{exportPath}' (State={state})."
                        : $"Alarm text list export had issues. State={state}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportAlarmTextLists(string softwarePath, string importPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var textListGroup = TryGetPropertyValue(plc, "PlcAlarmTextlistGroup", "AlarmTextlistGroup");
                if (textListGroup == null)
                    return new ResponseMessage { Message = "PlcAlarmTextlistGroup not accessible on this PLC." };

                // ImportFromXlsx(FileInfo, ImportOptions) — use None import options via reflection
                var importMethod = textListGroup.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ImportFromXlsx" && m.GetParameters().Length >= 1);

                if (importMethod == null)
                    return new ResponseMessage { Message = "ImportFromXlsx method not found." };

                var parms = importMethod.GetParameters();
                object?[] args;
                if (parms.Length == 2 && parms[1].ParameterType.IsEnum)
                {
                    // ImportOptions enum — use value 0 (None/Default)
                    args = new object?[] { new FileInfo(importPath), Enum.ToObject(parms[1].ParameterType, 0) };
                }
                else
                {
                    args = new object?[] { new FileInfo(importPath) };
                }

                var result = importMethod.Invoke(textListGroup, args);
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm text lists imported from '{importPath}' (State={state})."
                        : $"Alarm text list import had issues. State={state}.",
                    Meta = new JsonObject { ["importPath"] = importPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        public ResponseMessage ExportAlarmInstanceTexts(string softwarePath, string exportPath, bool includeInfoText = true, bool includeAdditionalTexts = true, bool includeAlarmClass = true)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = plc.GetService<PlcAlarmTextProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "PlcAlarmTextProvider service not available for this PLC." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");

                // ExportInstanceTextsToXlsx(FileInfo, IEnumerable<Language>, PlcAlarmTextXlsxExportOption)
                // Use reflection to handle Language and flags enum
                var exportMethod = provider.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ExportInstanceTextsToXlsx");

                if (exportMethod == null)
                    return new ResponseMessage { Message = "ExportInstanceTextsToXlsx method not found." };

                var parms = exportMethod.GetParameters();
                // Build options flags value: All = typically 7 (IncludeInfoText|IncludeAdditionalTexts|IncludeAlarmClass)
                object optionsValue;
                if (parms.Length >= 3 && parms[2].ParameterType.IsEnum)
                {
                    int flags = 0;
                    if (includeInfoText) flags |= 1;
                    if (includeAdditionalTexts) flags |= 2;
                    if (includeAlarmClass) flags |= 4;
                    optionsValue = Enum.ToObject(parms[2].ParameterType, flags);
                }
                else
                {
                    optionsValue = 7; // All
                }

                // Pass null for languages (export all)
                object?[] args = parms.Length == 3
                    ? new object?[] { new FileInfo(exportPath), null, optionsValue }
                    : new object?[] { new FileInfo(exportPath) };

                var result = exportMethod.Invoke(provider, args);
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm instance texts exported to '{exportPath}' (State={state})."
                        : $"Alarm instance text export had issues. State={state}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmInstanceTexts failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        #endregion
    }
}
