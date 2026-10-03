using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcpServer.ModelContextProtocol;
#if TIA_V20
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.MC.Sinumerik;
using Siemens.Engineering.Simotion;
using Siemens.Engineering.SCADAExporter;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class V20OptionsService
    {
        private readonly IEngineeringSession _session;

        public V20OptionsService(IEngineeringSession session) => _session = session;

        private static FileInfo OptionFile(string path, string extension, bool input)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Expected an absolute " + extension + " file path on the server.");
            var file = new FileInfo(path);
            if (input && !file.Exists) throw new FileNotFoundException("Input file missing.", path);
            if (!input && file.Exists) throw new IOException("Output file exists; overwrite refused.");
            if (file.Directory?.Exists != true) throw new DirectoryNotFoundException("Parent directory must already exist.");
            return file;
        }
        private static void VerifyOptionOutput(FileInfo file, JsonObject meta)
        {
            file.Refresh();
            if (!file.Exists || file.Length == 0) throw new IOException("Native call returned without a nonempty output file.");
            meta["output"] = SoftwareUnitDeepLogic.FileRow(file);
        }
        public ResponseMessage ManageSinumerikArchive(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]",
            string modifiedDevicePathJson = "[]", string modifiedItemPathJson = "[]", string mode = "HardwareAndAllProgramBlocks", string comment = "", string author = "", string password = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageSinumerikArchive", meta => {
#if !TIA_V20
                throw new NotSupportedException("SinumerikArchiveProvider is absent from the supplied V21 SDK; use the V20 engine with the appropriate SINUMERIK option.");
#else
                if (action != "archive" && action != "retrieve" && action != "fAddressArchive") throw new ArgumentException("action: archive/retrieve/fAddressArchive.");
                var file = OptionFile(filePath, ".dsf", action == "retrieve");
                var archiveMode = (SinumerikArchivationMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(mode), typeof(SinumerikArchivationMode))!;
                using var access = !dryRun ? _session.AcquireHmiEditAccess() : null;
                var provider = _session.RequireHardwareUtility<SinumerikArchiveProvider>("SinumerikArchiveProvider");
                DeviceItem? plc = null, modified = null;
                if (action != "retrieve") plc = _session.ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("itemPathJson must select the NCU PLC device item.");
                if (action == "fAddressArchive") modified = _session.ExactEngineeringHardware(modifiedDevicePathJson, modifiedItemPathJson) as DeviceItem ?? throw new ArgumentException("modifiedItemPathJson must select the modified PLC device item.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["filePath"] = file.FullName; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (dryRun) return "SINUMERIK archive operation preview; no native archive/retrieve call.";
                using var secure = string.IsNullOrEmpty(password) ? null : PlcBlockServicesLogic.ToSecureString(password);
                if (action == "retrieve")
                {
                    meta["mayHaveChanged"] = true;
                    var device = InvocationJournal.Native("SinumerikArchive.Retrieve", () => secure == null ? provider.Retrieve(file) : provider.Retrieve(file, secure));
                    if (device == null) throw new InvalidOperationException("Retrieve returned no device.");
                    meta["retrievedDevice"] = device.Name; meta["contentVerified"] = null;
                    return "SINUMERIK device retrieved into the project; native returned identity read. Project not saved.";
                }
                meta["mayHaveWrittenFiles"] = true;
                InvocationJournal.Native("SinumerikArchive." + action, () => {
                    if (action == "archive")
                    {
                        if (secure == null) provider.Archive(plc!, file, archiveMode, comment, author);
                        else provider.Archive(plc!, file, archiveMode, comment, author, secure);
                    }
                    else if (secure == null) provider.CreateFAddressAssignmentArchive(plc!, modified!, file, comment, author);
                    else provider.CreateFAddressAssignmentArchive(plc!, modified!, file, comment, author, secure);
                });
                VerifyOptionOutput(file, meta);
                return "SINUMERIK archive written and hashed; archive content has not been deployed or validated on a controller.";
#endif
            }, requiresProject: Engineering.TiaMajorVersion == 20);

        public ResponseMessage ImportSinumerikAlarmTexts(string devicePathJson, string filesJson, bool dryRun = true)
            => _session.RunHmiStepTool("ImportSinumerikAlarmTexts", meta => {
#if !TIA_V20
                throw new NotSupportedException("SinumerikAlarmTextProvider is absent from the supplied V21 SDK.");
#else
                var names = JsonNode.Parse(filesJson) as JsonArray ?? throw new ArgumentException("filesJson must be an array of absolute TS/CSV paths.");
                if (names.Count < 1 || names.Count > 200) throw new ArgumentException("Supply 1..200 alarm text files.");
                var files = names.Select(n => n?.GetValue<string>() ?? throw new ArgumentException("File paths cannot be null.")).Select(p => {
                    var extension = Path.GetExtension(p).ToLowerInvariant();
                    if (extension != ".ts" && extension != ".csv") throw new ArgumentException("Alarm files must be TS or CSV.");
                    return OptionFile(p, extension, true);
                }).ToArray();
                using var access = !dryRun ? _session.AcquireHmiEditAccess() : null;
                var device = _session.ExactEngineeringHardware(devicePathJson, "[]") as Device ?? throw new ArgumentException("Select a SINUMERIK NCU device.");
                var provider = InvocationJournal.Native("SinumerikAlarmText.GetService", () => device.GetService<SinumerikAlarmTextProvider>()) ?? throw new NotSupportedException("The selected device has no SinumerikAlarmTextProvider.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["inputCount"] = files.Length;
                meta["replacementScope"] = "SINUMERIK DB2 alarm texts; native import can replace existing texts. Required languages must already be active.";
                if (dryRun) return "Alarm import preview; no text imported.";
                meta["mayHaveChanged"] = true;
                InvocationJournal.Native("SinumerikAlarmText.ImportAlarmTexts", () => provider.AlarmTextImporter.ImportAlarmTexts(files));
                meta["apiCallSuccess"] = true; meta["contentVerified"] = null;
                return "Native alarm-text import returned; text contents require project readback/acceptance. Project not saved.";
#endif
            }, requiresProject: Engineering.TiaMajorVersion == 20);

        public ResponseMessage ManageSinumerikSafetyMode(string devicePathJson, string action = "read", string mode = "", bool dryRun = true, bool confirmSafetyChange = false)
            => _session.RunHmiStepTool("ManageSinumerikSafetyMode", meta => {
#if !TIA_V20
                throw new NotSupportedException("SafetyModeProvider is absent from the supplied V21 SDK.");
#else
                if (action != "read" && action != "set") throw new ArgumentException("action: read/set.");
                bool writing = action == "set" && !dryRun;
                if (writing && !confirmSafetyChange) throw new ArgumentException("Setting SINUMERIK safety mode requires confirmSafetyChange=true and dryRun=false.");
                SafetyMode requested = action == "set" ? (SafetyMode)EngineeringScalarProperties.ConvertValue(JsonValue.Create(mode), typeof(SafetyMode))! : default;
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var device = _session.ExactEngineeringHardware(devicePathJson, "[]") as Device ?? throw new ArgumentException("Select a SINUMERIK NCU device.");
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
            }, requiresProject: Engineering.TiaMajorVersion == 20);

        public ResponseMessage InitializeSimotionScripting(bool dryRun = true)
            => _session.RunHmiStepTool("InitializeSimotionScripting", meta => {
#if !TIA_V20
                throw new NotSupportedException("SimotionProvider is absent from the supplied V21 SDK.");
#else
                var provider = InvocationJournal.Native("Simotion.GetService", () => _session.CurrentProject!.GetService<SimotionProvider>()) ?? throw new NotSupportedException("Project does not provide SimotionProvider; SIMOTION SCOUT TIA must be installed.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (dryRun) return "SIMOTION scripting initialization preview; no Initialize call.";
                var result = InvocationJournal.Native("Simotion.Initialize", () => provider.Initialize());
                meta["initializationResult"] = result; meta["apiCallSuccess"] = true;
                return "SIMOTION scripting initialized; the returned string is passed through, never executed. External SCOUT scripting is outside this tool.";
#endif
            }, requiresProject: Engineering.TiaMajorVersion == 20);

        public ResponseMessage ExportScadaData(string filePath, string softwarePath = "", bool dryRun = true)
            => _session.RunHmiStepTool("ExportScadaData", meta => {
#if !TIA_V20
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
            }, requiresProject: Engineering.TiaMajorVersion == 20);
    }
}
