using System;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply CompileDevice(string[] devicePath, string[] itemPath, Func<object, bool, PlcCompilerEvidence> collect)
            => _session.RunHmiStepTool("CompileDevice", meta => {
                var owner = _session.ExactEngineeringHardware(devicePath, itemPath);
                meta["target"] = owner.Name; meta["targetType"] = owner.GetType().Name;
                var compilable = ((IEngineeringServiceProvider)owner).GetService<ICompilable>()
                    ?? throw new NotSupportedException("ICompilable is not available on '" + owner.Name + "'.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                meta["mayHaveChanged"] = true;
                CompilerResult result = compilable.Compile();
                meta["compileElapsedMs"] = watch.ElapsedMilliseconds;
                meta["apiCallSuccess"] = true;
                var collected = collect(result, false);
                foreach (var kv in collected.Meta) meta[kv.Key] = kv.Value;
                meta["errors"] = collected.Errors;
                meta["warnings"] = collected.Warnings;
                // envelope: legacy-independent-verdicts
                meta["nativeCompleted"] = true;
                meta["success"] = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                meta["operationSuccess"] = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                return "Hardware compile of '" + owner.Name + "' finished: " + result.State + " (errors " + result.ErrorCount + ", warnings " + result.WarningCount + "). Project not saved.";
            });
    }
}
