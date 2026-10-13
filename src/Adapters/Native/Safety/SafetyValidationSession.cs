using System;
using System.Collections.Generic;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Safety;

internal sealed class SafetyValidationSession
{
    private readonly PlcFoundationEngine _engine;
    private readonly SafetyValidationRequest _request;
    internal SafetyValidationSession(PlcFoundationEngine engine, SafetyValidationRequest request)
    {
        _engine = engine;
        _request = request;
    }
    internal Siemens.Engineering.Project? CurrentProject => _engine.PlcB5Session.CurrentProject;
    internal IDisposable AcquireHmiEditAccess() => _engine.PlcB5Session.AcquireHmiEditAccess();
    internal MasterCopy ExactMasterCopy(string library, string path) => _engine.PlcB5Session.ExactMasterCopy(library, path);
    internal string[] HardwareOwnerPath(object item) => HardwareAddressPrimitives.HardwareOwnerPath(item);
    internal HardwareAddressingReply RunHmiStepTool(string tool, Func<HmiObject, string> action)
        => _engine.RunHmiStep(tool, values => {
            var meta = new HmiObject(values);
            try { return action(meta); }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }, true);
    internal string[] GroupPath()
    {
        Require("group");
        return _request.GroupPath;
    }
    internal SafetyValidationNativePlan Plan()
    {
        Require("plan");
        var plan = _request.Plan;
        var properties = SafetyScalars.Values(plan.Properties);
        if (plan.Signals != null) properties["signals"] = new HmiArray(plan.Signals);
        return new SafetyValidationNativePlan { Writes = plan.Writes, WritesFile = plan.WritesFile, Properties = properties,
            ExportFlags = plan.ExportFlags, DocumentFlags = plan.DocumentFlags, ImportOption = plan.ImportOption };
    }
    private void Require(string stage)
    {
        if (_request.Faults.TryGetValue(stage, out var error)) throw new ArgumentException(error);
    }
}

internal sealed class SafetyValidationNativePlan
{
    internal bool Writes;
    internal bool WritesFile;
    internal HmiObject Properties = new HmiObject();
    internal string[] ExportFlags = Array.Empty<string>();
    internal string[] DocumentFlags = Array.Empty<string>();
    internal string ImportOption = "None";
}
