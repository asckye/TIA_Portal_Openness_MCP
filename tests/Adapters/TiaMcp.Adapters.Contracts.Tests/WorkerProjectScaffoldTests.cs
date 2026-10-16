using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerProjectScaffoldTests
{
    public static IEnumerable<object[]> Primitives()
    {
        foreach (string release in new[] { "20", "21" })
            foreach (string kind in new[] { "connect", "createProject", "save", "addDevice", "lad", "sclImport", "sclGenerate", "compile", "hmiInfo", "hmiConnect", "hmiScreen", "hmiDesign", "hmiTag" })
                yield return new object[] { release, kind };
    }

    [Theory, MemberData(nameof(Primitives))]
    public void Every_primitive_uses_existing_operations_and_preserves_reply_fields(string release, string kind)
    {
        var fake = new ScaffoldWorkerFake(release) { Attached = kind != "connect", ProjectFile = kind is "connect" or "createProject" ? "" : "C:/fixture/P.ap" + release };
        string directory = Path.Combine(Path.GetTempPath(), "scaffold-primitive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Main.s7dcl"), "fixture");
            File.WriteAllText(Path.Combine(directory, "Main.s7res"), "MultiLingualTexts:\n  - id: missing\n    zh-CN: text\n");
            var primitive = Request(kind, directory);
            var result = Assert.IsType<JsonObject>(fake.Execute(primitive));
            Assert.NotNull(result["Message"]);
            Assert.True((bool?)result["Meta"]?["success"]);
            if (kind == "addDevice")
            {
                Assert.True((bool?)result["Ok"]);
                Assert.Equal("PLC_1", (string?)result["DeviceName"]);
                Assert.Equal("fixture-mlfb", (string?)result["MlfbUsed"]);
                Assert.Equal("V1", (string?)result["VersionUsed"]);
                Assert.Equal(new[] { "first", "second" }, result["Attempts"]!.AsArray().Select(x => (string)x!));
                Assert.Null(result["Error"]);
            }
            if (kind == "compile")
            {
                Assert.Equal("Success", (string?)result["State"]);
                Assert.Equal(0, (int?)result["ErrorCount"]);
                Assert.Equal(2, (int?)result["WarningCount"]);
                foreach (string field in new[] { "Errors", "Warnings", "Info", "RawMessages" }) Assert.Single(result[field]!.AsArray());
            }
            if (kind == "lad")
            {
                Assert.True((bool?)result["Meta"]?["verified"]);
                Assert.Equal("missing", (string?)result["Meta"]?["warnings"]?[0]?["missingEnUsIds"]?[0]);
                Assert.Null(result["Meta"]?["contentVerified"]);
                Assert.Equal("verify", (string?)fake.Calls.Last().Arguments["request"]?["Action"]);
            }
            if (kind is "sclImport" or "sclGenerate")
            {
                var calls = fake.Calls.Where(x => x.Operation == (kind == "sclImport" ? "ImportPlcExternalSource" : "GenerateBlocksFromExternalSource")).ToArray();
                Assert.Equal(2, calls.Length);
                Assert.True((bool?)calls[0].Arguments["dryRun"]);
                Assert.False((bool?)calls[1].Arguments["dryRun"]);
                Assert.Equal("fixture-plan", (string?)calls[1].Arguments["expectedPlanHash"]);
            }
            if (kind == "hmiDesign")
            {
                var sent = fake.Calls.Single(x => x.Operation == "hmi-unified.ExecuteUnifiedHmi").Arguments["request"]!;
                Assert.True((bool?)sent["Strict"]);
                Assert.Equal("object", (string?)sent["Inputs"]?[primitive.Text[2]]?["Kind"]);
            }
            if (kind == "hmiTag") Assert.True((bool?)fake.Calls.Last().Arguments["request"]?["RequireVerifiedBinding"]);
            if (kind == "hmiScreen")
            {
                Assert.Equal(800, (int?)fake.Calls.Last().Arguments["request"]?["Width"]);
                Assert.Equal(480, (int?)fake.Calls.Last().Arguments["request"]?["Height"]);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Worker_registration_uses_the_same_implementation(string release)
    {
        var engine = new PlcFoundationEngine(release);
        var fake = new ScaffoldWorkerFake(release);
        WorkerProjectScaffold.Register(release, primitive => engine.ProjectScaffoldOperation = primitive, fake.Call, _ => new JsonObject());
        Assert.IsType<WorkerProjectScaffold>(engine.ProjectScaffoldOperation!.Target);
        var result = Assert.IsType<JsonObject>(engine.ProjectScaffoldOperation(Request("connect", "unused")));
        Assert.True((bool?)result["Meta"]?["startedNew"]);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public void Ambiguous_processes_and_foreign_projects_are_refused_without_writes(string release)
    {
        var fake = new ScaffoldWorkerFake(release);
        fake.Processes.Add(new JsonObject { ["ProcessId"] = 1 }); fake.Processes.Add(new JsonObject { ["ProcessId"] = 2 });
        Assert.Throws<AdapterPreconditionException>(() => fake.Execute(Request("connect", "unused")));
        Assert.DoesNotContain(fake.Calls, x => x.Operation == "Attach");
        fake.OpenForeignProject = true;
        Assert.Throws<AdapterPreconditionException>(() => fake.Execute(Request("createProject", "unused")));
        Assert.DoesNotContain(fake.Calls, x => x.Operation == "CreateProject");
        fake.ProjectFile = "C:/fixture/P.ap" + release;
        Assert.Throws<AdapterPreconditionException>(() => fake.Execute(Request("createProject", "unused")));
    }

    [Theory, MemberData(nameof(Primitives))]
    public void Malformed_requests_are_refused_before_any_worker_operation(string release, string kind)
    {
        var fake = new ScaffoldWorkerFake(release);
        var request = Request(kind, "unused"); request.Text = new[] { "unexpected" };
        if (kind is "compile" or "hmiInfo") request.Flags = new[] { true };
        Assert.Throws<ArgumentException>(() => fake.Execute(request));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("20", "hmiInfo"), InlineData("21", "hmiInfo")]
    [InlineData("20", "hmiConnect"), InlineData("21", "hmiConnect")]
    [InlineData("20", "hmiScreen"), InlineData("21", "hmiScreen")]
    [InlineData("20", "hmiDesign"), InlineData("21", "hmiDesign")]
    [InlineData("20", "hmiTag"), InlineData("21", "hmiTag")]
    [InlineData("20", "compile"), InlineData("21", "compile")]
    public void Family_capability_failures_are_propagated_as_failures(string release, string kind)
    {
        var fake = new ScaffoldWorkerFake(release) { ProjectFile = "C:/fixture/P.ap" + release,
            ReturnedFailureOperation = kind == "compile" ? "plc-software.ExecutePlcOrganisation"
                : kind == "hmiInfo" ? "hmi-generic.ExecuteHmi" : "hmi-unified.ExecuteUnifiedHmi" };
        var error = Assert.Throws<NotSupportedException>(() => fake.Execute(Request(kind, "unused")));
        Assert.Equal("fixture unsupported capability", error.Message);
    }

    [Theory]
    [InlineData("20", "sclImport"), InlineData("21", "sclImport")]
    [InlineData("20", "sclGenerate"), InlineData("21", "sclGenerate")]
    public void A_failed_source_execution_never_reports_success_or_retries(string release, string kind)
    {
        var fake = new ScaffoldWorkerFake(release) { ProjectFile = "C:/fixture/P.ap" + release, SourceExecutionFailed = true };
        var error = Assert.Throws<InvalidOperationException>(() => fake.Execute(Request(kind, "unused")));
        Assert.Equal("fixture source failure", error.Message);
        Assert.Equal(2, fake.Calls.Count(x => x.Operation == (kind == "sclImport" ? "ImportPlcExternalSource" : "GenerateBlocksFromExternalSource")));
    }

    internal static ProjectScaffoldPrimitive Request(string kind, string directory) => new() { Kind = kind,
        Text = kind switch {
            "connect" or "save" => Array.Empty<string>(), "createProject" => new[] { directory, "P" },
            "addDevice" => new[] { "", "", "PLC_1", "S7-1500" }, "lad" => new[] { "PLC_1", "", directory, "Main" },
            "sclImport" => new[] { "PLC_1", "", "Main.scl" }, "sclGenerate" => new[] { "PLC_1", "Main.scl" },
            "compile" => new[] { "PLC_1" }, "hmiInfo" => new[] { "HMI_RT_1" },
            "hmiConnect" => new[] { "HMI_RT_1", "Connection", "PLC_1" }, "hmiScreen" => new[] { "HMI_RT_1", "Main" },
            "hmiDesign" => new[] { "HMI_RT_1", "Main", "{\"items\":[]}" },
            "hmiTag" => new[] { "HMI_RT_1", "Default tag table", "Start", "Bool", "PLC_1", "Start", "Connection", "%M0.0" },
            _ => throw new ArgumentException(kind) },
        Numbers = kind == "hmiScreen" ? new uint[] { 800, 480 } : Array.Empty<uint>(),
        Flags = kind is "hmiDesign" or "hmiTag" ? new[] { true } : Array.Empty<bool>() };
}
