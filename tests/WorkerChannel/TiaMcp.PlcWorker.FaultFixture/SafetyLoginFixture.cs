extern alias nativewrites;
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
    private sealed class CompileGroup(Siemens.Engineering.Compiler.ICompilable compiler)
        : Siemens.Engineering.SW.Blocks.PlcBlockGroup, Siemens.Engineering.IEngineeringServiceProvider
    {
        public T? GetService<T>() where T : class => compiler as T;
    }

    private sealed class CompileBlock(Siemens.Engineering.Compiler.ICompilable compiler) : Siemens.Engineering.SW.Blocks.PlcBlock
    {
        public override T? GetService<T>() where T : class => compiler as T;
    }

    internal static int Run(string caller, string scenario)
    {
        if (caller == "native-writes")
        {
            var input = JsonNode.Parse(Console.ReadLine()!)!;
            var probe = nativewrites::TiaMcp.Tests.NativeWrites.NativeWriteFixture.Run(scenario, (string)input["file"]!);
            Console.WriteLine(WorkerJson.Serialize(new { framework = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName, probe }));
            return 0;
        }
        const string password = "fixture-login-password";
        Exception loginError = new UnauthorizedAccessException("private vendor login detail " + password);
        Exception loss = scenario.EndsWith("disposed", StringComparison.Ordinal) ? new ObjectDisposedException("fixture")
            : scenario.EndsWith("com", StringComparison.Ordinal) ? new COMException("fixture transport")
            : new Siemens.Engineering.NonRecoverableException();
        var admin = new SafetyAdministration {
            LoggedOnAfterLogin = scenario is "on" or "on-cleanup" or "success" || scenario.StartsWith("logoff-"),
            LoginError = scenario == "success" ? null : scenario.StartsWith("login-") ? loss : loginError,
            LogoffError = scenario.StartsWith("logoff-") ? loss : scenario.EndsWith("-cleanup") ? new IOException("private native cleanup") : null
        };
        if (scenario.StartsWith("read-")) admin.OnReadback = () => throw loss;
        else if (scenario.StartsWith("unreadable")) admin.OnReadback = () => throw new IOException("private native read");
        var session = new SafetySession(admin);
        if (scenario == "messages-unreadable")
        {
            admin.LoginError = null;
            admin.LoggedOnAfterLogin = true;
            session.GetSoftwareContainer("CPU").Software.ReturnedResult = new Siemens.Engineering.Compiler.CompilerResult();
        }
        if (scenario == "offline-refused-cleanup")
        {
            admin.LoginError = null;
            admin.LoggedOnAfterLogin = true;
        }
        if (scenario == "scoped-messages-unreadable")
        {
            var software = session.GetSoftwareContainer("CPU").Software;
            software.ReturnedResult = new Siemens.Engineering.Compiler.CompilerResult();
            var group = new CompileGroup(software) { Name = "Group/one" };
            group.Blocks.Add(new CompileBlock(software) { Name = "Main" });
            software.BlockGroup.Groups.Add(group);
        }
        if (scenario == "scope-unavailable")
        {
            var group = new Siemens.Engineering.SW.Blocks.PlcBlockGroup { Name = "Group/one" };
            group.Blocks.Add(new Siemens.Engineering.SW.Blocks.PlcBlock { Name = "Main" });
            session.GetSoftwareContainer("CPU").Software.BlockGroup.Groups.Add(group);
        }
        if (caller == "safety-update")
        {
            session.TestWrite = scenario;
            if (scenario == "version-abort") admin.Settings.VersionFailure = new Siemens.Engineering.EngineeringUserAbortException();
            admin.Settings.AttributeFailure = scenario switch {
                "attribute-abort" => new Siemens.Engineering.EngineeringTargetInvocationException(new Siemens.Engineering.EngineeringUserAbortException()),
                "refused" => new Siemens.Engineering.EngineeringSecurityException(),
                "ordinary-refusal" => new Siemens.Engineering.EngineeringTargetInvocationException(),
                "connection-loss" => new Siemens.Engineering.EngineeringTargetInvocationException(new EndOfStreamException()),
                _ => (Exception?)null
            };
            admin.IsSafetyOfflineProgramPasswordSet = false;
            var group = new RuntimeGroup();
            admin.RuntimeGroups.Add(group);
            var reply = new SafetyManagementAdapter(session).ManagePlcSafety("CPU", scenario == "group" ? "updateRuntimeGroup" : "updateSettings",
                runtimeGroup: scenario == "group" ? "Group" : "", dryRun: false);
            Console.WriteLine(WorkerJson.Serialize(new {
                framework = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName, reply,
                storedAttribute = admin.Settings.GetAttribute("EnableFCommunicationIdTag"),
                attributeWritten = group.AttributeWritten || admin.Settings.AttributeWritten,
                cycle = scenario == "group" ? group.Cycle : admin.Settings.Cycle, version = admin.Settings.SafetySystemVersion?.Value }));
            return 0;
        }
        var engine = new PlcFoundationEngine(session, caller == "legacy-prior");
        if (caller == "legacy-worker")
        {
            admin.LoginError = scenario == "disposed" ? new ObjectDisposedException("vendor " + password)
                : new COMException("vendor " + password);
            var compileRequest = new PlcSoftwareRequest { Operation = "RepairAndReimportPlcBlock", Action = "compile", SoftwarePath = "CPU",
                Password = password, DryRun = false };
            var result = engine.ExecutePlcOrganisation(compileRequest, dryRun: false);
            Console.WriteLine(WorkerJson.Serialize(new { framework = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName, result, admin.Calls }));
            return 0;
        }
        if (caller == "generate-source")
        {
            var source = session.GetSoftwareContainer("CPU").Software.Source;
            source.Failure = scenario switch {
                "compiler-message" => new Siemens.Engineering.EngineeringTargetInvocationException((string)JsonNode.Parse(Console.ReadLine()!)!["message"]!),
                "security" => new Siemens.Engineering.EngineeringSecurityException(),
                "products" => new Siemens.Engineering.MissingProductsException(),
                "licence" => new Siemens.Engineering.LicenseNotFoundException(),
                "multiuser" => new Siemens.Engineering.Multiuser.MultiuserException(),
                "wrapped-abort" => new Siemens.Engineering.EngineeringTargetInvocationException(new Siemens.Engineering.EngineeringUserAbortException()),
                "nested-detail" => new Siemens.Engineering.EngineeringTargetInvocationException(new ArgumentException("Generation diagnostic.")),
                "wrapped-security" => new InvalidOperationException("Provider diagnostic.", new Siemens.Engineering.EngineeringSecurityException()),
                "connection" => new Siemens.Engineering.NonRecoverableException(),
                "lost-stream" => new Siemens.Engineering.EngineeringTargetInvocationException(new EndOfStreamException()),
                "disposed" => new Siemens.Engineering.EngineeringObjectDisposedException(),
                "user-abort" => new Siemens.Engineering.EngineeringUserAbortException(),
                _ => new Siemens.Engineering.EngineeringTargetInvocationException()
            };
            var preview = engine.GenerateBlocksFromExternalSource("CPU", source.Name);
            var generation = engine.GenerateBlocksFromExternalSource("CPU", source.Name, false, preview.PlanHash, true, preview.ProjectFile);
            Console.WriteLine(WorkerJson.Serialize(new { framework = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName, generation }));
            return 0;
        }
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
        engine.RefuseSecondOfflineCheck = scenario == "offline-refused-cleanup";
        var arguments = new { softwarePath = "CPU", password,
            dryRun = false, confirm = true, expectedProjectFile = PlcFoundationEngine.ProjectPath };
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
