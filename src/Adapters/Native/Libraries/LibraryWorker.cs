using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Engineering;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
#if LIBRARY_COMPARE
using Siemens.Engineering.Library.Compare;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Libraries;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public LibraryReply ExecuteLibrary(LibraryRequest request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting library preview mode.");
            var unavailable = LibraryCapabilities.Unsupported(ReleaseKey, request.Operation, request.Action, request.SourceKind, request.Properties.Keys, request.ForceUpdateMode, request.DeleteUnusedVersionsMode, request.StructureConflictResolutionMode);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (request.ValidationError != null) return new LibraryReply { Step = RunEngineeringServicesStep(request.Operation, _ => throw new ArgumentException(request.ValidationError)) };
            switch (request.Operation)
            {
                case "ManageLibraryTypeVersion": return new LibraryReply { Step = ManageLibraryTypeVersion(request.TypePath, request.Version, request.Action, request.LibraryName, request.NewVersion, request.DependenciesMode, request.Author, request.Comment, request.TargetSoftwarePath, request.DryRun) };
                case "CreateLibraryMasterCopy": return new LibraryReply { Step = CreateLibraryMasterCopy(request.SourceKind, request.SourcePath, request.SoftwarePath, request.FolderPath, request.LibraryName, request.DryRun, request.SourceDevicePath) };
                case "ReadLibraryOverview": return new LibraryReply { Step = ReadLibraryOverview(request.LibraryName, request.IncludeTypes, request.IncludeMasterCopies, request.MaxDepth, request.MaxItems) };
                case "ReadLibraryType": return new LibraryReply { Step = ReadLibraryType(request.LibraryName, request.TypePath, request.Guid, request.Offset, request.Limit) };
                case "CheckLibraryUpdates": return new LibraryReply { Step = CheckLibraryUpdates(request.LibraryName, request.UpdateCheckMode, request.MaxItems) };
                case "ManageLibraryType": return new LibraryReply { Step = ManageLibraryType(request.TypePath, request.Action, request.LibraryName, request.Properties, request.TargetLibraryName, request.ScopeSoftwarePaths, request.DeleteUnusedVersionsMode, request.StructureConflictResolutionMode, request.ForceUpdateMode, request.ConfirmDelete, request.DryRun) };
                case "SynchronizeLibrary": return new LibraryReply { Step = SynchronizeLibrary(request.Action, request.Selection, request.LibraryName, request.TargetLibraryName, request.ScopeSoftwarePaths, request.ForceUpdateMode, request.DeleteUnusedVersionsMode, request.StructureConflictResolutionMode, request.HarmonizeOptions, request.CleanUpMode, request.ConfirmChange, request.DryRun) };
#if LIBRARY_COMPARE
                case "CompareLibraryObjects": return new LibraryReply { Step = CompareLibraryObjects(request.Kind, request.LeftPath, request.RightPath, request.LeftLibraryName, request.RightLibraryName, request.LeftVersion, request.RightVersion, request.IncludeIdentical, request.Offset, request.Limit) };
#endif
                case "ManageLibraryMasterCopy": return new LibraryReply { Step = ManageLibraryMasterCopy(request.SourcePath, request.Action, request.LibraryName, request.DestinationLibraryName, request.DestinationPath, request.DryRun) };
#if LIBRARY_DOCUMENT_IMPORT
                case "ImportLibraryTypeDocuments": return new LibraryReply { Step = ImportLibraryTypeDocuments(request.FilePath, request.FolderPath, request.LibraryName, request.ImportOptions, request.TypePath, request.CreateOptions, request.TargetSoftwarePath, request.TargetGroupKind, request.TargetGroupPath, request.DryRun) };
#endif
                case "ManageGlobalLibrary": return new LibraryReply { Step = ManageGlobalLibrary(request.Action, request.LibraryName, request.FilePath, request.DestinationDirectory, request.OpenMode, request.Upgrade, request.ArchiveName, request.ArchiveMode, request.DryRun) };
                case "ManageLibraryFolder": return new LibraryReply { Step = ManageLibraryFolder(request.FolderKind, request.FolderPath, request.Action, request.LibraryName, request.NewName, request.DryRun) };
                case "ProbeGlobalLibrary": return new LibraryReply { Probe = ProbeGlobalLibrary(request.LibraryPath, request.MaxItems) };
                case "ImportMasterCopyFromGlobalLibrary": return new LibraryReply { Import = ImportMasterCopyFromGlobalLibrary(request.LibraryPath, request.MasterCopyName, request.HmiSoftwarePath, request.ScreenName, request.ImportedItemName, request.Left, request.Top) };
                default: throw new ArgumentException("Unregistered library operation: " + request.Operation);
            }
        }
    }
}
