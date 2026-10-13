using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Safety;

internal sealed class SafetySession
{
    private readonly PlcFoundationEngine _engine;
    private readonly SafetyRequest _request;
    internal SafetySession(PlcFoundationEngine engine, SafetyRequest request)
    {
        _engine = engine;
        _request = request;
    }

    internal TiaPortal? CurrentPortal => _engine.PlcB5Session.CurrentPortal;
    internal SoftwareContainer? GetSoftwareContainer(string path) => _engine.PlcB5Session.GetSoftwareContainer(path);
    internal IDisposable AcquireHmiEditAccess() => _engine.PlcB5Session.AcquireHmiEditAccess();
    internal PlcSoftware ExactPlcForEngineering(string path, bool writing) => _engine.PlcB5Session.ExactPlcForEngineering(path, writing);
    internal T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService => _engine.PlcB5Session.ResolvePlcService<T>(path, plc);
    internal object ExactMasterCopyPlcSource(string software, string path, bool block) => _engine.PlcB5Session.ExactMasterCopyPlcSource(software, path, block);
    internal void GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> blocks) => _engine.PlcB5Session.GetBlocksRecursive(group, blocks);

    internal HardwareAddressingReply RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        => _engine.RunHmiStep(tool, values => {
            var meta = new HmiObject(values);
            try { return action(meta); }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }, requiresProject);

    private void RequireChanges()
    {
        if (_request.ChangesError != null) throw new ArgumentException(_request.ChangesError);
    }

    internal (HmiObject Scalars, HmiObject Attributes) RuntimeGroupChanges()
    {
        RequireChanges();
        return (SafetyScalars.Values(_request.Scalars), SafetyScalars.Values(_request.Attributes));
    }

    internal SafetyChanges SettingsChanges()
    {
        RequireChanges();
        return new SafetyChanges { Scalars = SafetyScalars.Values(_request.Scalars), Attributes = SafetyScalars.Values(_request.Attributes),
            BlockNumbers = SafetyScalars.Values(_request.BlockNumbers), SafetySystemVersion = _request.SafetySystemVersion };
    }

    internal List<KeyValuePair<string, object>> GlobalChanges()
    {
        RequireChanges();
        return HardwareScalarEvidence.Requested(_request.Scalars).Select(p => new KeyValuePair<string, object>(p.Key, p.Value!)).ToList();
    }
}

internal sealed class SafetyChanges
{
    internal HmiObject Scalars = new HmiObject();
    internal HmiObject Attributes = new HmiObject();
    internal HmiObject BlockNumbers = new HmiObject();
    internal string? SafetySystemVersion;
}
