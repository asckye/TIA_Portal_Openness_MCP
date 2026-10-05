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

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HardwareAmlService
    {
        private readonly IEngineeringSession _session;

        public HardwareAmlService(IEngineeringSession session)
        {
            _session = session;
        }

        // Read-only: export a device's hardware configuration to an AutomationML (CAx) file.
        // The .aml contains the configured IP address, subnet/mask, PN device name and topology -
        // information not surfaced by GetDeviceItemNetworkInfo (which omits the node Address).
        public ModelContextProtocol.CaxExportResult ExportDeviceConfigurationAml(string devicePath, string exportPath)
        {
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var device = Guard.RequireNotNull(_session.GetDevice(devicePath), "Device", devicePath);

            // Resolve final file path: treat a directory or extension-less path as a folder, else use as-is.
            string filePath;
            if (Directory.Exists(exportPath) || string.IsNullOrEmpty(Path.GetExtension(exportPath)))
            {
                var safeName = string.Concat((device.Name ?? "device").Split(Path.GetInvalidFileNameChars()));
                filePath = Path.Combine(exportPath, $"{safeName}.aml");
            }
            else
            {
                filePath = exportPath;
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(filePath)) File.Delete(filePath);

            var cax = _session.CurrentProject!.GetService<CaxProvider>();
            if (cax == null)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "CAx/AML export service is not available for this project");
            }

            var result = cax.Export(device, new FileInfo(filePath));
            var messageLines = FlattenTransferMessages(result?.Messages);
            var state = result?.State.ToString() ?? "Unknown";
            var ok = result != null && result.State != TransferResultState.Error;

            if (!ok && !File.Exists(filePath))
            {
                throw new PortalException(PortalErrorCode.ExportFailed,
                    $"CAx/AML export failed (state={state}): " + (messageLines.Count > 0 ? string.Join(" | ", messageLines) : "no detail returned"));
            }

            return new ModelContextProtocol.CaxExportResult
            {
                DeviceName = device.Name ?? devicePath,
                FilePath = filePath,
                Success = ok,
                State = state,
                ErrorCount = result?.ErrorCount ?? 0,
                WarningCount = result?.WarningCount ?? 0,
                Messages = messageLines
            };
        }

        private static List<string> FlattenTransferMessages(IEnumerable<TransferResultMessage>? messages)
        {
            var lines = new List<string>();
            void Walk(IEnumerable<TransferResultMessage>? ms)
            {
                if (ms == null) return;
                foreach (var m in ms)
                {
                    if (m == null) continue;
                    lines.Add($"[{m.State}] {m.Message}");
                    try { Walk(m.Messages); } catch /* swallow(enumerate-optional): Unavailable nested CAx messages must not discard the parent transfer result. */ { }
                }
            }
            Walk(messages);
            return lines;
        }

        public ResponseMessage ImportDeviceAml(string filePath, string logFilePath, string importOption = "RetainTiaDevice", bool confirmImport = false, bool dryRun = true)
            => _session.RunHmiStepTool("ImportDeviceAml", meta => {
                var option = (CaxImportOptions)Enum.Parse(typeof(CaxImportOptions), HardwareServicesLogic.RequireOneOf(importOption, HardwareServicesLogic.CaxImportOptions, "importOption"));
                HardwareServicesLogic.RequireConfirmation(confirmImport, "confirmImport", dryRun);
                var source = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath");
                var log = NativeFileOutput.Plan(logFilePath);
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var cax = _session.CurrentProject!.GetService<CaxProvider>() ?? throw new NotSupportedException("CaxProvider unavailable for this project.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["importOption"] = option.ToString();
                meta["sourceFile"] = NativeFileOutput.Verify(source); meta["plannedLogFile"] = log.FullName;
                meta["deviceCountBefore"] = _session.EnumerateAllDevices().Count();
                if (dryRun) return "CAx/AutomationML import preview; source hashed, nothing imported.";
                meta["mayHaveChanged"] = true; meta["mayHaveWrittenFiles"] = true;
                bool ok = cax.Import(source, log, option);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = ok; meta["deviceCountAfter"] = _session.EnumerateAllDevices().Count();
                log.Refresh();
                if (log.Exists && log.Length > 0) meta["logFile"] = NativeFileOutput.Verify(log); else { meta["logFile"] = null; meta["logFileMissing"] = true; }
                if (!ok) throw new PortalException(PortalErrorCode.ImportFailed, "CaxProvider.Import returned false; inspect the native log file.");
                return "CAx/AutomationML import returned true (native log hashed); imported content not semantically verified. Project not saved, compiled or downloaded.";
            });
    }
}
