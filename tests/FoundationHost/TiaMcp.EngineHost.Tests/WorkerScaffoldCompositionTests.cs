using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Tests;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class WorkerScaffoldCompositionTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public void Full_scaffold_runs_all_primitives_in_order_with_unchanged_step_records(string release)
    {
        Run(release, (tools, fake, spec) => {
            var result = tools.ScaffoldProject(spec.ToJsonString(), false);
            Assert.True(result.Ok);
            Assert.Equal(new[] { "connect", "createProject", "addDevice", "addDevice", "sclImport", "sclGenerate", "lad", "compile", "hmiInfo", "hmiConnect", "hmiScreen", "hmiDesign", "hmiTag", "save" }, fake.Primitives);
            Assert.Equal(new[] { "connect", "createProject", "addDevicePlc", "addDeviceHmi", "scl", "lad", "compile", "hmiResolve", "hmiConnection", "hmiScreen", "hmiTag", "save" }, result.Steps.Select(x => x.Step));
            Assert.All(result.Steps, step => Assert.Equal("ok", step.Status));
            Assert.Equal("Success", result.CompileState);
            Assert.Equal(0, result.CompileErrorCount);
            Assert.Equal(2, result.CompileWarningCount);
            Assert.True((bool?)result.Meta?["success"]);
        });
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Dry_run_never_dispatches_and_retains_existing_offline_results(string release)
    {
        Run(release, (tools, fake, spec) => {
            var result = tools.ScaffoldProject(spec.ToJsonString());
            Assert.True(result.Ok);
            Assert.Equal(new[] { "scl", "lad", "hmiScreen" }, result.Steps.Select(x => x.Step));
            Assert.True((bool?)result.Meta?["dryRun"]);
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.Primitives);
        });
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Element_failure_is_reported_and_later_steps_keep_their_order(string release)
    {
        Run(release, (tools, fake, spec) => {
            fake.FailOperation = "plc-documents.ExecutePlcB5";
            fake.CompilerErrors = 3;
            var result = tools.ScaffoldProject(spec.ToJsonString(), false);
            Assert.False(result.Ok);
            Assert.Equal(new[] { "lad", "compile" }, result.Steps.Where(x => x.Status == "failed").Select(x => x.Step));
            Assert.Contains("fixture failure", result.Steps.Single(x => x.Step == "lad").Detail);
            Assert.Equal(3, result.CompileErrorCount);
            Assert.Equal("save", fake.Primitives.Last());
        });
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Critical_device_failure_aborts_before_import_compile_hmi_or_save(string release)
    {
        Run(release, (tools, fake, spec) => {
            fake.FailedProbe = true;
            var error = Assert.Throws<McpException>(() => tools.ScaffoldProject(spec.ToJsonString(), false));
            Assert.Equal("PLC device add failed: fixture probe failure", error.Message);
            Assert.Equal(new[] { "connect", "createProject", "addDevice" }, fake.Primitives);
        });
    }

    private static void Run(string release, Action<ProjectLifecycleProjectTools, ScaffoldWorkerFake, JsonObject> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "scaffold-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "Main.scl"); File.WriteAllText(source, "FUNCTION Main : Void\nEND_FUNCTION");
            File.WriteAllText(Path.Combine(directory, "Main.s7dcl"), "fixture");
            var fake = new ScaffoldWorkerFake(release);
            JsonNode? Call(string operation, JsonObject arguments)
                => operation == "project-lifecycle.ExecuteProjectScaffoldPrimitive"
                    ? JsonSerializer.SerializeToNode(fake.Execute(JsonSerializer.Deserialize<ProjectScaffoldPrimitive>(arguments["request"]!.ToJsonString())!))
                    : fake.Call(operation, arguments);
            var service = new ProjectLifecyclePortService(Call, () => fake.ProjectFile.Length > 0, () => fake.ProjectFile, fake.Release);
            var spec = new JsonObject { ["projectName"] = "P", ["directoryPath"] = directory, ["hmiName"] = "HMI_1", ["hmiSoftwarePath"] = "HMI_RT_1",
                ["sclSourceFiles"] = new JsonArray(source), ["ladDocs"] = new JsonArray(new JsonObject { ["importPath"] = directory, ["name"] = "Main" }),
                ["hmiScreens"] = new JsonArray(new JsonObject { ["screenName"] = "Main", ["width"] = 800, ["height"] = 480, ["designJson"] = new JsonObject { ["items"] = new JsonArray() } }),
                ["hmiTags"] = new JsonArray(new JsonObject { ["tagName"] = "Start", ["plcTag"] = "Start" }) };
            test(new ProjectLifecycleProjectTools(service), fake, spec);
        }
        finally { Directory.Delete(directory, true); }
    }
}
