using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private Action? _resetWorkerFamilyState;
        private string? _workerAnalysisExportPath;
        internal string? WorkerAnalysisExportPath => _workerAnalysisExportPath;

        // The worker supplies only codecs. Native resolution and safety policies
        // belong to the adapter and run on its existing owning lane on every SDK.
        public void ConfigureWorkerFamilyCallbacks(Func<string, object> documentPreflight,
            Func<string, string[]> devicePath)
        {
            Check();
            if (documentPreflight == null) throw new ArgumentNullException(nameof(documentPreflight));
            if (devicePath == null) throw new ArgumentNullException(nameof(devicePath));
            var session = new OrganisationSession(this);
            EngineeringPlcOrganisationSession = session;
            EngineeringStep = (tool, action) => RunHmiStepCore(tool, action, true);
            EngineeringServicesStep = (tool, requiresProject, action) => RunHmiStepCore(tool, action, requiresProject);
            ResolveEngineeringBlock = (software, block) => session.GetBlock(software, block);
            ResolveEngineeringDevice = path => HardwareLegacyDevice(path);
            ResolveEngineeringItem = path => HardwareLegacyItem(path);
            ResolveEngineeringSoftwareContainer = path => session.ReadContainer(path);
            ResolveEngineeringHmiSoftware = path => DefaultHmiSoftware(session, path);
            ResolveEngineeringHmiScreen = (software, screen) => HmiExactAccess.Screen(ResolveEngineeringHmiSoftware(software), screen);
            ResolveEngineeringLibraryDevice = path => session.ExactEngineeringHardware(devicePath(path), Array.Empty<string>());
            ResolveUnifiedHmiDeviceItem = path => HardwareLegacyItem(path);
            ResolveEngineeringUnifiedScripts = (path, software, collection) =>
                UnifiedScriptAccess.Resolve(ResolveEngineeringHmiSoftware, path, software, collection);
            FormatEngineeringException = error => error.ToString();
            EngineeringReflectionFailure = DefaultEngineeringFailure;
            RenderEngineeringValue = DefaultLibraryValue;
            RenderEngineeringReflectionValue = DefaultLibraryValue;
            HmiContentToken = DefaultUnifiedHmiToken;
            HmiReadFault = RecordHmiFaultCore;
            ReadEngineeringHmiReadFault = () => hmiReadFault == null ? null
                : (Dictionary<string, object?>)HmiValues.DeepClone(hmiReadFault)!;
            WriteEngineeringHmiTrace = HmiReadTrace.WriteLine;
            OnlinePlcs = () => Plcs().Select(candidate => candidate.Value).ToList();
            OnlineLog = (level, error, message, values) =>
            {
                if (error != null) session.LogEngineeringFailure(error, message);
            };
            PlcB5AutoOffline = WithWorkerDocumentOfflineRequirement;
            PlcB5ResolvedPath = DefaultB5ContainerPath;
            PlcB5DocumentPreflight = documentPreflight;
            EngineeringLibraryOptionKind = OptionalLibraryKind;
            EngineeringBlockGroupPath = group => session.GetPlcBlockGroupPath((PlcBlockGroup)group);
            EngineeringRecordExportPath = path => _workerAnalysisExportPath = path;
            AdmitHardwareAmlExport = HardwareAmlPolicy.RequireExportTarget;
            UpdateEngineeringReflectionLookupPaths = paths => reflectionLookupPaths = paths;
            EngineeringReflectionLookupPaths = () => reflectionLookupPaths;
            _resetWorkerFamilyState = ResetWorkerFamilyState;
        }

        private static PlcSoftwareFailure DefaultEngineeringFailure(Exception error)
            => new PlcSoftwareFailure { Code = error is PlcSoftwareException failure ? failure.Code : error.GetType().Name,
                Message = error.Message, Candidates = (error as PlcSoftwareException)?.Candidates?.ToArray() };

        private bool WithWorkerDocumentOfflineRequirement(string path, Func<bool> action)
            => OnlineOfflinePolicy.WithOfflineRequirement(action, () =>
            {
                var software = EngineeringPlcSession.ExactPlcForEngineering(path, false);
                var provider = EngineeringPlcSession.ResolvePlcService<Siemens.Engineering.Online.OnlineProvider>(path, software);
                return provider?.State.ToString().ToLowerInvariant() ?? "unknown";
            });

        private void ResetWorkerFamilyState()
        {
            hmiReadFault = null;
            _hmiTokenGeneration = Guid.NewGuid().ToString("N");
            reflectionLookupPaths = null;
            _workerAnalysisExportPath = null;
#if HMI_UNIFIED
            unifiedPages?.Dispose(); unifiedPages = null;
#endif
        }
    }
}
