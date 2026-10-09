using System;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static bool IsUnified(object software)
        {
#if PLC_HMI_UNIFIED
            return software is Siemens.Engineering.HmiUnified.HmiSoftware;
#else
            return false;
#endif
        }
        public CompileNativeTarget CompileTarget(CompileRequest request)
        {
            if (request.Entry == "CompileDevice")
            {
                var hardware = _session.ExactEngineeringHardware(request.DevicePath, request.ItemPath);
                return new CompileNativeTarget { Owner = hardware, OfflineOwner = hardware, Kind = hardware.GetType().Name, Name = hardware.Name,
                    Compiler = ((IEngineeringServiceProvider)hardware).GetService<ICompilable>() };
            }
            var container = _session.ResolveSoftwareContainerUncached(request.SoftwarePath) ?? throw new InvalidOperationException("Exact software container unavailable.");
            var software = container.Software ?? throw new InvalidOperationException("Software unavailable.");
            bool hmi = request.Entry == "CompileHmiDiagnostics";
            if (hmi ? !(software is Siemens.Engineering.Hmi.HmiTarget || IsUnified(software)) : !(software is PlcSoftware)) CandidatePrimitives.Fail("unsupported", "entry-software-kind");
            var item = container.Parent as DeviceItem ?? throw new InvalidOperationException("Software device item unavailable.");
            // PLC and Classic remain software-only. Unified retains the existing owning compiler walk.
            object owner = software; ICompilable? compiler = (software as IEngineeringServiceProvider)?.GetService<ICompilable>();
            string kind = software.GetType().Name;
            if (compiler == null && IsUnified(software))
            {
                IEngineeringObject? node = container;
                for (int depth = 0; node != null && depth < 8; depth++, node = node.Parent)
                {
                    compiler = (node as IEngineeringServiceProvider)?.GetService<ICompilable>();
                    if (compiler != null) { owner = node; kind = software.GetType().Name + " via " + node.GetType().Name; break; }
                }
            }
            return new CompileNativeTarget { Owner = owner, Software = software, SafetyItem = software is PlcSoftware ? item : null,
                OfflineOwner = owner as HardwareObject ?? item, Kind = kind, Name = software.Name, Compiler = compiler };
        }
    }
}
