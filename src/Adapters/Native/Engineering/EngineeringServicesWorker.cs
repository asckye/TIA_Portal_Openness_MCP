using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<string, bool, Func<Dictionary<string, object?>, string>, HardwareAddressingReply>? EngineeringServicesStep { get; set; }
        private IPlcOrganisationSession EngineeringPlcSession => EngineeringPlcOrganisationSession ?? new OrganisationSession(this);

        private HardwareAddressingReply RunEngineeringServicesStep(string tool, Func<Dictionary<string, object?>, string> action, bool requiresProject = true)
        {
            if (EngineeringServicesStep != null) return EngineeringServicesStep(tool, requiresProject, action);
            return RunHardwareAddressStep(tool, action, requiresProject);
        }

        public HardwareAddressingReply ExecuteEngineeringServices(EngineeringServicesRequest request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting engineering preview mode.");
            string? unavailable = EngineeringServicesCapabilities.Unsupported(ReleaseKey, request.Operation, request.Kind, request.CustomIdentityKey, request.CompilationSettings.Keys);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (request.ValidationError != null) return RunEngineeringServicesStep(request.Operation, _ => throw new ArgumentException(request.ValidationError),
                requiresProject: request.Operation != "CompareLibraries");
            return request.Operation switch {
                "ManageProjectLanguage" => ManageProjectLanguage(request.Action, request.Culture, dryRun),
                "ExportProjectTexts" => ExportProjectTexts(request.FilePath, request.SourceCulture, request.TargetCulture, dryRun),
                "ImportProjectTexts" => ImportProjectTexts(request.FilePath, request.UpdateSourceLanguage, dryRun),
#if ENGINEERING_OBJECT_IDENTIFIER
                "GetObjectIdentifier" => ReadObjectIdentifier(request.Kind, request.DevicePath, request.ItemPath, request.SoftwarePath, request.ObjectPath, request.Identifier),
#endif
                "ShowObjectInEditor" => ShowObjectInEditor(request.Kind, request.DevicePath, request.ItemPath, request.SoftwarePath, request.ObjectPath, dryRun),
#if ENGINEERING_COMPILATION_PROPERTIES || ENGINEERING_COMPILATION_SERVICES
                "ManageProjectCompilationSettings" => ManageProjectCompilationSettings(request.Action, request.CompilationSettings, dryRun),
#endif
                "CompareProjects" => CompareProjects(request.Kind, request.SoftwarePath, request.DevicePath, request.ItemPath, request.TargetProjectName, request.TargetSoftwarePath, request.TargetDevicePath, request.TargetItemPath, request.TargetLibraryName, request.IncludeIdentical, request.MaxDepth, request.Offset, request.Limit, request.SameDevicePaths),
                "CompareLibraries" => CompareLibraries(request.LeftLibraryName, request.RightLibraryName, request.IncludeIdentical, request.MaxDepth, request.Offset, request.Limit),
                "GetProjectSettings" => ReadProjectSettings(request.FolderPath, request.CustomIdentityKey, request.Offset, request.Limit),
                _ => throw new ArgumentException("Unregistered engineering operation: " + request.Operation)
            };
        }

        public object ExactOpenEngineeringLibrary(string libraryName)
            => string.IsNullOrEmpty(libraryName) ? project!.ProjectLibrary
                : PlcGroupOperations.Find(portal!.GlobalLibraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found. Open it in TIA first; no implicit open or close.");
        private Dictionary<string, object?> EngineeringLibraryRef(object library)
        {
            var row = new Dictionary<string, object?> { ["name"] = library is Siemens.Engineering.Library.GlobalLibrary global ? global.Name : "ProjectLibrary", ["libraryClass"] = library.GetType().Name };
            if (library is Siemens.Engineering.Library.ProjectLibrary) row["project"] = project?.Name;
            return row;
        }
    }
}
