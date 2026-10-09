using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class P707bNativeTests
    {
        static P707bNativeTests() => RuntimeHelpers.RunClassConstructor(typeof(PlcExchangeContractsTests).TypeHandle);
        private static Type EngineType(string name) => typeof(McpServer).Assembly.GetType("TiaMcpServer." + name, true)!;
        private static JsonObject Body(object result) => ((CallToolResult)result).StructuredContent!.AsObject();
        private static object Call(object target, string method, params object?[] args)
            => target.GetType().GetMethod(method)!.Invoke(target, args)!;

        [Theory]
        [InlineData("read")]
        [InlineData("add")]
        [InlineData("change")]
        [InlineData("remove")]
        public void ProductionProtectionGuardRefusesBeforeEveryNativeCall(string action)
        {
            foreach (bool preview in new[] { true, false })
                foreach (bool skip in new[] { true, false })
                {
                    var fake = DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SessionFake));
                    var service = Activator.CreateInstance(EngineType("Siemens.Services.CfcService"), fake)!;
                    var tool = Activator.CreateInstance(EngineType("ModelContextProtocol.CfcTools"), service)!;
                    var body = Body(Call(tool, "ManageCfcChartProtection", "PLC_1", "Chart_1", action,
                        action == "change" || action == "remove" ? "private" : "", action == "add" || action == "change" ? "hash" : "", preview, "V2.0", skip));
                    Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
                    Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                    Assert.Equal(0, ((SessionFake)fake).NativeCalls);
                }
        }

        [Theory]
        [InlineData("export")]
        [InlineData("selectiveExport")]
        [InlineData("exportInstructionData")]
        public void ProductionExchangeGuardRefusesUnsafeExportAndPreflight(string action)
        {
            foreach (bool preview in new[] { true, false })
                foreach (bool skip in new[] { true, false })
                {
                    var fake = DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SessionFake));
                    var service = Activator.CreateInstance(EngineType("Siemens.Services.CfcService"), fake)!;
                    var tool = Activator.CreateInstance(EngineType("ModelContextProtocol.CfcTools"), service)!;
                    var body = Body(Call(tool, "ExchangeCfcCharts", "PLC_1", action, Path.Combine(Path.GetTempPath(), "new.xml.zip"),
                        action == "exportInstructionData" ? "" : "V2.0", 0L, true, false, preview,
                        action == "selectiveExport" ? new[] { "Chart_1" } : Array.Empty<string>(), skip));
                    Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
                    Assert.Equal(0, ((SessionFake)fake).NativeCalls);
                }
        }

        [Theory]
        [InlineData("Error", false)]
        [InlineData("Failure", false)]
        [InlineData("Error", true)]
        public void HardwareAndScopedDocumentContractsUseReturnedNativeEvidence(string state, bool modifiesProject)
        {
            var evidence = new JsonObject { ["success"] = false };
            NativeResultState.Record(evidence, state, modifiesProject, "native.log", Path.Combine(Path.GetTempPath(), "missing.aml"), new JsonArray("native message"));
            foreach (string contract in new[] { "HardwareContract", "PlcExchangeContract" })
            {
                var map = EngineType("ModelContextProtocol." + contract).GetMethod("Map", BindingFlags.Static | BindingFlags.NonPublic)!;
                var args = contract == "HardwareContract" ? new object?[] { "ExportDeviceAml", new ResponseMessage { Meta = evidence }, true, true }
                    : new object?[] { "ExportPlcBlockDocuments", new ResponseMessage { Meta = evidence }, true, true, null };
                var body = Body(map.Invoke(null, args)!);
                Assert.Equal(modifiesProject ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
                Assert.Equal(state, (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
                Assert.Equal(modifiesProject, (bool?)body["meta"]?["requiresSessionReset"]);
            }
        }

        [Theory]
        [InlineData("native:read", false)]
        [InlineData("native:write", false)]
        [InlineData("GetSessionState", true)]
        public void EngineFlushPolicyNeverDowngradesNativeBefore(string tool, bool nativeCallId)
        {
            var method = EngineType("ModelContextProtocol.InvocationJournal").GetMethod("RequireDiskFlush", BindingFlags.Static | BindingFlags.NonPublic)!;
            var row = new JsonObject { ["tool"] = tool, ["phase"] = "BEFORE" };
            if (nativeCallId) row["nativeCallId"] = "pending";
            object[] args = { row.ToJsonString(), false };
            method.Invoke(null, args);
            Assert.True((bool)args[1]);
        }

        [Fact]
        public void NestedLibraryExportRetainsExplicitFailureEvidence()
        {
            var state = new JsonObject();
            NativeResultState.Record(state, "Error", false, "native.log");
            state["targetFiles"] = new JsonArray(NativeResultState.FileRow(Path.Combine(Path.GetTempPath(), "missing.yaml")));
            var response = new ResponseMessage { Meta = new JsonObject { ["success"] = false, ["records"] = new JsonArray(state) } };
            var map = EngineType("ModelContextProtocol.HmiInspectionContract").GetMethod("Map", BindingFlags.Static | BindingFlags.NonPublic)!;
            var body = Body(map.Invoke(null, new object?[] { "GetUnifiedLibraryType", response, false, false, null, 0, null })!);
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("Error", (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
            Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
        }

        public class SessionFake : DispatchProxy
        {
            public int NativeCalls;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method!.Name != "RunHmiStepTool") { NativeCalls++; throw new Exception("Native access must not run"); }
                var meta = new JsonObject();
                try { ((Func<JsonObject, string>)args![1]!)(meta); meta["success"] = true; }
                catch { meta["success"] = false; }
                var response = Activator.CreateInstance(method.ReturnType)!;
                response.GetType().GetProperty("Meta")!.SetValue(response, meta);
                return response;
            }
        }
    }
}
