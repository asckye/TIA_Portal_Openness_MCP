using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace PlcExchangeTests
{
    public sealed class P707cNativeTests
    {
        static P707cNativeTests() => RuntimeHelpers.RunClassConstructor(typeof(PlcExchangeContractsTests).TypeHandle);
        private static Type EngineType(string name) => typeof(McpServer).Assembly.GetType("TiaMcpServer.ModelContextProtocol." + name, true)!;

        [Theory]
        [InlineData("EngineeringToolContract")]
        [InlineData("LibraryToolContract")]
        [InlineData("OptionalPackageContract")]
        [InlineData("PlcToolContract")]
        [InlineData("RuntimeToolContract")]
        [InlineData("HardwareContract")]
        [InlineData("HardwareToolContract")]
        [InlineData("HmiInspectionContract")]
        [InlineData("PlcExchangeContract")]
        public void ProductionMappingsCoverAllNativeStates(string site)
        {
            foreach (string type in NativeResultStates.EnumTypes)
                foreach (string state in NativeResultStates.States(type).Concat(new[] { "987654" }))
                    foreach (bool changesProject in new[] { false, true })
                    {
                        var kind = NativeResultStates.Classify(type, state);
                        bool success = kind is NativeStateKind.Success or NativeStateKind.Warning;
                        var evidence = new JsonObject { ["nativeStateType"] = type, ["success"] = success, ["operationSuccess"] = success,
                            ["mayHaveWrittenFiles"] = !changesProject };
                        NativeResultState.Record(evidence, state, changesProject, "native.log");
                        var response = new ResponseMessage { Meta = evidence };
                        var method = EngineType(site).GetMethod(site == "HardwareToolContract" ? "MapResult" : "Map", BindingFlags.Static | BindingFlags.NonPublic)!;
                        object?[] args = site switch {
                            "EngineeringToolContract" => new object?[] { "ExportAlarmInstanceTexts", response, true },
                            "LibraryToolContract" => new object?[] { "ExchangeSivarcScreenLayouts", response, true, true },
                            "OptionalPackageContract" => new object?[] { "ExchangePlcSupervisions", response, true, null, null },
                            "RuntimeToolContract" or "HardwareToolContract" => new object?[] { "native-fixture", response, false, true },
                            "HmiInspectionContract" => new object?[] { "native-fixture", response, true, true, null, 0, null },
                            "PlcExchangeContract" => new object?[] { "native-fixture", response, true, true, null },
                            _ => new object?[] { "native-fixture", response, true, true }
                        };
                        var body = ((CallToolResult)method.Invoke(null, args)!).StructuredContent!;
                        bool unknown = kind is NativeStateKind.Unexpected or NativeStateKind.Partial || kind == NativeStateKind.Failure && changesProject;
                        Assert.Equal(success ? "succeeded" : unknown ? "unknown" : "failed", (string?)body["meta"]?["outcome"]);
                        Assert.Equal(unknown, (bool?)body["meta"]?["requiresSessionReset"]);
                        Assert.Equal(success ? null : unknown ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
                        if (!success) Assert.Equal(state, (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
                        Assert.Equal(kind == NativeStateKind.Warning, body["meta"]!["warnings"]!.AsArray().Any(w => (string?)w?["code"] == "NATIVE_WARNING"));
                    }
        }

        [Theory]
        [InlineData("HardwareToolContract", "CompileDevice")]
        [InlineData("PlcToolContract", "CompilePlcSoftware")]
        [InlineData("HmiInspectionContract", "CompileHmiDiagnostics")]
        public void UnknownCompilerRootOverridesDiagnosticCounts(string site, string tool)
        {
            var evidence = new JsonObject { ["nativeStateType"] = NativeResultStates.Compiler, ["rootState"] = "987654",
                ["effectiveState"] = "987654", ["rootCounts"] = new JsonObject { ["errors"] = 1, ["warnings"] = 1 } };
            NativeResultState.Record(evidence, "987654", true);
            var method = EngineType(site).GetMethod(site == "HardwareToolContract" ? "MapResult" : "Map", BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] args = site switch {
                "HardwareToolContract" => new object?[] { tool, new ResponseMessage { Meta = evidence }, false, true },
                "HmiInspectionContract" => new object?[] { tool, new ResponseMessage { Meta = evidence }, true, true, null, 0, null },
                _ => new object?[] { tool, new ResponseMessage { Meta = evidence }, true, true }
            };
            var body = ((CallToolResult)method.Invoke(null, args)!).StructuredContent!;
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("987654", (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
        }

        [Theory]
        [InlineData("14sp1", "TIA_V14SP1_PublicAPI")]
        [InlineData("15.1", "TIA_V15.1_PublicAPI")]
        [InlineData("16", "TIA_V16_PublicAPI")]
        [InlineData("17", "TIA_V17_PublicAPI")]
        [InlineData("18", "TIA_V18_PublicAPI")]
        [InlineData("19", "TIA_V19_PublicAPI")]
        [InlineData("20", "TIA_V20_PublicAPI")]
        [InlineData("21", "TIA_V21_PublicAPI")]
        public void TableMatchesEveryAvailableSdkEnum(string release, string folder)
        {
            string root = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT") ?? throw new InvalidOperationException("SDK root is required.");
            int checkedEnums = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(root, folder), "*.dll", SearchOption.AllDirectories).Where(p => !p.Contains("net8.0")))
            {
                using var stream = File.OpenRead(file); using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var definition = metadata.GetTypeDefinition(handle);
                    string name = metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
                    if (!NativeResultStates.EnumTypes.Contains(name)) continue;
                    var values = definition.GetFields().Select(h => metadata.GetFieldDefinition(h)).Where(f => !f.GetDefaultValue().IsNil)
                        .Select(f => metadata.GetString(f.Name)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    Assert.Equal(values, NativeResultStates.States(name).OrderBy(s => s, StringComparer.Ordinal).ToArray());
                    checkedEnums++;
                }
            }
            Assert.True(checkedEnums >= (release == "14sp1" ? 2 : 4));
        }
    }
}
