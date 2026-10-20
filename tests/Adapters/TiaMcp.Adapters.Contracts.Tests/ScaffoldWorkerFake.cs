using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;

namespace TiaMcp.Adapters.Contracts.Tests;

internal sealed class ScaffoldWorkerFake
{
    internal string Release { get; }
    internal string ProjectFile { get; set; } = "";
    internal bool Attached { get; set; }
    internal bool OpenForeignProject { get; set; }
    internal bool FailedProbe { get; set; }
    internal string? FailOperation { get; set; }
    internal string? ReturnedFailureOperation { get; set; }
    internal bool SourceExecutionFailed { get; set; }
    internal JsonArray Processes { get; } = new();
    internal List<(string Operation, JsonObject Arguments)> Calls { get; } = new();
    internal List<string> Primitives { get; } = new();
    internal List<string> DeviceRequests { get; } = new();
    internal WorkerProjectScaffold Worker { get; }
    internal int CompilerErrors { get; set; }

    internal ScaffoldWorkerFake(string release)
    {
        Release = release;
        Worker = new WorkerProjectScaffold(release, Call, _ => new JsonObject { ["fixture"] = true });
    }

    internal object Execute(ProjectScaffoldPrimitive primitive)
    { Primitives.Add(primitive.Kind); return Worker.Execute(primitive); }

    internal JsonNode? Call(string operation, JsonObject args)
    {
        Calls.Add((operation, (JsonObject)args.DeepClone()));
        if (operation == FailOperation) throw new InvalidOperationException("fixture failure: " + operation);
        if (operation == ReturnedFailureOperation)
            return new JsonObject { ["Failure"] = new JsonObject { ["Code"] = "NotSupportedException", ["Message"] = "fixture unsupported capability" } };
        if ((bool?)args["dryRun"] == false)
        {
            if ((bool?)args["confirm"] != true || string.IsNullOrEmpty((string?)args["expectedProjectFile"]))
                throw new InvalidOperationException("Fixture requires confirmation and project identity.");
        }
        switch (operation)
        {
            case "ReadState": return new JsonObject { ["IsAttached"] = Attached, ["ProcessId"] = Attached ? 101 : null,
                [nameof(ProjectFile)] = ProjectFile, ["IsLocalSession"] = false };
            case "ReadPortalProcessProjects": return new JsonObject { [nameof(Processes)] = Processes.DeepClone() };
            case "ListProjects": return OpenForeignProject ? new JsonArray(new JsonObject { ["Name"] = "Foreign" }) : new JsonArray();
            case "Attach":
            case "project-lifecycle.ConnectIsolatedPortal": Attached = true; return new JsonObject();
            case "BindProject": ProjectFile = (string)args["expectedProjectFile"]!; return new JsonObject();
            case "CreateProject": ProjectFile = (string)args["expectedProjectFile"]!; return new JsonObject();
            case "SaveProject": return new JsonObject();
            case "ImportPlcExternalSource":
            case "GenerateBlocksFromExternalSource": return new JsonObject { ["PlanHash"] = "fixture-plan",
                ["Executed"] = (bool?)args["dryRun"] == false && !SourceExecutionFailed, ["Error"] = "fixture source failure" };
            case "hardware-devices.HardwareCreateDeviceFallback":
                DeviceRequests.Add((string?)args["preferredMlfb"] + "|" + (string?)args["preferredVersion"] + "|" + (string?)args["deviceName"] + "|" + (string?)args["family"]);
                return new JsonObject {
                ["Device"] = FailedProbe ? null : new JsonObject { ["Name"] = args["deviceName"]!.DeepClone() },
                ["MlfbUsed"] = "fixture-mlfb", ["VersionUsed"] = "V1", ["Attempts"] = new JsonArray("first", "second"), ["Error"] = "fixture probe failure" };
            case "plc-documents.ExecutePlcB5": return new JsonObject { ["Value"] = true, ["ImportedNames"] = new JsonArray("Main") };
            case "plc-software.ExecutePlcOrganisation":
                if ((string?)args["request"]?["Action"] != "compile") return new JsonObject { ["Found"] = true };
                return new JsonObject { ["Compiler"] = new JsonObject { ["State"] = CompilerErrors == 0 ? "Success" : "Error", ["ErrorCount"] = CompilerErrors,
                    ["WarningCount"] = 2, ["Errors"] = new JsonArray("error"), ["Warnings"] = new JsonArray("warning"),
                    ["Info"] = new JsonArray("info"), ["RawMessages"] = new JsonArray("raw"), ["Meta"] = new JsonObject { ["effectiveState"] = "Success", ["success"] = true } } };
            case "hmi-generic.ExecuteHmi": return new JsonObject { ["Value"] = new JsonObject { ["Name"] = "HMI_RT_1", ["ProgramType"] = "Unified", ["Screens"] = new JsonArray("Main") } };
            case "hmi-unified.ExecuteUnifiedHmi":
                var request = JsonSerializer.Deserialize<UnifiedHmiRequest>(args["request"]!.ToJsonString())!;
                return new JsonObject { ["Value"] = new JsonObject { ["Message"] = request.Operation, ["Meta"] = new JsonObject { ["success"] = true } } };
            default: throw new InvalidOperationException("Unhandled fixture operation: " + operation);
        }
    }
}
