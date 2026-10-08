using TiaMcpServer;
using static TiaMcpServer.ModelContextProtocol.ScaffoldOperations;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Exercise the actual shared orchestration with an in-memory native boundary.
    public sealed class ScaffoldStep { public string? Step, Status, Detail; }
    public sealed class ResponseScaffold
    {
        public bool Ok = true;
        public string? CompileState;
        public int? CompileErrorCount, CompileWarningCount;
        public List<ScaffoldStep> Steps = new();
    }
    public static partial class McpServer
    {
        internal static readonly List<string> ScaffoldCalls = new();
        internal static bool rejectUdt, missingHmi;
        internal static int compileErrors;

        internal static void CheckScaffoldOperations(Action<bool, string> check)
        {
            var previous = EngineServices.Provider;
            using var provider = new ServiceCollection().AddSingleton<TiaMcpServer.Siemens.IEngineeringSession, ScaffoldSession>().AddSingleton<PlcBuildTools>()
                .AddSingleton<PlcExternalSourcesTools>().AddSingleton<PlcBlocksTools>()
                .AddSingleton<HmiDescribeTools>().AddSingleton<UnifiedHmiTools>().BuildServiceProvider();
            EngineServices.SetServiceProvider(provider);
            try
            {
                var root = JsonNode.Parse("{\"udt\":[{\"name\":\"UDT_A\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]}],\"globalDb\":[{\"dbName\":\"DB_A\",\"dbNumber\":1,\"staticMembers\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]}],\"tagTable\":[{\"tableName\":\"Tags\",\"tags\":[{\"name\":\"Ready\",\"dataTypeName\":\"Bool\",\"logicalAddress\":\"%M0.0\"}]}],\"sclSourceFiles\":[\"Axis.scl\"]}")!;
                ScaffoldCalls.Clear();
                var result = new ResponseScaffold();
                ApplyScaffoldPlcElements(root, "PLC_1", result);
                check(result.Ok && result.Steps.All(s => s.Status == "ok") && ScaffoldCalls.SequenceEqual(new[] { "udt", "globaldb", "tagtable", "import:Axis.scl", "generate:Axis.scl" }), "shared scaffold imports PLC definitions and generates the imported SCL source in order: " + string.Join("; ", result.Steps.Select(s => s.Step + "=" + s.Status + ":" + s.Detail)));
                rejectUdt = true; ScaffoldCalls.Clear(); result = new ResponseScaffold();
                ApplyScaffoldPlcElements(root, "PLC_1", result); rejectUdt = false;
                check(!result.Ok && result.Steps[0].Status == "failed" && ScaffoldCalls.Contains("generate:Axis.scl"), "shared scaffold records one failed element and processes the remaining elements");
                result = new ResponseScaffold(); CompileScaffoldPlc("PLC_1", result);
                check(result.Ok && result.CompileErrorCount == 0 && result.CompileWarningCount == 1, "shared scaffold reports successful compile with warnings");
                compileErrors = 2; result = new ResponseScaffold(); CompileScaffoldPlc("PLC_1", result); compileErrors = 0;
                check(!result.Ok && result.CompileErrorCount == 2 && result.Steps.Single().Status == "failed", "shared scaffold reports compile errors as failure");
                root = JsonNode.Parse("{\"hmiScreens\":[{\"screenName\":\"Main\",\"width\":640,\"height\":480,\"designJson\":{}}],\"hmiTags\":[{\"tagName\":\"Run\"}]}")!;
                result = new ResponseScaffold(); ScaffoldCalls.Clear();
                ApplyScaffoldHmi(root, "PLC_1", "HMI_1", "missing", "Connection", result);
                check(result.Ok && ScaffoldCalls.SequenceEqual(new[] { "connection:HMI_RT_1:Connection:PLC_1", "screen:Main:640:480", "design:Main:True", "tag:Default tag table:Run:Bool:Connection:True" }), "shared scaffold resolves HMI and applies screen geometry, design, and tag defaults");
                missingHmi = true; result = new ResponseScaffold(); ScaffoldCalls.Clear();
                ApplyScaffoldHmi(root, "PLC_1", "HMI_1", "missing", "Connection", result); missingHmi = false;
                check(!result.Ok && result.Steps.Single().Step == "hmiResolve" && ScaffoldCalls.Count == 0, "shared scaffold leaves HMI untouched when software cannot be resolved");
            }
            finally { EngineServices.SetServiceProvider(previous); }
        }
    }
    internal sealed class ScaffoldSession : TiaMcpServer.Siemens.IEngineeringSession
    {
        public void ImportType(string plc, string group, string path)
        { if (McpServer.rejectUdt) throw new InvalidOperationException("Fixture UDT rejection"); McpServer.ScaffoldCalls.Add("udt"); }
        public void ImportPlcTagTable(string plc, string group, string path) => McpServer.ScaffoldCalls.Add("tagtable");
        public void ImportBlock(string plc, string group, string path) => McpServer.ScaffoldCalls.Add("globaldb");
        public object CompileSoftware(string plc) => throw new InvalidOperationException("Unexpected build/import compile.");
        public (string TempDir, string XmlPath) ExportBlockDocumentForAnalysis(string plc, string block)
            => throw new InvalidOperationException("Unexpected native export.");
    }
    internal sealed class PlcExternalSourcesTools
    {
        public void ImportPlcExternalSource(string plc, string group, string path) => McpServer.ScaffoldCalls.Add("import:" + path);
        public void GenerateBlocksFromExternalSource(string plc, string name) => McpServer.ScaffoldCalls.Add("generate:" + name);
    }
    internal sealed class PlcBlocksTools
    {
        public (string State, int? ErrorCount, int? WarningCount) CompileAndDiagnosePlc(string plc) => ("Finished", McpServer.compileErrors, 1);
    }
    internal sealed class HmiDescribeTools
    {
        public void GetHmiProgramInfo(string path) { if (McpServer.missingHmi || path != "HMI_RT_1") throw new InvalidOperationException("Fixture path missing"); }
    }
    internal sealed class UnifiedHmiTools
    {
        public void EnsureUnifiedHmiConnection(string path, string connection, string plc) => McpServer.ScaffoldCalls.Add("connection:" + path + ":" + connection + ":" + plc);
        public void EnsureUnifiedHmiScreen(string path, string name, uint width, uint height) => McpServer.ScaffoldCalls.Add("screen:" + name + ":" + width + ":" + height);
        public void ApplyUnifiedHmiScreenDesignJson(string path, string name, string json, bool overwrite) => McpServer.ScaffoldCalls.Add("design:" + name + ":" + overwrite);
        public void EnsureUnifiedHmiTag(string path, string table, string name, string type, string plc, string tag, string connection, string address, bool overwrite) => McpServer.ScaffoldCalls.Add("tag:" + table + ":" + name + ":" + type + ":" + connection + ":" + overwrite);
    }
}
