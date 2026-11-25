using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Online;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Vci;
using TiaMcp.PlcWorker;

namespace TiaMcp.Engine.Tests;

internal static class RefusalFixture
{
    internal static object Reply(string scenario)
    {
        var parts = scenario.Split('/');
        switch (parts[0])
        {
            case "CreatePlcTag":
            case "CreatePlcTagTable":
            case "CreatePlcUserConstant":
            case "ImportPlcTagTable": return Tag(parts[0], parts.Length == 1 ? "unchanged" : parts[1]);
            case "CreatePlcInstanceDb":
                var group = new PlcBlockGroup();
                group.Blocks.Creating = _ => { };
                var engine = new TiaMcp.Adapters.PlcFoundationEngine();
                var step = engine.RunHmiStep(scenario, meta => {
                    meta["mayHaveChanged"] = true;
                    PlcOrganisationAdapter.CreateInstanceDb(group, "DriveDB", true, 0, new FB(), meta);
                    return "Created";
                }, requiresProject: true);
                return new PlcSoftwareReply { Message = step.Message, Meta = step.Meta,
                    MayHaveChanged = step.MayHaveChanged, OutcomeUnknown = step.OutcomeUnknown };
            case "CreateVersionControlWorkspace":
                var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N")));
                try
                {
                    var vci = new VersionControlAdapter { Creating = _ => { } };
                    return VersionControlAdapter.WithWriteOutcome(new VersionControlRequest { Operation = scenario, DryRun = false },
                        vci.CreateVersionControlWorkspace("Line", dir.FullName, false));
                }
                finally { dir.Delete(); }
            default: return Online(scenario);
        }
    }

    private static object Tag(string tool, string outcome)
    {
        var group = new PlcTagTableGroup();
        var table = new PlcTagTable();
        void Inject<T>(NamedObjects<T> composition) where T : PlcObject, new() => composition.OnCreate = item => {
            if (outcome == "changed") composition.Add(item);
            if (outcome == "unreadable") composition.Unreadable = true;
            throw new EngineeringException();
        };
        Inject(group.TagTables);
        Inject(table.Tags);
        Inject(table.UserConstants);
        try
        {
            switch (tool)
            {
                case "CreatePlcTag": PlcTagWrites.CreateTag(table, "Ready", "Bool", "%M0.0"); break;
                case "CreatePlcUserConstant": PlcTagWrites.CreateConstant(table, "Ready", "Bool", "true"); break;
                case "CreatePlcTagTable": PlcTagWrites.CreateTable(group, "Signals"); break;
                case "ImportPlcTagTable": TagWriteRefusalTests.Import(group.TagTables); break;
            }
        }
        catch (EngineeringException error)
        {
            var failure = WorkerFailurePolicy.Classify(error, enteredOperation: true, readOnly: false);
            return new { error.Message, failure.Code, Outcome = failure.Outcome.ToString(), Evidence = WorkerJson.Evidence(error) };
        }
        throw new InvalidOperationException("Expected a refused tag write.");
    }

    private static OnlineReply Online(string scenario)
    {
        var adapter = new OnlineDownloadAdapter();
        var session = adapter.FixtureSession;
        var provider = session.Online;
        provider.Read = () => "Offline";
        var interfaces = provider.Configuration.Modes[0].PcInterfaces;
        string target = "";
        const string ip = "192.0.2.3";
        switch (scenario)
        {
            case "multi-interface":
                interfaces[0].TargetInterfaces[0].Addresses.FindUnavailable = true;
                interfaces.Add(new ConfigurationPcInterface("Second LAN"));
                break;
            case "missing-rh":
                session.Failure = "no-rh";
                provider.Read = () => "Online";
                target = "primary";
                break;
            case "non-rh-plc":
                session.Failure = "no-rh";
                target = "backup";
                break;
            case "standard-plc":
                session.Failure = "no-rh";
                break;
            case "apply-false":
                provider.Configuration.Apply = () => false;
                break;
            case "disconnect":
                provider.Disconnect = () => { };
                break;
            case "disconnect-refused":
                provider.Disconnect = () => throw new InvalidOperationException("Offline transition refused.");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        bool disconnect = scenario.StartsWith("disconnect", StringComparison.Ordinal);
        var request = new OnlineRequest { Operation = disconnect ? "DisconnectOnlinePlc" : "ConnectOnlinePlc", DryRun = false };
        object value = disconnect ? (object)adapter.GoOffline("PLC") : adapter.GoOnline("PLC", ip, null, null, null, target, null, false);
        return OnlineCompletionPolicy.Complete(request, value, true);
    }
}
