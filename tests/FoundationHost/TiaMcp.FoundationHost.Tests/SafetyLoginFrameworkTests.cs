using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

public sealed class SafetyLoginFrameworkTests
{
    internal static JsonObject RunFixture(string caller, string scenario, JsonObject? request = null)
    {
        const string project = "TiaMcp.PlcWorker.FaultFixture";
        string exe = TestPaths.WorkerFixture(project, project + ".exe", "net48");
#if DEBUG
        if (Environment.GetEnvironmentVariable("TIA_MCP_TEST_WORKER_FIXTURE_ROOT") == null)
            exe = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "tests", "WorkerChannel", project,
                "bin", "Debug", "net48", project + ".exe");
#endif
        var start = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        foreach (string argument in new[] { "--safety-login", caller, scenario }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardInput.WriteLine(request?.ToJsonString());
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(30000), "The offline safety login fixture did not exit.");
        Assert.Equal(0, process.ExitCode);
        var row = JsonNode.Parse(output.GetAwaiter().GetResult())!.AsObject();
        Assert.Equal(".NETFramework,Version=v4.8", (string?)row["framework"]);
        return row;
    }

    [Theory]
    [InlineData("foundation", "off")][InlineData("foundation", "on")][InlineData("foundation", "unreadable")]
    [InlineData("legacy", "off")][InlineData("legacy", "on")][InlineData("legacy", "unreadable")]
    [InlineData("legacy-prior", "off")]
    public void Compile_login_transports_readback_and_classification_on_net48(string caller, string scenario)
    {
        var row = RunFixture(caller, scenario);
        bool legacy = caller.StartsWith("legacy");
        bool unknown = scenario != "off" || caller == "legacy-prior";
        Assert.Equal(legacy ? "PlcSoftwareException" : "InvalidOperationException", (string?)row["exception"]);
        Assert.True((bool?)row["primitiveData"]);
        Assert.False((bool?)row["hasInnerException"]);
        var evidence = row["evidence"]!;
        Assert.Equal("safety-offline-login", (string?)evidence["phase"]);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, (string?)evidence["exceptionType"]);
        Assert.False((bool?)evidence["compileStarted"]);
        Assert.Equal(scenario == "unreadable" ? (bool?)null : scenario == "on", (bool?)evidence["loggedOnAfterFailure"]);
        Assert.Equal(unknown ? "Unknown" : "RejectedBeforeNative", (string?)row["outcome"]);
        if (legacy)
        {
            Assert.Equal(unknown, (bool?)row["mutationStarted"]);
            Assert.Equal(unknown ? "OpennessError" : "AccessDenied", (string?)row["legacyCode"]);
        }
        var calls = new List<string> { "precheck" };
        calls.Add("login"); calls.Add("readback");
        if (scenario != "off") calls.Add("logoff");
        Assert.Equal(calls, Calls(row));
        Assert.Equal(0, (int?)row["compileCalls"]);
        Assert.DoesNotContain("fixture-login-password", row.ToJsonString());
        Assert.DoesNotContain("private vendor login detail", row.ToJsonString());
    }

    [Theory]
    [InlineData("off")][InlineData("on")][InlineData("unreadable")][InlineData("success")]
    public void ManagePlcSafety_login_runs_native_case_on_net48(string scenario)
    {
        var row = RunFixture("standalone", scenario);
        var reply = row["result"]!;
        Assert.Equal(scenario == "on" || scenario == "unreadable", (bool?)reply["OutcomeUnknown"]);
        Assert.Equal(scenario == "success", (bool?)reply["Meta"]!["apiCallSuccess"]);
        var calls = new List<string> { "precheck", "precheck", "login", "readback" };
        if (scenario == "success") calls.Add("readback");
        else
        {
            if (scenario != "off") calls.Add("logoff");
            Assert.Equal(scenario == "off" ? "AccessDenied" : "ReadOrWriteFailed", (string?)reply["Meta"]!["status"]);
        }
        Assert.Equal(calls, Calls(row));
    }

    [Theory]
    [InlineData("standalone", "login-com")][InlineData("legacy", "login-com")]
    [InlineData("standalone", "login-disposed")][InlineData("legacy", "login-disposed")]
    [InlineData("standalone", "login-nonrecoverable")][InlineData("legacy", "login-nonrecoverable")]
    [InlineData("standalone", "read-com")][InlineData("legacy", "read-com")]
    [InlineData("standalone", "logoff-disposed")][InlineData("legacy", "logoff-disposed")]
    public void Session_loss_reaches_the_existing_handler_on_net48(string caller, string scenario)
    {
        var row = RunFixture(caller, scenario);
        Assert.True((bool?)row["sameLoss"], row.ToJsonString());
        if (caller == "legacy") Assert.True((bool?)row["mutationStarted"]);
        var calls = new List<string> { "precheck" };
        if (caller == "standalone") calls.Add("precheck");
        calls.Add("login");
        if (!scenario.StartsWith("login-")) calls.Add("readback");
        if (scenario.StartsWith("logoff-")) calls.Add("logoff");
        Assert.Equal(calls, Calls(row));
    }

    [Theory]
    [InlineData("login-com")][InlineData("login-disposed")][InlineData("login-nonrecoverable")][InlineData("read-com")]
    public void Foundation_session_loss_is_unknown_before_compile_on_net48(string scenario)
    {
        var row = RunFixture("foundation", scenario);
        Assert.Equal("Unknown", (string?)row["outcome"]);
        Assert.True((bool?)row["evidence"]?["connectionLost"]);
        Assert.False((bool?)row["evidence"]?["compileStarted"]);
        Assert.Equal(scenario == "read-com" ? new[] { "precheck", "login", "readback" } : new[] { "precheck", "login" }, Calls(row));
        Assert.Equal(0, (int?)row["compileCalls"]);
        Assert.DoesNotContain("fixture transport", row.ToJsonString());
    }

    [Fact]
    public void Foundation_loss_during_cleanup_preserves_the_readback_on_net48()
    {
        var row = RunFixture("foundation", "logoff-disposed");
        var evidence = row["evidence"]!;
        Assert.Equal("Unknown", (string?)row["outcome"]);
        Assert.True((bool?)evidence["loggedOnAfterFailure"]);
        Assert.True((bool?)evidence["logoffFailed"]);
        Assert.True((bool?)evidence["connectionLost"]);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, (string?)evidence["exceptionType"]);
        Assert.Equal(new[] { "precheck", "login", "readback", "logoff" }, Calls(row));
        Assert.Equal(0, (int?)row["compileCalls"]);
    }

    private static string[] Calls(JsonObject row) => row["calls"]!.AsArray().Select(n => (string)n!).ToArray();
}
