using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.V4
{
    internal static class CallerInputFiles
    {
        // These argument names denote caller files, never engineering object paths.
        // An export's filePath/directoryPath is an output and must not be probed here.
        internal static void Validate(string tool, JsonObject arguments)
        {
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
    }
}
