using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.V4
{
    internal static class CallerInputFiles
    {
        // These argument names denote caller files, never engineering object paths.
        // Output checks use the explicit native-format table, not the input-file heuristic.
        internal static void Validate(string tool, JsonObject arguments)
        {
            ValidateNativeFile(tool, arguments);
            string? action = arguments["action"] is JsonValue actionValue && actionValue.TryGetValue<string>(out var actionText) ? actionText : null;
            bool import = tool.StartsWith("Import", StringComparison.Ordinal) || tool == "PlanPlcExternalSourceImport"
                || tool == "RepairAndReimportPlcBlock"
                || action != null && (action.StartsWith("import", StringComparison.Ordinal) || action is "createFromFile" or "install" or "installAndGetIdentifier")
                || tool == "ManageGlobalLibrary" && action is "open" or "openInfo" or "retrieve"
                || tool == "ManageSinumerikArchive" && action == "retrieve";
            foreach (var pair in arguments)
            {
                if (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var path) || string.IsNullOrEmpty(path)) continue;
                bool input = pair.Key is "inputPath" or "inputFile" or "xmlPath" or "leftFilePath" or "rightFilePath" or "plcXmlPath" or "mappingFilePath" or "templateFile" or "templatePath" or "templateDirectory" or "referenceAmlPath" or "referenceGlobalLibraryPath" or "referenceProjectPath" or "offlineReleaseSuiteJsonPath" or "schemaDirectory" or "apiPath"
                    || import && pair.Key is "importPath" or "filePath" or "allowedFilePath" or "dir" or "directoryPath" or "sourceDir" or "directory"
                    || pair.Key == "filePath" && tool is "AnalyzePlcSclSource" or "DecodePlcSimaticMl" or "ValidatePlcDocumentSchemas" or "InspectSimaticSdCompatibility" or "PatchPlcBlockDocument" or "GetPlcBlockEditCapabilities" or "RenderPlcBlockDocument"
                    || pair.Key == "directory" && tool is "AnalyzePlcReferences" or "GeneratePlcDocumentation" or "ScanPlcSourceAnnotations" or "ManageUnifiedCwcPackage"
                    || pair.Key == "libraryPath" && tool is "AnalyzeGlobalLibraryPackage" or "PlanGlobalLibraryTemplateReuse" or "ProbeGlobalLibrary" or "ImportMasterCopyFromGlobalLibrary"
                    || pair.Key == "path" && tool is "BuildPlcSymbolManifestFromPath" or "ExtractPlcBlockMetrics" or "ValidateClassicHmiMinimalPackageFiles" or "ValidateClassicHmiMinimalPackagePlcSync"
                    || pair.Key == "directoryPath" && tool == "AuditEngineeringExports"
                    || tool == "SeedProjectFromReference" && pair.Key == "referenceDir"
                    || tool is "RetrieveProject" or "RetrieveProjectArchive" && pair.Key == "archivePath";
                if (tool == "OpenProject" && pair.Key == "path" || tool == "ConnectProject" && pair.Key == "projectPath"
                    || tool == "OpenLocalSession" && pair.Key == "localSessionPath")
                {
                    NativeInputPolicy.RequireExists(path, pair.Key);
                    continue;
                }
                if (!input) continue;
                // Unified exchange imports identify a file by directory + fileName.
                if (pair.Key == "directory" && import && arguments["fileName"] is JsonValue fileValue
                    && fileValue.TryGetValue<string>(out var fileName) && !string.IsNullOrEmpty(fileName))
                    NativeInputPolicy.RequireReadable(Path.Combine(path, fileName), "fileName");
                else NativeInputPolicy.RequireReadable(path, pair.Key,
                    pair.Key is "dir" or "directoryPath" or "directory" or "sourceDir" or "referenceDir" or "templateDirectory" or "schemaDirectory"
                    || pair.Key is "inputPath" or "path" or "libraryPath" or "plcXmlPath" or "referenceGlobalLibraryPath" or "referenceProjectPath" or "apiPath" && Directory.Exists(path) || tool.Contains("Documents") && pair.Key == "importPath");
            }
            if ((import || tool == "BuildPlcSymbolManifestFromPath") && arguments["files"] is JsonArray files)
                foreach (var file in files)
                    if (file is JsonValue fileValue && fileValue.TryGetValue<string>(out var path)) NativeInputPolicy.RequireReadable(path, "files");
        }

        private static (string Parameter, string[] Extensions, bool Output)? NativeFile(string tool, JsonObject arguments)
        {
            string action = (string?)arguments["action"] ?? "";
            switch (tool)
            {
                case "ExportAlarmInstanceTexts": case "ExportAlarmTextLists": return ("exportPath", new[] { ".xlsx" }, true);
                case "ImportAlarmTextLists": return ("importPath", new[] { ".xlsx" }, false);
                case "ExportAlarmClasses": return ("exportPath", new[] { ".dat" }, true);
                case "ExportOpcUaInterface": case "ExportHmiConnection": case "ExportHmiScreen": case "ExportHmiTagTable": case "ExportTechnologyObject":
                    return ("exportPath", new[] { ".xml" }, true);
                case "ImportAlarmClasses": return ("importPath", new[] { ".dat" }, false);
                case "ExportDeviceAml": return ("exportPath", new[] { ".aml" }, true);
                case "ImportDeviceAml": return ("filePath", new[] { ".aml" }, false);
                case "ExportProjectTexts": return ("filePath", new[] { ".xlsx" }, true);
                case "ImportProjectTexts": case "ImportPlcAlarmInstanceTexts": return ("filePath", new[] { ".xlsx" }, false);
                case "ExchangePlcAlarmTextLists": case "ExchangePlcSupervisions":
                    return ("filePath", new[] { ".xlsx" }, action == "export");
                case "ManageSivarcScreenLayout": return ("filePath", new[] { ".yml" }, action == "export");
                case "ExchangeCfcCharts": return ("filePath", action == "exportInstructionData" ? Array.Empty<string>() : new[] { ".zip" }, action != "import");
                case "ExchangeMotionCamData": return ("filePath", Array.Empty<string>(), action.StartsWith("export", StringComparison.Ordinal));
                case "GeneratePlcSourceFromBlocks": return ("filePath", Array.Empty<string>(), true);
                case "ManageHardwareUtilities" when action == "exportOpcUa": return ("filePath", new[] { ".xml" }, true);
                case "ManageClassicHmiCycle": case "ManageClassicHmiTextGraphicList":
                    return action is "export" or "import" ? ("filePath", new[] { ".xml" }, action == "export") : null;
                case "ExportSafetyPrintout": return ("filePath", (string?)arguments["printer"] is "MicrosoftXpsDocumentWriter" ? new[] { ".xps", ".oxps" } : new[] { ".pdf" }, true);
                default: return null;
            }
        }

        internal static void ValidateNativeFile(string tool, JsonObject arguments, bool checkOutput = true)
        {
            var contract = NativeFile(tool, arguments);
            if (!contract.HasValue) return;
            var (parameter, extensions, output) = contract.Value;
            if (arguments[parameter] is not JsonValue value || !value.TryGetValue<string>(out var path)) return;
            if (tool == "ExportDeviceAml" && (string.IsNullOrEmpty(Path.GetExtension(path)) || Directory.Exists(path)))
            {
                if (!Absolute(path)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("An absolute exportPath is required.", parameter);
                if (checkOutput)
                {
                    if (!Directory.Exists(path)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output directory must already exist.", parameter);
                    RequireWritable(path, parameter);
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(path) || !Absolute(path) || extensions.Length != 0 && !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("An absolute " + parameter + (extensions.Length == 0 ? " is required." : " ending in " + string.Join(" or ", extensions) + " is required."), parameter);
            if (output && checkOutput)
            {
                if (!ExportObservation.Absent(path)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output already exists or cannot be inspected; overwrite refused.", parameter);
                if (!Directory.Exists(Path.GetDirectoryName(path))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output parent must already exist.", parameter);
                RequireWritable(Path.GetDirectoryName(path)!, parameter);
            }
        }

        // The product runs on Windows; the format check also accepts drive and UNC paths when the offline tests run on Linux CI.
        private static bool Absolute(string path) => Path.IsPathRooted(path) || Path.DirectorySeparatorChar != '\\'
            && (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/') || path.StartsWith(@"\\", StringComparison.Ordinal));

        private static void RequireWritable(string directory, string parameter)
        {
            try { NativeExportPolicy.CheckWritableDirectory(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output directory is unavailable or not writable.", parameter, true, error); }
        }

        private static readonly AsyncLocal<ExportObservation?> CurrentExport = new();
        internal static ExportObservation? ObserveExport(string tool, JsonObject arguments)
        {
            var contract = NativeFile(tool, arguments);
            if (!contract.HasValue || !contract.Value.Output || arguments[contract.Value.Parameter] is not JsonValue value || !value.TryGetValue<string>(out var path)) return null;
            return new ExportObservation(path);
        }
        internal static void RecordExportFailure(Exception error) => CurrentExport.Value?.Record(error);
        internal static void ResolveExportTarget(string path) => CurrentExport.Value?.Resolve(path);
        internal sealed class ExportObservation : IDisposable
        {
            private string path;
            private bool absentBefore;
            private readonly ExportObservation? previous;
            internal string? NativeMessage { get; private set; }
            internal bool NoFileWritten => absentBefore && Absent(path);
            internal ExportObservation(string path)
            {
                this.path = path; absentBefore = Absent(path);
                previous = CurrentExport.Value; CurrentExport.Value = this;
            }
            internal void Resolve(string resolvedPath) { path = resolvedPath; absentBefore = Absent(path); }
            internal void Record(Exception error)
            {
                while (error.InnerException != null) error = error.InnerException;
                NativeMessage ??= HostBehavior.SafeDiagnostic(error.Message);
            }
            internal static bool Absent(string path)
            {
                try { _ = File.GetAttributes(path); return false; }
                catch (FileNotFoundException) /* swallow(env-probe): a missing target is the explicit no-file observation */ { return true; }
                catch (DirectoryNotFoundException) /* swallow(env-probe): a missing parent proves the target is absent */ { return true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
            }
            public void Dispose() => CurrentExport.Value = previous;
        }
    }
}
