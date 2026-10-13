using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Online;

internal sealed class OnlineSession
{
    private readonly IPlcOrganisationSession _session;
    private readonly OnlineRequest _request;
    private readonly Func<List<PlcSoftware>> _plcs;
    internal OnlineSession(IPlcOrganisationSession session, OnlineRequest request, Func<List<PlcSoftware>> plcs,
        Action<int, Exception?, string, object?[]>? log)
    {
        _session = session;
        _request = request;
        _plcs = plcs;
        Log = new OnlineLog(session, log);
    }
    internal OnlineLog Log { get; }
    internal bool IsProjectNull() => _session.IsProjectNull();
    internal Project? CurrentProject => _session.CurrentProject;
    internal PlcSoftware? GetPlcSoftware(string path) => _session.GetPlcSoftware(path);
    internal PlcSoftware? ResolvePlc(string path, bool write) => _session.ResolvePlc(path, write);
    internal T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService
        => _session.ResolvePlcService<T>(path, plc);
    internal PlcSoftware ExactPlcForEngineering(string path, bool write) => _session.ExactPlcForEngineering(path, write);
    internal object ExactMasterCopyPlcSource(string path, string block, bool isBlock) => _session.ExactMasterCopyPlcSource(path, block, isBlock);
    internal Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string devicePath, string itemPath)
        => _session.ExactEngineeringHardware(_request.DevicePath, _request.ItemPath);
    internal List<PlcSoftware> GetAllPlcSoftware() => _plcs();
    internal string AvailablePlcPathsSuffix() => _session.AvailablePlcPathsSuffix();
    internal List<(string Path, bool? Consistent)> ReadPlcConsistency(string path) => _session.ReadPlcConsistency(path);
    internal IDisposable AcquireHmiEditAccess() => _session.AcquireHmiEditAccess();
    internal bool RecoverableAuditError(Exception error) => _session.RecoverableAuditError(error);
    internal string ReadReflectedString(object? owner, string name)
    {
        try { return owner?.GetType().GetProperty(name)?.GetValue(owner)?.ToString() ?? string.Empty; }
        catch { /* swallow(probe-optional): an unavailable route label is represented by an empty string */ return string.Empty; }
    }
    internal List<object?> EnumerateReflectedProperty(object? owner, string name)
    {
        var items = new List<object?>();
        try
        {
            if (owner?.GetType().GetProperty(name)?.GetValue(owner) is System.Collections.IEnumerable sequence)
                foreach (object? item in sequence) items.Add(item);
        }
        catch { /* swallow(enumerate-optional): retain route items collected before an optional composition becomes unavailable */ }
        return items;
    }
    internal Dictionary<string, string> PromptAnswers(string json)
    {
        if (_request.PromptAnswersError != null) throw new ArgumentException(_request.PromptAnswersError);
        return _request.PromptAnswers;
    }
    internal HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action)
    {
        var result = _session.RunHmiStepTool(tool, values =>
        {
            var meta = new HmiObject(values);
            try { return action(meta); }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        });
        return new HmiMessage { Message = result.Message, Meta = result.Meta };
    }
}
