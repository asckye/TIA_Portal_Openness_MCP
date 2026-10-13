using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcp.Shared;

internal sealed class TestSuitePortService
{
    private readonly OptionalProductPort _port;
    public TestSuitePortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal TestSuitePortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalProductPort(call, identity);
    }
    public ResponseMessage ReadTestSuiteCases(string category, string name = "", int offset = 0, int limit = 100, string kind = "case")
    {
        var request = new OptionalProductRequest { Operation = "ListTestSuiteCases", Category = category, Name = name, Offset = offset, Limit = limit, Kind = kind };
        return _port.Invoke(request, () =>
        {
            var plan = TestSuiteLogic.ValidateReadRequest(category, kind, offset, limit);
            request.Plan.Kind = plan;
        });
    }
    public ResponseMessage ExchangeTestSuiteCase(string category, string action, string name, string filePath = "", string importOptions = "None", string loadOptions = "", bool dryRun = true, string kind = "case")
    {
        var request = new OptionalProductRequest { Operation = "ExchangeTestSuiteCase", Category = category, Action = action, Name = name, FilePath = filePath, ImportOptions = importOptions, LoadOptions = loadOptions, DryRun = dryRun, Kind = kind };
        return _port.Invoke(request, () =>
        {
            var plan = TestSuiteLogic.ValidateExchangeRequest(category, action, name, filePath, importOptions, loadOptions, kind, dryRun);
            request.Plan.Kind = plan.Kind;
            request.Plan.Writes = plan.Writes;
            request.Plan.ImportOption = plan.ImportOption;
            request.Plan.LoadFlags = plan.LoadFlags;
        });
    }
    public ResponseMessage RunTestSuiteCase(string category, string name = "", bool confirmExternalExecution = false, bool dryRun = true, string namesJson = "[]", bool runAll = false, string kind = "case")
    {
        var request = new OptionalProductRequest { Operation = "RunTestSuiteCase", Category = category, Name = name, ConfirmExternalExecution = confirmExternalExecution, DryRun = dryRun, NamesJson = namesJson, RunAll = runAll, Kind = kind };
        return _port.Invoke(request, () =>
        {
            var plan = TestSuiteLogic.ValidateRunRequest(category, name, namesJson, runAll, kind, confirmExternalExecution, dryRun);
            request.Plan.Kind = plan.Kind;
            request.Plan.Names = plan.Names;
            request.Plan.External = plan.External;
        });
    }
    public ResponseMessage ManageTestSuiteCase(string category, string name, string action = "read", string kind = "case", string newName = "", string softwarePath = "", string instanceName = "", string executionMode = "", string opcUaServerAddress = "", string serverInterfaceType = "", string interfaceFolderPath = "", string updateOptions = "", string scopeJson = "[]", string targetName = "", string masterCopyPath = "", string libraryName = "", bool dryRun = true)
    {
        var request = new OptionalProductRequest { Operation = "ManageTestSuiteCase", Category = category, Name = name, Action = action, Kind = kind, NewName = newName, SoftwarePath = softwarePath, InstanceName = instanceName, ExecutionMode = executionMode, OpcUaServerAddress = opcUaServerAddress, ServerInterfaceType = serverInterfaceType, InterfaceFolderPath = interfaceFolderPath, UpdateOptions = updateOptions, ScopeJson = scopeJson, TargetName = targetName, MasterCopyPath = masterCopyPath, LibraryName = libraryName, DryRun = dryRun };
        return _port.Invoke(request, () =>
        {
            var plan = TestSuiteLogic.ValidateManageRequest(category, name, action, kind, newName, softwarePath, instanceName, executionMode, opcUaServerAddress, serverInterfaceType, interfaceFolderPath, updateOptions, scopeJson, targetName, masterCopyPath, dryRun);
            request.Plan.Kind = plan.Kind;
            request.Plan.Writes = plan.Writes;
            request.Plan.ExecutionMode = plan.ExecutionMode;
            request.Plan.ServerInterface = plan.ServerInterface;
            request.Plan.UpdateOption = plan.UpdateOption;
            request.Plan.Scope = Array.ConvertAll(plan.Scope, entry => new TestSuiteScope { Kind = entry.Kind, Name = entry.Name, SoftwarePath = entry.SoftwarePath, GroupPath = entry.GroupPath });
        });
    }
}
