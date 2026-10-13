using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaOpenness.Shared;
using TiaMcpServer.ModelContextProtocol;
#if OPTION_SINUMERIK_ARCHIVE
using Siemens.Engineering.HW.Utilities;
#endif
#if OPTION_SINUMERIK_SAFETY || OPTION_SINUMERIK_ALARMS
using Siemens.Engineering.MC.Sinumerik;
#endif
#if OPTION_SIMOTION
using Siemens.Engineering.Simotion;
#endif
#if OPTION_SCADA
using Siemens.Engineering.SCADAExporter;
#endif

namespace TiaMcp.Adapters.Native.Options;

internal sealed class OptionalProductsAdapter
{
    private readonly OptionalProductSession _session;
    internal OptionalProductsAdapter(OptionalProductSession session) => _session = session;
        private static FileInfo OptionFile(string path, string extension, bool input)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Expected an absolute " + extension + " file path on the server.");
            var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
            if (input && !file.Exists) throw new FileNotFoundException("Input file missing.", path);
            if (!input && file.Exists) throw new IOException("Output file exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Parent directory must already exist.");
            return file;
        }
        private static void VerifyOptionOutput(FileInfo file, HmiObject meta)
        {
            file.Refresh();
            if (!file.Exists || file.Length == 0) throw new IOException("Native call returned without a nonempty output file.");
            meta["output"] = HmiFiles.FileRow(file);
        }
        public OptionalProductReply ManageSinumerikArchive(string action, string filePath, string[] devicePathJson = null!, string[] itemPathJson = null!,
            string[] modifiedDevicePathJson = null!, string[] modifiedItemPathJson = null!, string mode = "HardwareAndAllProgramBlocks", string comment = "", string author = "", string password = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageSinumerikArchive", meta => {
                try
                {
#if !OPTION_SINUMERIK_ARCHIVE
                throw new NotSupportedException("SinumerikArchiveProvider is absent from the supplied V21 SDK; use the V20 engine with the appropriate SINUMERIK option.");
#else
                // Refuse unavailable overloads before resolving the native provider or hardware.
#if !OPTION_ARCHIVE_F_ADDRESS
                if (action == "fAddressArchive") throw new NotSupportedException("CreateFAddressAssignmentArchive is unavailable on this release.");
#endif
#if !OPTION_ARCHIVE_PASSWORD
                if (!string.IsNullOrEmpty(password)) throw new NotSupportedException("The requested archive password overload is unavailable on this release.");
#endif
#if !OPTION_ARCHIVE_RETRIEVE_PASSWORD
                if (action == "retrieve" && !string.IsNullOrEmpty(password)) throw new NotSupportedException("The requested retrieve password overload is unavailable on this release.");
#endif
                if (action != "archive" && action != "retrieve" && action != "fAddressArchive") throw new ArgumentException("action: archive/retrieve/fAddressArchive.");
                var file = OptionFile(filePath, ".dsf", action == "retrieve");
                var archiveMode = (SinumerikArchivationMode)HmiScalar.ConvertValue(mode, typeof(SinumerikArchivationMode))!;
                using var access = !dryRun ? _session.AcquireHmiEditAccess() : null;
                var provider = _session.RequireHardwareUtility<SinumerikArchiveProvider>("SinumerikArchiveProvider");
                DeviceItem? plc = null, modified = null;
                if (action != "retrieve") plc = _session.ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("itemPathJson must select the NCU PLC device item.");
                if (action == "fAddressArchive") modified = _session.ExactEngineeringHardware(modifiedDevicePathJson, modifiedItemPathJson) as DeviceItem ?? throw new ArgumentException("modifiedItemPathJson must select the modified PLC device item.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["filePath"] = file.FullName; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "SINUMERIK archive operation preview; no native archive/retrieve call.";
                using var secure = string.IsNullOrEmpty(password) ? null : EngineeringCredentialRules.ToSecureString(password);
                if (action == "retrieve")
                {
                    meta["mayHaveChanged"] = true;
                    #if OPTION_ARCHIVE_DEVICE_RESULT
                    var device = InvocationJournal.Native("SinumerikArchive.Retrieve", () =>
                    {
#if OPTION_ARCHIVE_RETRIEVE_PASSWORD
                        return secure == null ? provider.Retrieve(file) : provider.Retrieve(file, secure);
#else
                        if (secure != null) throw new NotSupportedException("The requested retrieve password overload is unavailable on this release.");
                        return provider.Retrieve(file);
#endif
                    });
                    if (device == null) throw new InvalidOperationException("Retrieve returned no device.");
                    meta["retrievedDevice"] = device.Name; meta["contentVerified"] = null;
#else
                    if (secure != null) throw new NotSupportedException("The requested retrieve password overload is unavailable on this release.");
                    InvocationJournal.Native("SinumerikArchive.Retrieve", () => provider.Retrieve(file));
                    meta["contentVerified"] = null;
                    meta["nativeSignature"] = "SinumerikArchiveProvider.Retrieve(FileInfo) -> void";
                    return "SINUMERIK archive retrieval returned; this release returns no device identity. Project not saved.";
#endif
                    return "SINUMERIK device retrieved into the project; native returned identity read. Project not saved.";
                }
                meta["mayHaveWrittenFiles"] = true;
                InvocationJournal.Native("SinumerikArchive." + action, () => {
                    if (action == "archive")
                    {
                        if (secure == null) provider.Archive(plc!, file, archiveMode, comment, author);
#if OPTION_ARCHIVE_PASSWORD
                        else provider.Archive(plc!, file, archiveMode, comment, author, secure);
#else
                        else throw new NotSupportedException("The requested archive password overload is unavailable on this release.");
#endif
                    }
#if OPTION_ARCHIVE_F_ADDRESS
                    else if (secure == null) provider.CreateFAddressAssignmentArchive(plc!, modified!, file, comment, author);
#if OPTION_ARCHIVE_PASSWORD
                    else provider.CreateFAddressAssignmentArchive(plc!, modified!, file, comment, author, secure);
#else
                    else throw new NotSupportedException("The requested F-address password overload is unavailable on this release.");
#endif
#else
                    else throw new NotSupportedException("CreateFAddressAssignmentArchive is unavailable on this release.");
#endif
                });
                VerifyOptionOutput(file, meta);
                return "SINUMERIK archive written and hashed; archive content has not been deployed or validated on a controller.";
#endif
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            }, requiresProject: true);

        public OptionalProductReply ImportSinumerikAlarmTexts(string[] devicePathJson, string filesJson, bool dryRun = true)
            => _session.RunHmiStepTool("ImportSinumerikAlarmTexts", meta => {
                try
                {
#if !OPTION_SINUMERIK_ALARMS
                throw new NotSupportedException("SinumerikAlarmTextProvider is absent from the supplied V21 SDK.");
#else
                var names = _session.Files();
                if (names.Length < 1 || names.Length > 200) throw new ArgumentException("Supply 1..200 alarm text files.");
                var files = names.Select(n => n ?? throw new ArgumentException("File paths cannot be null.")).Select(p => {
                    var extension = Path.GetExtension(p).ToLowerInvariant();
                    if (extension != ".ts" && extension != ".csv") throw new ArgumentException("Alarm files must be TS or CSV.");
                    return OptionFile(p, extension, true);
                }).ToArray();
                using var access = !dryRun ? _session.AcquireHmiEditAccess() : null;
                var device = _session.ExactEngineeringHardware(devicePathJson, Array.Empty<string>()) as Device ?? throw new ArgumentException("Select a SINUMERIK NCU device.");
                var provider = InvocationJournal.Native("SinumerikAlarmText.GetService", () => device.GetService<SinumerikAlarmTextProvider>()) ?? throw new NotSupportedException("The selected device has no SinumerikAlarmTextProvider.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["inputCount"] = files.Length;
                meta["replacementScope"] = "SINUMERIK DB2 alarm texts; native import can replace existing texts. Required languages must already be active.";
                if (dryRun) return "Alarm import preview; no text imported.";
                meta["mayHaveChanged"] = true;
                InvocationJournal.Native("SinumerikAlarmText.ImportAlarmTexts", () => provider.AlarmTextImporter.ImportAlarmTexts(files));
                meta["apiCallSuccess"] = true; meta["contentVerified"] = null;
                return "Native alarm-text import returned; text contents require project readback/acceptance. Project not saved.";
#endif
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            }, requiresProject: true);

        public OptionalProductReply ManageSinumerikSafetyMode(string[] devicePathJson, string action = "read", string mode = "", bool dryRun = true, bool confirmSafetyChange = false)
            => _session.RunHmiStepTool("ManageSinumerikSafetyMode", meta => {
                try
                {
#if !OPTION_SINUMERIK_SAFETY
                throw new NotSupportedException("SafetyModeProvider is absent from the supplied V21 SDK.");
#else
                if (action != "read" && action != "set") throw new ArgumentException("action: read/set.");
                bool writing = action == "set" && !dryRun;
                if (writing && !confirmSafetyChange) throw new ArgumentException("Setting SINUMERIK safety mode requires confirmSafetyChange=true and dryRun=false.");
                SafetyMode requested = action == "set" ? (SafetyMode)HmiScalar.ConvertValue(mode, typeof(SafetyMode))! : default;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var device = _session.ExactEngineeringHardware(devicePathJson, Array.Empty<string>()) as Device ?? throw new ArgumentException("Select a SINUMERIK NCU device.");
                var provider = InvocationJournal.Native("SinumerikSafety.GetService", () => device.GetService<SafetyModeProvider>()) ?? throw new NotSupportedException("The selected device has no SafetyModeProvider.");
                meta["before"] = provider.CurrentMode.ToString(); meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["preconditions"] = "NCU PLC must be offline; the native API checks configuration eligibility. Switching affects safety/telegram configuration and requires engineering acceptance.";
                if (!writing) return action == "read" ? "Current SINUMERIK safety mode read." : "Safety mode preview; no change.";
                // Siemens performs the offline/configuration check on the actual NCU; do not go offline automatically or target a different PLC.
                meta["mayHaveChanged"] = true;
                InvocationJournal.Native("SinumerikSafety.SetSafetyMode", () => provider.SetSafetyMode(requested));
                var after = InvocationJournal.Native("SinumerikSafety.CurrentMode", () => provider.CurrentMode);
                meta["after"] = after.ToString();
                if (after != requested) throw new InvalidOperationException("Safety mode readback differs; do not retry blindly.");
                return "SINUMERIK safety mode changed and read back; project not saved, compiled or downloaded.";
#endif
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            }, requiresProject: true);

        public OptionalProductReply InitializeSimotionScripting(bool dryRun = true)
            => _session.RunHmiStepTool("InitializeSimotionScripting", meta => {
                try
                {
#if !OPTION_SIMOTION
                throw new NotSupportedException("SimotionProvider is absent from the supplied V21 SDK.");
#else
                var provider = InvocationJournal.Native("Simotion.GetService", () => _session.CurrentProject!.GetService<SimotionProvider>()) ?? throw new NotSupportedException("Project does not provide SimotionProvider; SIMOTION SCOUT TIA must be installed.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (dryRun) return "SIMOTION scripting initialization preview; no Initialize call.";
                meta["operationStarted"] = true;
                var result = InvocationJournal.Native("Simotion.Initialize", () => provider.Initialize());
                meta["initializationResult"] = result; meta["apiCallSuccess"] = true;
                return "SIMOTION scripting initialized; the returned string is passed through, never executed. External SCOUT scripting is outside this tool.";
#endif
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            }, requiresProject: true);

        public OptionalProductReply ExportScadaData(string filePath, string softwarePath = "", bool dryRun = true)
            => _session.RunHmiStepTool("ExportScadaData", meta => {
                try
                {
#if !OPTION_SCADA
                throw new NotSupportedException("ScadaExportProvider is absent from the supplied V21 SDK.");
#else
                var file = OptionFile(filePath, ".zip", false);
                IEngineeringServiceProvider owner = string.IsNullOrEmpty(softwarePath) ? (IEngineeringServiceProvider)_session.CurrentProject! : _session.ExactPlcForEngineering(softwarePath, false);
                var provider = InvocationJournal.Native("ScadaExport.GetService", () => owner.GetService<ScadaExportProvider>()) ?? throw new NotSupportedException("ScadaExportProvider unavailable; install SIMATIC SCADA Export for TIA Portal and select a supported project/PLC.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "SCADA PLC configuration export preview; no ZIP written.";
                meta["mayHaveWrittenFiles"] = true;
                InvocationJournal.Native("ScadaExport.Export", () => provider.Export(file));
                VerifyOptionOutput(file, meta);
                return "PLC configuration exported to SCADA ZIP and hashed; no runtime deployment.";
#endif
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            }, requiresProject: true);
}
