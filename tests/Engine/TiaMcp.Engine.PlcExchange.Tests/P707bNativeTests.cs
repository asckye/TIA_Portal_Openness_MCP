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
                    Assert.Contains("independent CFC chart inventory", (string?)body["error"]?["message"]);
                    Assert.Contains("CompleteExport", (string?)body["error"]?["message"]);
                    Assert.Contains("skipChartPreflight", (string?)body["error"]?["message"]);
                    Assert.Contains("CFC imports remain available", (string?)body["error"]?["message"]);
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
                    Assert.Contains("independent CFC chart inventory", (string?)body["error"]?["message"]);
                    Assert.Contains("CompleteExport", (string?)body["error"]?["message"]);
                    Assert.Contains("skipChartPreflight", (string?)body["error"]?["message"]);
                    Assert.Contains("CFC imports remain available", (string?)body["error"]?["message"]);
                    Assert.Equal(0, ((SessionFake)fake).NativeCalls);
                }
        }

        [Theory]
        [InlineData("Error", false)]
        [InlineData("Failure", false)]
        [InlineData("Error", true)]
        public void HardwareAndScopedDocumentContractsUseReturnedNativeEvidence(string state, bool modifiesProject)
        {
            var evidence = new JsonObject { ["success"] = false,
                ["nativeStateType"] = state == "Failure" ? TiaMcp.Adapters.Contracts.NativeResultStates.Documents : TiaMcp.Adapters.Contracts.NativeResultStates.Cax };
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ImportStillReachesTheOriginalSessionPath(bool preview)
        {
            foreach (bool skip in new[] { true, false })
            {
                var fake = DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SessionFake));
                var service = Activator.CreateInstance(EngineType("Siemens.Services.CfcService"), fake)!;
                var tool = Activator.CreateInstance(EngineType("ModelContextProtocol.CfcTools"), service)!;
                var body = Body(Call(tool, "ExchangeCfcCharts", "PLC_1", "import", Path.Combine(Path.GetTempPath(), "charts.xml.zip"),
                    "V2.0", 0L, true, false, preview, Array.Empty<string>(), skip));
                Assert.NotEqual("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
                Assert.Equal(1, ((SessionFake)fake).NativeCalls);
                Assert.Equal(preview ? "ExactPlcForEngineering" : "AcquireHmiEditAccess", ((SessionFake)fake).LastNativeCall);
            }
        }

        [Fact]
        public void ProductionSaveAsRemainsApprovalGated()
        {
            var method = EngineType("ModelContextProtocol.McpServer").GetMethod("ApprovalWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.True((bool)method.Invoke(null, new object[] { "SaveProjectCopy", "{\"newProjectPath\":\"C:/fixture/Copy\"}" })!);
        }

        private static object SaveAsTool(SaveAsFake fake)
            => Activator.CreateInstance(EngineType("ModelContextProtocol.ProjectSessionTools"), fake, null, null, null, null)!;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProductionSaveAsReturnsBothObservedProjectFiles(bool missingAfter)
        {
            var fake = (SaveAsFake)DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SaveAsFake));
            fake.MissingAfter = missingAfter;
            var body = Body(Call(SaveAsTool(fake), "SaveProjectCopyV4", Path.Combine(Path.GetTempPath(), "Copy")));
            Assert.Equal(1, fake.SaveCalls);
            Assert.Equal(2, fake.BindingReads);
            if (missingAfter)
            {
                Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
                Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
            }
            else
            {
                Assert.True((bool)body["ok"]!);
                Assert.Equal(fake.Previous, (string?)body["data"]?["previousProjectFile"]);
                Assert.Equal(fake.New, (string?)body["data"]?["newProjectFile"]);
                Assert.Equal(fake.New, (string?)body["data"]?["evidence"]?["binding"]?["identity"]?["projectPath"]);
                Assert.Contains("original file keeps its last saved state", (string?)body["data"]?["summary"]);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SaveAsRefusesLocalSessionsOrAnAbsentCachedBinding(bool local)
        {
            var fake = (SaveAsFake)DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SaveAsFake));
            fake.LocalSession = local;
            fake.MissingBefore = !local;
            var body = Body(Call(SaveAsTool(fake), "SaveProjectCopyV4", Path.Combine(Path.GetTempPath(), "Copy")));
            Assert.False((bool)body["ok"]!);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal(0, fake.SaveCalls);
        }

        public class SaveAsFake : DispatchProxy
        {
            public int SaveCalls, BindingReads;
            public bool LocalSession, MissingBefore, MissingAfter;
            public string Previous = Path.Combine(Path.GetTempPath(), "Original.ap21");
            public string New = Path.Combine(Path.GetTempPath(), "Copy", "NativeObserved.ap21");
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                switch (method!.Name)
                {
                    case "IsProjectNull": return false;
                    case "get_IsLocalSession": return LocalSession;
                    case "GetBindingIdentity":
                        BindingReads++;
                        return new JsonObject { ["identity"] = SaveCalls == 0 && MissingBefore || SaveCalls != 0 && MissingAfter ? null
                            : new JsonObject { ["projectPath"] = SaveCalls == 0 ? Previous : New } };
                    case "SaveAsProject":
                        EngineType("ModelContextProtocol.InvocationJournal").GetMethod("NativeCallStarted", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                        SaveCalls++;
                        return true;
                    default: throw new Exception("Unexpected session call: " + method.Name);
                }
            }
        }

        public class SessionFake : DispatchProxy
        {
            public int NativeCalls;
            public string? LastNativeCall;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method!.Name != "RunHmiStepTool") { NativeCalls++; LastNativeCall = method.Name; throw new Exception("Native access must not run"); }
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
