using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // These managed hooks preserve the original worker's file admission and
        // binding checks at their original points without a host policy dependency.
        public Action<string>? AdmitHardwareAmlExport { get; set; }
        public Func<string, object?>? ResolveEngineeringDevice { get; set; }

        public HardwareAmlExportReply ExportHardwareAml(string devicePath, string exportPath)
        {
            Check();
            if (HardwareProjectMissing())
                throw new HardwareAddressingException("InvalidState", "No project is open in TIA Portal");
            var device = ResolveEngineeringDevice != null ? ResolveEngineeringDevice(devicePath) as global::Siemens.Engineering.HW.Device
                : HardwareProjectMissing() ? null : HardwareLegacyDevice(devicePath);
            if (device == null) throw new HardwareAddressingException("NotFound", "Device not found: " + devicePath);
            string filePath;
            if (Directory.Exists(exportPath) || string.IsNullOrEmpty(Path.GetExtension(exportPath)))
            {
                var safeName = string.Concat((device.Name ?? "device").Split(Path.GetInvalidFileNameChars()));
                filePath = Path.Combine(exportPath, $"{safeName}.aml");
            }
            else filePath = exportPath;
            if (AdmitHardwareAmlExport != null) AdmitHardwareAmlExport(filePath);
            else HardwareAmlPolicy.RequireExportTarget(filePath);
            var cax = project!.GetService<CaxProvider>();
            if (cax == null)
                throw new HardwareAddressingException("InvalidState", "CAx/AML export service is not available for this project");
#if PLC_CAX_TRANSFER_RESULT
            var result = cax.Export(device, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)));
            var lines = FlattenHardwareAmlMessages(result?.Messages);
            var state = result?.State.ToString() ?? "Unknown";
            var ok = result != null && NativeResultStates.Succeeded(result.State);
            return new HardwareAmlExportReply {
                DeviceName = device.Name ?? devicePath, FilePath = filePath, Success = ok, State = state,
                ErrorCount = result?.ErrorCount ?? 0, WarningCount = result?.WarningCount ?? 0, Messages = lines
            };
#else
            // V14 SP1-V18 expose the bool overload with a native log file.
            // Keep its bool evidence distinct from the later TransferResult enum.
            var log = HardwareAmlPolicy.Plan(filePath + "." + Guid.NewGuid().ToString("N") + ".log");
            bool ok = cax.Export(device, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)), log);
            return new HardwareAmlExportReply { DeviceName = device.Name ?? devicePath, FilePath = filePath,
                Success = ok, State = ok.ToString(), NativeBooleanResult = ok,
                Messages = new List<string> { "CAx Export returned bool; native log: " + log.FullName } };
#endif
        }

#if PLC_CAX_TRANSFER_RESULT
        private static List<string> FlattenHardwareAmlMessages(IEnumerable<TransferResultMessage>? messages)
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
#endif

        public HardwareAddressingReply ImportHardwareAml(string filePath, string logFilePath,
            string importOption = "RetainTiaDevice", bool confirmImport = false, bool dryRun = true)
        {
            Check();
            return RunHardwareAddressStep("ImportDeviceAml", meta => {
                var option = HardwareAmlPolicy.ImportOption(importOption);
                HardwareAmlPolicy.RequireConfirmation(confirmImport, dryRun);
                var source = HardwareAmlPolicy.InputFile(filePath);
                var log = HardwareAmlPolicy.Plan(logFilePath);
                using var exclusive = dryRun ? null : HardwareEditAccess();
                var cax = project!.GetService<CaxProvider>() ?? throw new NotSupportedException("CaxProvider unavailable for this project.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                meta["importOption"] = option.ToString();
                meta["sourceFile"] = HardwareAmlPolicy.Verify(source); meta["plannedLogFile"] = log.FullName;
                meta["deviceCountBefore"] = EnumerateAllDevices().Count();
                if (dryRun) return "CAx/AutomationML import preview; source hashed, nothing imported.";
                meta["mayHaveChanged"] = true; meta["mayHaveWrittenFiles"] = true;
                bool ok = cax.Import(source, log, option);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = ok;
                meta["deviceCountAfter"] = EnumerateAllDevices().Count();
                log.Refresh();
                if (log.Exists && log.Length > 0) meta["logFile"] = HardwareAmlPolicy.Verify(log);
                else { meta["logFile"] = null; meta["logFileMissing"] = true; }
                if (!ok) throw new HardwareAddressingException("ImportFailed", "CaxProvider.Import returned false; inspect the native log file.");
                return "CAx/AutomationML import returned true (native log hashed); imported content not semantically verified. Project not saved, compiled or downloaded.";
            });
        }
    }
}
