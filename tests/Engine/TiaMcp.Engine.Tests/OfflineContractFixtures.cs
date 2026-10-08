using System;
using System.Collections.Generic;
using TiaMcpServer.ModelContextProtocol;

// Empty namespaces satisfy unused imports in the linked offline builders.
namespace Siemens.Engineering.SW { }
namespace Siemens.Engineering.SW.Blocks { }

namespace TiaMcpServer.ModelContextProtocol
{
    // Response boundary stand-ins, matching the production DTO properties.
    public class ImportFailure
    {
        public string? Path { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseCompile : ResponseMessage
    {
        public string? State { get; set; }
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public IEnumerable<string>? Messages { get; set; }
    }

    public class ResponsePlcProgramImport : ResponseMessage
    {
        public bool? DryRun { get; set; }
        public string? BuildKind { get; set; }
        public string? GeneratedDirectory { get; set; }
        public IEnumerable<string>? WrittenFiles { get; set; }
        public IEnumerable<string>? DiscoveredTypes { get; set; }
        public IEnumerable<string>? DiscoveredTagTables { get; set; }
        public IEnumerable<string>? DiscoveredTechnologyObjects { get; set; }
        public IEnumerable<string>? DiscoveredBlocks { get; set; }
        public IEnumerable<string>? ImportedTypes { get; set; }
        public IEnumerable<string>? ImportedTagTables { get; set; }
        public IEnumerable<string>? ImportedTechnologyObjects { get; set; }
        public IEnumerable<string>? ImportedBlocks { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
        public ResponseCompile? Compile { get; set; }
        public string? CapabilityDecision { get; set; }
        public IEnumerable<string>? CapabilityWarnings { get; set; }
        public IEnumerable<string>? RecommendedNextActions { get; set; }
    }

    public class ResponseXmlBuild : ResponseJsonReport
    {
        public string? Xml { get; set; }
    }

    // The contract checks inspect this tool's signature but never run release probes.
    internal static class OfflineReleaseValidationSuite
    {
        internal static System.Text.Json.Nodes.JsonObject Run(string workspaceRoot, string reportDirectory)
            => throw new InvalidOperationException("Offline contracts must not run engine capability probes.");
    }

    internal static class PlcCompilation
    {
        internal static ResponseCompile BuildCompileResponse(string softwarePath, object result)
            => throw new InvalidOperationException("Offline contracts must not compile PLC software.");
    }
}

namespace TiaMcpServer.Siemens
{
    internal partial interface IEngineeringSession
    {
        void ImportType(string softwarePath, string group, string path);
        void ImportPlcTagTable(string softwarePath, string group, string path);
        void ImportBlock(string softwarePath, string group, string path);
        object CompileSoftware(string softwarePath);
        (string TempDir, string XmlPath) ExportBlockDocumentForAnalysis(string softwarePath, string blockPath);
    }

    internal sealed partial class Portal
    {
        public void ImportType(string softwarePath, string group, string path) => throw Unexpected();
        public void ImportPlcTagTable(string softwarePath, string group, string path) => throw Unexpected();
        public void ImportBlock(string softwarePath, string group, string path) => throw Unexpected();
        public object CompileSoftware(string softwarePath) => throw Unexpected();
        public (string TempDir, string XmlPath) ExportBlockDocumentForAnalysis(string softwarePath, string blockPath) => throw Unexpected();
        private static Exception Unexpected() => new InvalidOperationException("Offline contracts must not enter a native session.");
    }
}

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class EngineeringAuditService
    {
        internal ResponseMessage ReadPlcBlockScopes(string softwarePath, int offset, int limit)
            => throw new InvalidOperationException("Offline contracts must not enumerate native blocks.");
        internal ResponseMessage ManagePlcBlockDocuments(string softwarePath, string action, string name, string groupPath,
            string unitName, string unitKind, string directoryPath, string importOption, bool dryRun, bool verifyDocumentReadback)
            => throw new InvalidOperationException("Offline contracts must not access native documents.");
    }
}
