using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Siemens.Engineering.Safety;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Safety;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;

internal static class SafetyLoginFixture
{
    internal static int Run(string caller, string scenario)
    {
        const string password = "fixture-login-password";
        Exception loginError = new UnauthorizedAccessException("private vendor login detail " + password);
        Exception loss = scenario.EndsWith("disposed", StringComparison.Ordinal) ? new ObjectDisposedException("fixture")
            : scenario.EndsWith("com", StringComparison.Ordinal) ? new COMException("fixture transport")
            : new Siemens.Engineering.NonRecoverableException();
        var admin = new SafetyAdministration {
            LoggedOnAfterLogin = scenario is "on" or "on-cleanup" or "success" || scenario.StartsWith("logoff-"),
            LoginError = scenario == "success" ? null : scenario.StartsWith("login-") ? loss : loginError,
            ReadError = scenario.StartsWith("read-") ? loss : scenario.StartsWith("unreadable") ? new IOException("private native read") : null,
            LogoffError = scenario.StartsWith("logoff-") ? loss : scenario.EndsWith("-cleanup") ? new IOException("private native cleanup") : null
        };
        var session = new SafetySession(admin);
        if (scenario == "scope-unavailable")
        {
            var group = new Siemens.Engineering.SW.Blocks.PlcBlockGroup { Name = "Group/one" };
            group.Blocks.Add(new Siemens.Engineering.SW.Blocks.PlcBlock { Name = "Main" });
            session.GetSoftwareContainer("CPU").Software.BlockGroup.Groups.Add(group);
        }
        var engine = new PlcFoundationEngine(session, caller == "legacy-prior");
        var row = new JsonObject { ["framework"] = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName };
        var dispatcher = new FoundationWorkerDispatcher(engine, error => {
            row["exception"] = error.GetType().Name;
            row["primitiveData"] = error.Data.Cast<DictionaryEntry>().All(pair => pair.Key is string && pair.Value is string or bool);
            row["hasInnerException"] = error.InnerException != null;
            row["sameLoss"] = ReferenceEquals(error, loss);
            row["legacyCode"] = (error as PlcSoftwareException)?.Code;
        });
        string method = caller switch { "foundation" => "CompileSoftware", "standalone" => "ManagePlcSafety",
            "legacy" or "legacy-prior" => "CompileLegacy", _ => throw new ArgumentException("Unknown fixture caller.") };
        var arguments = new { softwarePath = "CPU", password, dryRun = false, confirm = true, expectedProjectFile = PlcFoundationEngine.ProjectPath };
        string? request = Console.IsInputRedirected ? Console.ReadLine() : null;
        var response = dispatcher.Dispatch(new ChannelRequest(1, "adapter." + method,
            string.IsNullOrEmpty(request) ? WorkerJson.Serialize(arguments) : request!, (_, _) => { }));
        if (response.Failure is { } failure)
        {
            row["message"] = failure.Message;
            row["code"] = failure.Code;
            row["outcome"] = failure.Outcome.ToString();
            row["evidence"] = JsonNode.Parse(failure.EvidenceJson);
        }
        else row["result"] = JsonNode.Parse(response.ResultJson);
        row["calls"] = JsonNode.Parse(WorkerJson.Serialize(admin.Calls));
        row["compileCalls"] = session.GetSoftwareContainer("CPU").Software.CompileCalls;
        row["mutationStarted"] = engine.Legacy.MutationStarted;
        Console.WriteLine(row.ToJsonString());
        return 0;
    }
}
