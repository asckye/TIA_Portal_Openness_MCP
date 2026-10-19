using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class P707bNativeTests
    {
#if TIA_PLC_EXCHANGE_TESTS
        public P707bNativeTests() => PlcExchangeTarget.ConfigureHost();
#endif
        static P707bNativeTests() => RuntimeHelpers.RunClassConstructor(typeof(PlcExchangeContractsTests).TypeHandle);
        private static Type EngineType(string name) => typeof(McpServer).Assembly.GetType("TiaMcpServer." + name, true)!;
        private static JsonObject Body(object result) => ((CallToolResult)result).StructuredContent!.AsObject();
        private static object Call(object target, string method, params object?[] args)
            => target.GetType().GetMethod(method)!.Invoke(target, args)!;

        private static (SessionFake Fake, object Service) CfcFixture()
        {
            var assembly = typeof(McpServer).Assembly;
#if TIA_PLC_EXCHANGE_TESTS
            var adapter = PlcExchangeTarget.Adapter;
            var adapterName = adapter.GetName();
#else
            var adapterName = assembly.GetReferencedAssemblies().Single(name => name.Name!.StartsWith("TiaMcp.Adapter.", StringComparison.Ordinal));
            var adapter = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location)!, adapterName.Name + ".dll"));
#endif
            var engineType = adapter.GetType("TiaMcp.Adapters.PlcFoundationEngine", true)!;
            var sessionType = adapter.GetType("TiaMcp.Adapters.Native.Plc.IPlcOrganisationSession", true)!;
            var fake = (SessionFake)DispatchProxy.Create(sessionType, typeof(SessionFake));
            fake.ReleaseKey = adapterName.Name!.Split('.').Last();
            var native = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetType("Siemens.Engineering.TiaPortal") != null);
            var engine = Activator.CreateInstance(engineType, adapterName.Name!.Split('.').Last(), Path.GetDirectoryName(native.Location)!)!;
            fake.Engine = engine;
            engineType.GetProperty("EngineeringPlcOrganisationSession")!.SetValue(engine, fake);
            engineType.GetProperty("EngineeringServicesStep")!.SetValue(engine,
                (Func<string, bool, Func<Dictionary<string, object?>, string>, TiaMcp.Adapters.Contracts.HardwareAddressingReply>)((tool, requiresProject, action) =>
                {
                    var meta = new Dictionary<string, object?>();
                    try { action(meta); meta["success"] = !meta.TryGetValue("operationSuccess", out var success) || success is not false; }
                    catch { meta["success"] = false; }
                    return new TiaMcp.Adapters.Contracts.HardwareAddressingReply { Meta = meta };
                }));
            var portType = assembly.GetType("TiaMcp.Shared.CfcPortService", true)!;
            var service = Activator.CreateInstance(portType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] {
                (Func<string, JsonObject, JsonNode?>)((operation, args) =>
                {
                    Assert.Equal("cfc-charts.ExecuteOptionalProduct", operation);
                    var request = System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.OptionalProductRequest>(args["request"]!.ToJsonString())!;
                    var reply = engineType.GetMethod("ExecuteOptionalProduct")!.Invoke(engine, new object[] { request, request.DryRun });
                    return System.Text.Json.JsonSerializer.SerializeToNode(reply);
                }), (Func<string>)(() => "fixture.ap21") }, null)!;
            return (fake, service);
        }

        [Theory]
        [InlineData("Siemens.Engineering.CFC.dll")]
        [InlineData("Siemens.Engineering.TestSuite, Version=21.0.0.0, Culture=neutral")]
        public void MissingOptionalAssemblyIsRefusedInsideTheFakeWorkerStepBeforeNativeCalls(string assemblyName)
        {
            var (fake, _) = CfcFixture();
            var sessionType = fake.Engine.GetType().Assembly.GetType("TiaMcp.Adapters.Native.Options.OptionalProductSession", true)!;
            var request = new TiaMcp.Adapters.Contracts.OptionalProductRequest { Operation = "ExchangeCfcCharts", DryRun = true };
            var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { fake.Engine, request }, null)!;
            var method = sessionType.GetMethod("RunHmiStepTool", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var actionType = method.GetParameters()[1].ParameterType;
            var parameter = Expression.Parameter(actionType.GetGenericArguments()[0]);
            var action = Expression.Lambda(actionType,
                Expression.Throw(Expression.Constant(new FileNotFoundException("fixture optional assembly", assemblyName)), typeof(string)), parameter).Compile();
            var reply = (TiaMcp.Adapters.Contracts.OptionalProductReply)method.Invoke(session, new object[] { request.Operation, action, true })!;
            Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["rejectionCode"]);
            Assert.Equal(true, reply.Meta["operationNotStarted"]);
            Assert.Equal(false, reply.Meta["operationSuccess"]);
            Assert.Equal(false, reply.Meta["apiCallSuccess"]);
            Assert.False(reply.RequiresSessionReset);
            Assert.Equal(0, fake.NativeCalls);
            var map = typeof(McpServer).Assembly.GetType("TiaMcpServer.ModelContextProtocol.OptionalPackageContract", true)!
                .GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;
            var body = Body(map.Invoke(null, new object?[] { request.Operation,
                new ResponseMessage { Message = reply.Message, Meta = System.Text.Json.JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() }, false, null, null })!);
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("none", (string?)body["meta"]?["completeness"]);
            Assert.Equal(false, (bool?)body["meta"]?["requiresSessionReset"]);
        }

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
                    var (fake, service) = CfcFixture();
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
                    var (fake, service) = CfcFixture();
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
                var (fake, service) = CfcFixture();
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
        {
            var service = Activator.CreateInstance(EngineType("Siemens.Services.ProjectLifecyclePortService"),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] {
                    // A null release keeps the engine's own release, as the production constructor does.
                    (Func<string, JsonObject, JsonNode?>)fake.ProjectCall, (Func<bool>)(() => true), (Func<string>)(() => fake.Previous), null! }, null)!;
            return Activator.CreateInstance(EngineType("ModelContextProtocol.ProjectLifecycleProjectTools"), service)!;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProductionSaveAsReturnsBothObservedProjectFiles(bool missingAfter)
        {
#if TIA_PLC_EXCHANGE_TESTS
            var fake = new SaveAsFake();
#else
            var fake = (SaveAsFake)DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SaveAsFake));
#endif
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
#if TIA_PLC_EXCHANGE_TESTS
            var fake = new SaveAsFake();
#else
            var fake = (SaveAsFake)DispatchProxy.Create(EngineType("Siemens.IEngineeringSession"), typeof(SaveAsFake));
#endif
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
            internal JsonNode? ProjectCall(string operation, JsonObject arguments)
            {
                switch (operation)
                {
                    case "ReadState": return new JsonObject { ["IsLocalSession"] = LocalSession };
                    case "project-lifecycle.ReadProjectLifecycleBinding":
                        BindingReads++;
                        return new JsonObject { ["identity"] = SaveCalls == 0 && MissingBefore || SaveCalls != 0 && MissingAfter ? null
                            : new JsonObject { ["projectPath"] = SaveCalls == 0 ? Previous : New } };
                    case "project-lifecycle.ArchiveSaveProjectCopy":
                        Assert.False((bool)arguments["dryRun"]!);
                        Assert.True((bool)arguments["confirm"]!);
                        Assert.Equal(Previous, (string?)arguments["expectedProjectFile"]);
                        EngineType("ModelContextProtocol.InvocationJournal").GetMethod("NativeCallStarted", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                        SaveCalls++;
                        return JsonValue.Create(true);
                    default: throw new Exception("Unexpected project lifecycle operation: " + operation);
                }
            }
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
            public object Engine = null!;
            public string ReleaseKey = "20";
            public int NativeCalls;
            public string? LastNativeCall;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method!.Name == "get_ReleaseKey") return ReleaseKey;
                if (method.Name != "RunHmiStepTool") { NativeCalls++; LastNativeCall = method.Name; throw new Exception("Native access must not run"); }
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
