using System;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Options;

internal sealed class OptionalProductSession
{
    private readonly PlcFoundationEngine _engine;
    private readonly OptionalProductRequest _request;
    internal OptionalProductSession(PlcFoundationEngine engine, OptionalProductRequest request)
    {
        _engine = engine;
        _request = request;
    }
    internal string ReleaseKey => _engine.PlcB5Session.ReleaseKey;
#if PLC_SAFETY
    internal Siemens.Engineering.ProjectBase? CurrentProject => _engine.SecurityProject;
#else
    internal Siemens.Engineering.Project? CurrentProject => _engine.SecurityProject;
#endif
    internal IDisposable AcquireHmiEditAccess() => _engine.PlcB5Session.AcquireHmiEditAccess();
    internal PlcSoftware ExactPlcForEngineering(string path, bool writing) => _engine.PlcB5Session.ExactPlcForEngineering(path, writing);
    internal SoftwareContainer? ResolveSoftwareContainerUncached(string path) => _engine.PlcB5Session.ResolveSoftwareContainerUncached(path);
    internal HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath)
    {
        Validate();
        return _engine.PlcB5Session.ExactEngineeringHardware(devicePath, itemPath);
    }
    internal MasterCopy ExactMasterCopy(string library, string path) => _engine.PlcB5Session.ExactMasterCopy(library, path);
    internal T RequireHardwareUtility<T>(string identifier) where T : HardwareUtility => _engine.OptionalHardwareUtility<T>(identifier);
#if PLC_UNITS
    internal Siemens.Engineering.SW.Units.PlcUnitProvider RequireUnitProvider(PlcSoftware plc) => PlcUnitScope.RequireProvider(plc);
#endif
    internal OptionalProductPlan Plan()
    {
        Validate();
        return _request.Plan;
    }
    internal string[] Files()
    {
        Validate();
        return _request.FilesJson;
    }
    private void Validate()
    {
        if (_request.ValidationError == null) return;
        if (_request.ValidationKind == nameof(InvalidOperationException)) throw new InvalidOperationException(_request.ValidationError);
        throw new ArgumentException(_request.ValidationError);
    }
    internal OptionalProductReply RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
    {
        var reply = _engine.RunHmiStep(tool, values =>
        {
            var meta = new HmiObject(values);
            try { return action(meta); }
            catch (Exception error) when (!OptionalProductEvidence.Issued(meta) && OptionalProductFailures.IsAssemblyLoadFailure(error))
            {
                return RefuseMissingAssembly(meta);
            }
            finally { foreach (var pair in meta) values[pair.Key] = pair.Value; }
        }, requiresProject);
        return new OptionalProductReply { Message = reply.Message, Meta = reply.Meta, RequiresSessionReset = reply.RequiresSessionReset };
    }
    internal static string RefuseMissingAssembly(HmiObject meta)
    {
        const string message = "The optional product Openness assembly is unavailable in this session.";
        OptionalProductEvidence.RecordFailure(meta, new NotSupportedException(message));
        meta["operationNotStarted"] = true;
        meta["rejectionCode"] = "UNSUPPORTED_CAPABILITY";
        meta["operationSuccess"] = false;
        meta["apiCallSuccess"] = false;
        meta["dataComplete"] = false;
        meta["unsupportedReason"] = message;
        meta["error"] = message;
        return message;
    }
}
