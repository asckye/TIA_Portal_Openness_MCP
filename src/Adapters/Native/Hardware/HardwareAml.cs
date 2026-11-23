using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            global::Siemens.Engineering.HW.Device? device;
            CaxProvider cax;
            string filePath;
            FileInfo target;
#if !PLC_CAX_TRANSFER_RESULT
            FileInfo log;
#endif
            try
            {
                Check();
                if (HardwareProjectMissing())
                    throw new HardwareAddressingException("InvalidState", "No project is open in TIA Portal");
                device = ResolveEngineeringDevice != null ? ResolveEngineeringDevice(devicePath) as global::Siemens.Engineering.HW.Device
                    : HardwareProjectMissing() ? null : HardwareLegacyDevice(devicePath);
                if (device == null) throw new HardwareAddressingException("NotFound", "Device not found: " + devicePath);
                if (Directory.Exists(exportPath) || string.IsNullOrEmpty(Path.GetExtension(exportPath)))
                {
                    var safeName = string.Concat((device.Name ?? "device").Split(Path.GetInvalidFileNameChars()));
                    filePath = Path.Combine(exportPath, $"{safeName}.aml");
                }
                else filePath = exportPath;
                if (AdmitHardwareAmlExport != null) AdmitHardwareAmlExport(filePath);
                else HardwareAmlPolicy.RequireExportTarget(filePath);
                cax = project!.GetService<CaxProvider>()
                    ?? throw new HardwareAddressingException("InvalidState", "CAx/AML export service is not available for this project");
#if !PLC_CAX_TRANSFER_RESULT
                log = HardwareAmlPolicy.Plan(filePath + "." + Guid.NewGuid().ToString("N") + ".log", "exportPath");
#endif
                target = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
            }
            catch (Exception ex)
            {
                ex.Data["foundationKnownNoMutation"] = true;
                ex.Data["phase"] = "export-admission";
                throw;
            }
            try
            {
#if PLC_CAX_TRANSFER_RESULT
                var result = cax.Export(device, target);
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
                bool ok = cax.Export(device, target, log);
                return new HardwareAmlExportReply { DeviceName = device.Name ?? devicePath, FilePath = filePath,
                    Success = ok, State = ok.ToString(), NativeBooleanResult = ok,
                    Messages = new List<string> { "CAx Export returned bool; native log: " + log.FullName } };
#endif
            }
            catch (Exception ex)
            {
                ex.Data["exportPhase"] = "native-export";
                ex.Data["outputFile"] = target.FullName;
                throw;
            }
        }

#if PLC_CAX_TRANSFER_RESULT
        private static List<string> FlattenHardwareAmlMessages(IEnumerable<TransferResultMessage>? messages, int limit = int.MaxValue)
        {
            var lines = new List<string>();
            void Walk(IEnumerable<TransferResultMessage>? ms)
            {
                if (ms == null) return;
                foreach (var m in ms)
                {
                    if (lines.Count >= limit) break;
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
                var log = HardwareAmlPolicy.Plan(logFilePath, nameof(logFilePath));
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false;
                if (!dryRun) HardwareWriteAdmission();
                var cax = project!.GetService<CaxProvider>() ?? throw new NotSupportedException("CaxProvider unavailable for this project.");
                meta["importOption"] = option.ToString();
                meta["sourceFile"] = HardwareAmlPolicy.Verify(source); meta["plannedLogFile"] = log.FullName;
                meta["deviceCountBefore"] = EnumerateAllDevices().Count();
                if (dryRun) return "CAx/AutomationML import preview; source hashed, nothing imported.";
                meta["phase"] = "import";
                bool returnedFailure = false;
                try
                {
#if PLC_CAX_TRANSFER_RESULT
                // V19+: Import(FileInfo, CaxImportOptions) returns a TransferResult with structured messages and no log file.
                meta["mayHaveChanged"] = true;
                var result = cax.Import(source, option);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result != null && NativeResultStates.Succeeded(result.State);
                returnedFailure = !ok;
                meta["apiCallSuccess"] = true; meta["nativeResult"] = state;
                meta["errorCount"] = result?.ErrorCount ?? 0; meta["warningCount"] = result?.WarningCount ?? 0;
                var messages = FlattenHardwareAmlMessages(result?.Messages, ok ? int.MaxValue : 40);
                meta["messages"] = ok ? messages : DiagnosticLines(messages);
                meta["deviceCountAfter"] = EnumerateAllDevices().Count();
                meta["logFile"] = null; meta["logFileScope"] = "V19+ reports the TransferResult messages; logFilePath is not written by this overload.";
                if (!ok) return ImportFailed(meta);
                return "CAx/AutomationML import returned TransferResult " + state
                    + " (messages reported); imported content not semantically verified. Project not saved, compiled or downloaded.";
#else
                meta["mayHaveChanged"] = true; meta["mayHaveWrittenFiles"] = true;
                bool ok = cax.Import(source, log, option);
                returnedFailure = !ok;
                meta["apiCallSuccess"] = true; meta["nativeResult"] = ok;
                meta["deviceCountAfter"] = EnumerateAllDevices().Count();
                log.Refresh();
                if (log.Exists && log.Length > 0) meta["logFile"] = HardwareAmlPolicy.Verify(log);
                else { meta["logFile"] = null; meta["logFileMissing"] = true; }
                if (!ok)
                {
                    if (log.Exists && log.Length > 0)
                        meta["logExcerpt"] = ReadLogExcerpt(log, Encoding.Default);
                    return ImportFailed(meta);
                }
                return "CAx/AutomationML import returned true (native log hashed); imported content not semantically verified. Project not saved, compiled " +
                    "or downloaded.";
#endif
                }
                catch (Exception error) when (returnedFailure && !HmiReadPolicy.ConnectionUnavailable(error))
                {
                    meta["diagnosticReadFailed"] = true;
                    return ImportFailed(meta);
                }
                catch
                {
                    try { meta["deviceCountAfter"] = EnumerateAllDevices().Count(); }
                    catch { /* swallow(probe-optional): import remains unknown when even the device count is unavailable. */ }
                    throw;
                }
            }, writing: !dryRun);
        }

#if !PLC_CAX_TRANSFER_RESULT
        internal static List<string> ReadLogExcerpt(FileInfo log, Encoding fallback)
        {
            using var file = log.OpenRead();
            using var reader = new BinaryReader(file);
            var bytes = reader.ReadBytes(4096);
            bool complete = file.Position == file.Length;

            string Decode(Encoding encoding, int offset)
            {
                var chars = new char[encoding.GetMaxCharCount(bytes.Length - offset)];
                // At the byte limit the decoder retains any unfinished character.
                int count = encoding.GetDecoder().GetChars(bytes, offset, bytes.Length - offset, chars, 0, flush: complete);
                return new string(chars, 0, count);
            }

            // UTF-32 LE has the same first two bytes as UTF-16 LE.
            var bom = new[] { Encoding.UTF32, new UTF32Encoding(true, true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF8 }
                .FirstOrDefault(encoding => bytes.Take(encoding.GetPreamble().Length).SequenceEqual(encoding.GetPreamble()));
            string text;
            if (bom != null) text = Decode(bom, bom.GetPreamble().Length);
            else
            {
                try { text = Decode(new UTF8Encoding(false, true), 0); }
                catch (DecoderFallbackException)
                {
                    /* swallow(parse-fallback): Legacy CAx logs with invalid UTF-8 use the Windows ANSI code page. */
                    text = Decode(fallback, 0);
                }
            }
            using var prefix = new StringReader(text);
            var lines = new List<string>();
            while (lines.Count < 40 && prefix.ReadLine() is string line) lines.Add(line);
            return DiagnosticLines(lines);
        }
#endif

        private static string ImportFailed(Dictionary<string, object?> meta)
        {
            meta["operationSuccess"] = false; meta["status"] = "ImportFailed"; meta["dataComplete"] = false;
            meta["cleanupRequired"] = new[] { new Dictionary<string, object?> { ["action"] = "inspect devices per log" } };
            return "CAx import returned an unsuccessful result. Inspect devices per log for partial changes.";
        }

        private static List<string> DiagnosticLines(IEnumerable<string> lines)
        {
            return NativeFailureText.BoundedLines(lines.TakeWhile(line => !line.Contains("-----BEGIN")
                && !NativeFailureText.IsStackBoundary(line)), line => {
                string text = NativeFailureText.Compose(line, null, Array.Empty<string>());
                int end = text.IndexOf("] ", StringComparison.Ordinal);
                if (text.StartsWith("[", StringComparison.Ordinal) && end > 0) text = text.Substring(1, end - 1) + ": " + text.Substring(end + 2);
                return NativeFailureText.WithoutLocalPaths(text);
            });
        }
    }
}
