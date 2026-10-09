using TiaMcp.Logic.V4;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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
using System.Globalization;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.WatchAndForceTables;
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks.Interface;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class AlarmsService
    {
        private readonly IEngineeringSession _session;

        public AlarmsService(IEngineeringSession session) => _session = session;

        #region alarms

        // Typed AlarmClassExportImportResultMessage rows (Message + per-message State).
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
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                // Official "Export/Import of Alarm classes": the provider lives on ProjectBase; older TIA versions answered it on the PLC too.
                var provider = plc.GetService<AlarmClassDataProvider>() ?? _session.CurrentProject?.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC or project.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                // Native observation: other extensions are refused with "invalid file extension"; the official format is .DAT.
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                if (!exportPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    return new ResponseMessage { Message = "exportPath must end in .DAT (official AlarmClassDataProvider format, e.g. D:\\AlarmClasses.DAT); got '" + exportPath + "'.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "INVALID_ARGUMENT"), ("mayHaveChanged", false)) };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                AlarmClassExportImportResult result = provider.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)));
                var state = result?.State.ToString() ?? "Unknown";
                var errCount = result?.ErrorCount ?? 0;
                bool ok = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses, state);
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes exported to '{exportPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class export failed. State={state}, Errors={errCount}.",
                    // envelope: legacy-success-last
                    Meta = NativeXlsxEvidence(new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["errorCount"] = errCount, ["warningCount"] = result?.WarningCount ?? 0, ["messages"] = AlarmClassMessages(result) }, state, false, null, exportPath, TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses)
                };
            }
            catch (Exception ex)
            {
                TiaMcp.Logic.V4.CallerInputFiles.RecordExportFailure(ex);
                _session.Logger?.LogError(ex, "ExportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportAlarmClasses(string softwarePath, string importPath)
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var provider = plc.GetService<AlarmClassDataProvider>() ?? _session.CurrentProject?.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC or project.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };

                if (!importPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || !File.Exists(importPath))
                    return new ResponseMessage { Message = "importPath must be an existing .DAT file written by ExportAlarmClasses (official AlarmClassDataProvider format); got '" + importPath + "'.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "INVALID_ARGUMENT"), ("mayHaveChanged", false)) };
                AlarmClassExportImportResult result = provider.Import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)));
                var state = result?.State.ToString() ?? "Unknown";
                var errCount = result?.ErrorCount ?? 0;
                bool ok = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses, state);
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes imported from '{importPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class import failed. State={state}, Errors={errCount}.",
                    Meta = NativeXlsxEvidence(new JsonObject { ["importPath"] = importPath, ["state"] = state, ["errorCount"] = errCount, ["warningCount"] = result?.WarningCount ?? 0, ["messages"] = AlarmClassMessages(result) }, state, true, null, importPath, TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses)
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ImportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        // ExportToXlsx / ImportFromXlsx belong to PlcAlarmTextListProvider, not PlcAlarmTextlistGroup.
        // Native observation (fresh 1515F, no text lists): TIA throws TextListNotFoundException.
        // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
        public ResponseMessage ExportAlarmTextLists(string softwarePath, string exportPath)
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };
            try
            {
                var provider = plc.GetService<PlcAlarmTextListProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextListProvider service not available on this PLC.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TextListXlsxResult result = provider.ExportToXlsx(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)));
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm text lists exported to '{exportPath}' (State={state})." : $"Alarm text list export reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = NativeXlsxEvidence(new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["fileExists"] = File.Exists(exportPath), ["success"] = ok }, state, false, result?.LogFilePath?.FullName, exportPath, TiaMcp.Adapters.Contracts.NativeResultStates.TextLists)
                };
            }
            catch (Exception ex)
            {
                TiaMcp.Logic.V4.CallerInputFiles.RecordExportFailure(ex);
                _session.Logger?.LogError(ex, "ExportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.GetBaseException().Message} (a PLC without any alarm text list answers TextListNotFoundException - create one in TIA or via ManagePlcAlarmTextList createFromMasterCopy first).", Meta = ResponseMeta.Unstamped(false) };
            }
        }

        public ResponseMessage ImportAlarmTextLists(string softwarePath, string importPath)
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };
            try
            {
                var provider = plc.GetService<PlcAlarmTextListProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextListProvider service not available on this PLC.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };
                if (!File.Exists(importPath)) return new ResponseMessage { Message = $"Import file not found: {importPath}", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };
                TextListXlsxResult result = provider.ImportFromXlsx(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)), ImportOptions.None);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm text lists imported from '{importPath}' (State={state}); compile afterwards." : $"Alarm text list import reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = NativeXlsxEvidence(new JsonObject { ["importPath"] = importPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["success"] = ok }, state, true, result?.LogFilePath?.FullName, importPath, TiaMcp.Adapters.Contracts.NativeResultStates.TextLists)
                };
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ImportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.GetBaseException().Message}", Meta = ResponseMeta.Unstamped(false) };
            }
        }

        // ExportInstanceTextsToXlsx(file, languages, option) receives all active project languages.
        public ResponseMessage ExportAlarmInstanceTexts(string softwarePath, string exportPath, bool includeInfoText = true, bool includeAdditionalTexts = true, bool includeAlarmClass = true)
        {
            if (_session.IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };
            try
            {
                var provider = plc.GetService<PlcAlarmTextProvider>();
                if (provider == null) return new ResponseMessage { Message = "PlcAlarmTextProvider service not available for this PLC.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false)) };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                var option = PlcAlarmTextXlsxExportOption.None;
                if (includeInfoText) option |= PlcAlarmTextXlsxExportOption.IncludeInfoText;
                if (includeAdditionalTexts) option |= PlcAlarmTextXlsxExportOption.IncludeAdditionalTexts;
                if (includeAlarmClass) option |= PlcAlarmTextXlsxExportOption.IncludeAlarmClass;
                var languages = EngineeringGroupOperations.Items(_session.CurrentProject!.LanguageSettings.ActiveLanguages).Cast<Language>().ToList();
                PlcAlarmTextXlsxResult result = provider.ExportInstanceTextsToXlsx(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), languages, option);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                return new ResponseMessage
                {
                    Message = ok ? $"Alarm instance texts exported to '{exportPath}' (State={state}, languages {string.Join(", ", languages.Select(l => l.Culture?.Name))})." : $"Alarm instance text export reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta = NativeXlsxEvidence(new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["fileExists"] = File.Exists(exportPath), ["option"] = option.ToString(), ["success"] = ok }, state, false, result?.LogFilePath?.FullName, exportPath, TiaMcp.Adapters.Contracts.NativeResultStates.AlarmTexts)
                };
            }
            catch (Exception ex)
            {
                TiaMcp.Logic.V4.CallerInputFiles.RecordExportFailure(ex);
                _session.Logger?.LogError(ex, "ExportAlarmInstanceTexts failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.GetBaseException().Message}", Meta = ResponseMeta.Unstamped(false) };
            }
        }

        private static JsonObject NativeXlsxEvidence(JsonObject meta, string state, bool changesProject, string? log, string path, string enumType)
        {
            NativeResultState.Record(meta, state, changesProject, log, path);
            meta["nativeStateType"] = enumType;
            return meta;
        }

        #endregion

        // ---- alarm text lists XLSX ----------------------------------------------------------------------------------------------------------
        public ResponseMessage ExchangePlcAlarmTextListsXlsx(string softwarePath, string action, string filePath, string unitName = "", string unitKind = "unit",
            string textListNamesJson = "[]", string culturesJson = "[]", string importOption = "None", bool confirmImport = false, bool dryRun = true)
            => _session.RunHmiStepTool("ExchangePlcAlarmTextLists", meta => {
                meta["mayHaveChanged"] = false;
                var request = AlarmTextListRules.ValidateXlsxRequest(action, filePath, unitName, unitKind, textListNamesJson, culturesJson, importOption, confirmImport, dryRun);
                bool writing = request.Writing;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var unit = _session.OptionalUnit(plc, unitName, unitKind);
                PlcAlarmTextListProvider provider = (unit == null ? plc.GetService<PlcAlarmTextListProvider>() : unit.GetService<PlcAlarmTextListProvider>())
                    ?? throw new NotSupportedException("PlcAlarmTextListProvider unavailable on this " + (unit == null ? "PLC" : "unit") + ".");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["owner"] = unit?.Name ?? plc.Name;
                if (action == "export")
                {
                    var file = NativeFileOutput.Plan(filePath);
                    Language[] languages = Array.Empty<Language>();
                    if (request.Cultures.Length > 0)
                    {
                        LanguageComposition available = _session.CurrentProject!.LanguageSettings.Languages;
                        languages = request.Cultures.Select(c => available.Find(CultureInfo.GetCultureInfo(c)) ?? throw new PortalException(PortalErrorCode.NotFound, "Project language not found: " + c + " (available: " + string.Join(", ", EngineeringGroupOperations.Items(available).Cast<Language>().Select(l => l.Culture?.Name)) + ").")).ToArray();
                        meta["languages"] = new JsonArray(languages.Select(l => (JsonNode)l.Culture?.Name).ToArray()); meta["textLists"] = new JsonArray(request.TextLists.Select(t => (JsonNode)t).ToArray());
                    }
                    if (dryRun) return "Text list XLSX export preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true;
                    TextListXlsxResult result = languages.Length == 0 ? provider.ExportToXlsx(file) : provider.ExportToXlsx(file, request.TextLists, languages);
                    meta["apiCallSuccess"] = true; meta["nativeState"] = result?.State.ToString(); meta["logFile"] = result?.LogFilePath?.FullName;
                    NativeResultState.Record(meta, result?.State, false, result?.LogFilePath?.FullName, file.FullName);
                    if (result?.State == TextListXlsxResultState.Error) throw new PortalException(PortalErrorCode.ExportFailed, "ExportToXlsx reported Error (see logFile " + result.LogFilePath?.FullName + ").");
                    meta["file"] = NativeFileOutput.Verify(file);
                    return "Alarm text lists exported to XLSX and hashed; no project change.";
                }
                var source = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["sourceFile"] = SoftwareUnitDeepLogic.FileRow(source);
                var option = (ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOption), typeof(ImportOptions))!;
                meta["importOption"] = option.ToString();
                if (!writing) return "Text list XLSX import preview; no changes.";
                meta["mayHaveChanged"] = true;
                TextListXlsxResult imported = provider.ImportFromXlsx(source, option);
                meta["apiCallSuccess"] = true; meta["nativeState"] = imported?.State.ToString(); meta["logFile"] = imported?.LogFilePath?.FullName;
                NativeResultState.Record(meta, imported?.State, true, imported?.LogFilePath?.FullName, source.FullName);
                if (imported?.State == TextListXlsxResultState.Error) throw new PortalException(PortalErrorCode.ImportFailed, "ImportFromXlsx reported Error (see logFile " + imported.LogFilePath?.FullName + ").");
                return "Alarm text lists imported from XLSX (native state attached); project not saved / compiled.";
            });

        public ResponseMessage ImportPlcAlarmInstanceTexts(string softwarePath, string filePath, string culturesJson, bool dryRun = true)
            => _session.RunHmiStepTool("ImportPlcAlarmInstanceTexts", meta => {
                meta["mayHaveChanged"] = false;
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)); if (!Path.IsPathRooted(filePath) || !file.Exists) throw new FileNotFoundException("Absolute existing xlsx file required.");
                var cultures = PlcBlockServicesLogic.ParseCultureNames(culturesJson);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                var provider = plc.GetService<PlcAlarmTextProvider>() ?? throw new NotSupportedException("PlcAlarmTextProvider unavailable for this PLC/version.");
                var signature = new[] { typeof(FileInfo), typeof(IEnumerable<Language>) };
                if (provider.GetType().GetMethod("ImportInstanceTextsFromXlsx", signature) == null) throw new NotSupportedException("Native ImportInstanceTextsFromXlsx(FileInfo, IEnumerable<Language>) unavailable.");
                var settings = _session.CurrentProject!.LanguageSettings;
                var languages = cultures.Select(c => settings.Languages.Find(c) ?? throw new NotSupportedException("Language not supported by this project: " + c.Name)).ToArray();
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["filePath"] = file.FullName;
                meta["cultures"] = new JsonArray(languages.Select(l => (JsonNode)new JsonObject { ["culture"] = l.Culture.Name, ["active"] = settings.ActiveLanguages.Any(a => a.Culture.Name == l.Culture.Name) }).ToArray());
                if (dryRun) return "Alarm instance text import preview; file contents not applied.";
                meta["mayHaveChanged"] = true;
                var result = (PlcAlarmTextXlsxResult)EngineeringGroupOperations.Call(provider, "ImportInstanceTextsFromXlsx", signature, file, languages);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = EngineeringScalarProperties.Read(result);
                meta["nativeState"] = result.State.ToString(); meta["logFilePath"] = result.LogFilePath?.FullName;
                NativeResultState.Record(meta, result.State, true, result.LogFilePath?.FullName, file.FullName);
                meta["operationSuccess"] = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State); meta["dataComplete"] = false;
                return "Native alarm instance text import returned; inspect nativeState/logFilePath. Text changes require separate export/readback; no save/compile/download.";
            });
        public ResponseMessage ManagePlcAlarmTextList(string softwarePath, string action = "read", string name = "", string libraryName = "", string masterCopyPath = "", string copyMode = "", bool confirmDelete = false, int offset = 0, int limit = 100, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcAlarmTextList", meta => {
                meta["mayHaveChanged"] = false;
                bool writing = PlcBlockServicesLogic.ValidateTextListRequest(action, name, libraryName, masterCopyPath, confirmDelete, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var group = plc.PlcAlarmTextlistGroup ?? throw new NotSupportedException("PlcAlarmTextlistGroup unavailable on this PLC.");
                JsonObject Row(PlcAlarmTextlist list, string kind) { var row = EngineeringScalarProperties.Read(list); row["kind"] = kind; return row; }
                var system = EngineeringGroupOperations.Items(group.PlcAlarmSystemTextlists).Cast<PlcAlarmTextlist>().Select(l => (l, "system"));
                var user = EngineeringGroupOperations.Items(group.PlcAlarmUserTextlists).Cast<PlcAlarmTextlist>().Select(l => (l, "user"));
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["action"] = action;
                if (action == "read")
                {
                    var all = system.Concat(user).ToArray();
                    if (!string.IsNullOrEmpty(name)) { all = all.Where(x => string.Equals(x.Item1.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray(); if (all.Length == 0) throw new PortalException(PortalErrorCode.NotFound, "Exact text list not found: " + name); if (all.Length > 1) throw new InvalidOperationException("Ambiguous text list name: " + name); }
                    var window = PlcBlockServicesLogic.Paginate(meta, all.Length, offset, limit);
                    meta["records"] = new JsonArray(all.Skip(window.Skip).Take(window.Take).Select(x => (JsonNode)Row(x.Item1, x.Item2)).ToArray());
                    meta["apiCallSuccess"] = true; meta["scope"] = "Text list scalar properties (Name, ID, ListRange); entries are not exposed by the Openness API.";
                    return "PLC alarm text lists read; no changes.";
                }
                if (action == "delete")
                {
                    if (system.Any(x => string.Equals(x.Item1.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("System text lists cannot be deleted.");
                    var target = (PlcAlarmUserTextlist)(EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact user text list not found: " + name));
                    meta["before"] = Row(target, "user");
                    if (!writing) return "Text list deletion preview; no changes.";
                    meta["mayHaveChanged"] = true;
                    target.Delete(); meta["apiCallSuccess"] = true;
                    if (EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, name) != null) throw new InvalidOperationException("Text list remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "User text list deleted and absence verified; no save/compile/download.";
                }
                var source = _session.ExactMasterCopy(libraryName, masterCopyPath);
                meta["masterCopy"] = EngineeringScalarProperties.Read(source);
                MasterCopyMode? mode = string.IsNullOrEmpty(copyMode) ? null : (MasterCopyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(copyMode), typeof(MasterCopyMode))!;
                meta["copyMode"] = mode?.ToString();
                if (mode == null && EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, source.Name) != null) throw new InvalidOperationException("A user text list with the master copy name already exists; pass copyMode Rename/Replace explicitly.");
                if (!writing) return "Text list creation preview; no changes.";
                meta["mayHaveChanged"] = true;
                var created = mode == null ? group.PlcAlarmUserTextlists.CreateFrom(source) : group.PlcAlarmUserTextlists.CreateFrom(source, mode.Value);
                meta["apiCallSuccess"] = true;
                if (created == null || EngineeringGroupOperations.Find(group.PlcAlarmUserTextlists, created.Name) == null) throw new InvalidOperationException("Created text list not found by readback.");
                meta["after"] = Row(created, "user");
                return "User text list created from master copy and verified by readback; no save/compile/download.";
            });
    }
}
