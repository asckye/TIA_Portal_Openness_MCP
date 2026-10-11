using System;
using System.Linq;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public IPlcOrganisationSession PlcB5Session => EngineeringPlcSession;
        public Func<PlcB5Request, Dictionary<string, object?>>? PlcB5GenerateModelled { get; set; }
        public Func<string, Func<bool>, bool>? PlcB5AutoOffline { get; set; }
        public Func<object?, string>? PlcB5ResolvedPath { get; set; }
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, B5ResolvedPath> b5ResolvedPaths = new System.Runtime.CompilerServices.ConditionalWeakTable<object, B5ResolvedPath>();
        private sealed class B5ResolvedPath { internal string Value = ""; }
        internal void RememberB5Container(object container, string path) => b5ResolvedPaths.GetValue(container, _ => new B5ResolvedPath()).Value = path;
        internal string B5ContainerPath(object? container) => PlcB5ResolvedPath != null ? PlcB5ResolvedPath(container)
            : container != null && b5ResolvedPaths.TryGetValue(container, out var path) ? path.Value : "";
        public Func<string, object>? PlcB5DocumentPreflight { get; set; }
        public PlcB5Reply ExecutePlcB5(PlcB5Request request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting PLC B5 preview mode.");
            var unavailable = PlcB5Capabilities.Unsupported(ReleaseKey, request);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var adapter = new PlcB5Adapter(EngineeringPlcSession, this, request);
            try
            {
                return request.Operation switch {
#if PLC_ALARM_CLASSES
                    "ExportAlarmClasses" => adapter.ExportAlarmClasses(request.SoftwarePath, request.ExportPath),
#endif
#if PLC_ALARM_CLASSES
                    "ImportAlarmClasses" => adapter.ImportAlarmClasses(request.SoftwarePath, request.ImportPath),
#endif
#if PLC_ALARM_TEXTS
                    "ExportAlarmTextLists" => adapter.ExportAlarmTextLists(request.SoftwarePath, request.ExportPath),
#endif
#if PLC_ALARM_TEXTS
                    "ImportAlarmTextLists" => adapter.ImportAlarmTextLists(request.SoftwarePath, request.ImportPath),
#endif
#if PLC_ALARM_TEXTS
                    "ExportAlarmInstanceTexts" => adapter.ExportAlarmInstanceTexts(request.SoftwarePath, request.ExportPath, request.IncludeInfoText, request.IncludeAdditionalTexts, request.IncludeAlarmClass),
#endif
#if PLC_ALARM_TEXTS
                    "ExchangePlcAlarmTextLists" => adapter.ExchangePlcAlarmTextListsXlsx(request.SoftwarePath, request.Action, request.FilePath, request.UnitName, request.UnitKind, request.TextListNamesJson, request.CulturesJson, request.ImportOption, request.ConfirmImport, request.DryRun),
#endif
#if PLC_ALARM_TEXTS
                    "ImportPlcAlarmInstanceTexts" => adapter.ImportPlcAlarmInstanceTexts(request.SoftwarePath, request.FilePath, request.CulturesJson, request.DryRun),
#endif
#if PLC_ALARM_LISTS
                    "ManagePlcAlarmTextList" => adapter.ManagePlcAlarmTextList(request.SoftwarePath, request.Action, request.Name, request.LibraryName, request.MasterCopyPath, request.CopyMode, request.ConfirmDelete, request.Offset, request.Limit, request.DryRun),
#endif
#if PLC_OPC_UA
                    "GenerateOpcUaModelledInterface" => new PlcB5Reply { Data = (PlcB5GenerateModelled ?? throw new NotSupportedException("Missing OPC UA modelled worker extension."))(request) },
                    "GetPlcOpcUaConfiguration" => adapter.GetOpcUaConfig(request.SoftwarePath),
#endif
#if PLC_OPC_UA
                    "ManageOpcUaInterface" => adapter.ManageOpcUaInterface(request.SoftwarePath, request.InterfaceName, request.Action, request.InterfaceType, request.DryRun),
#endif
#if PLC_OPC_UA
                    "SetOpcUaInterfaceEnabled" => adapter.SetOpcUaInterfaceEnabled(request.SoftwarePath, request.InterfaceName, request.Enabled, request.InterfaceType),
#endif
#if PLC_OPC_UA
                    "ExportOpcUaInterface" => adapter.ExportOpcUaInterface(request.SoftwarePath, request.InterfaceName, request.ExportPath, request.InterfaceType),
#endif
#if PLC_OPC_UA
                    "ImportOpcUaInterface" => adapter.ImportOpcUaInterface(request.SoftwarePath, request.ImportPath, request.InterfaceType),
#endif
#if PLC_OPC_UA
                    "GetOpcUaAccessControl" => adapter.ReadOpcUaAccessControl(request.SoftwarePath, request.Section, request.Offset, request.Limit),
#endif
#if PLC_OPC_UA
                    "ManageOpcUaAccessControl" => adapter.ManageOpcUaAccessControl(request.SoftwarePath, request.Action, request.RoleName, request.DefinedInNamespace, request.ProjectRole, request.NamespaceUri, request.Permission, request.Enabled, request.PropertiesJson, request.ConfirmChange, request.DryRun),
#endif
                    "GetPlcBlockScopes" => adapter.ReadPlcBlockScopes(request.SoftwarePath, request.Offset, request.Limit),
#if PLC_DOCUMENT_EXPORT
                    "ManagePlcBlockDocuments" => adapter.ManagePlcBlockDocuments(request.SoftwarePath, request.Action, request.Name, request.GroupPath, request.UnitName, request.UnitKind, request.DirectoryPath, request.ImportOption, request.DryRun, request.VerifyDocumentReadback),
#endif
#if PLC_DOCUMENT_EXPORT
                    "ExportPlcBlockDocuments" or "ExportPlcBlocksDocuments" or "ImportPlcBlockDocuments" or "ImportPlcBlocksDocuments" => adapter.LegacyDocuments(),
                    "ManagePlcDocuments" => adapter.ManagePlcDocuments(request.SoftwarePath, request.Action, request.ObjectKind, request.Name, request.UnitName, request.UnitKind, request.GroupPath, request.DirectoryPath, request.ImportOption, request.LibraryName, request.MasterCopyPath, request.CopyMode, request.TypePath, request.Version, request.UpdatePathsMode, request.DryRun),
#endif
#if PLC_SUPERVISION
                    "ManagePlcSupervision" => adapter.ManagePlcSupervision(request.SoftwarePath, request.Action, request.BlockPath, request.ProviderKind, request.CompositionName, request.EntryName, request.TypeName, request.FilePath, request.AttributesJson, request.Offset, request.Limit, request.ConfirmDelete, request.DryRun),
#endif
#if PLC_SUPERVISION
                    "ExportPlcProDiagInfo" => adapter.ExportPlcProDiagInfo(request.SoftwarePath, request.BlockPath, request.DirectoryPath, request.UnitName, request.UnitKind, request.DryRun),
#endif
#if PLC_SUPERVISION
                    "ExchangePlcSupervisions" => adapter.ExchangePlcSupervisions(request.SoftwarePath, request.Action, request.FilePath, request.ImportOptions, request.DryRun),
#endif
                    _ => throw new ArgumentException("Unregistered PLC B5 operation: " + request.Operation)
                };
            }
            catch (Exception error) when (error.GetBaseException() is not Siemens.Engineering.NonRecoverableException)
            {
                return new PlcB5Reply { Failure = new PlcSoftwareFailure { Code = error is PlcSoftwareException failure ? failure.Code : error.GetType().Name, Message = error.Message }, MayHaveChanged = adapter.MayHaveChanged, RequiresSessionReset = adapter.MayHaveChanged };
            }
        }
    }
}
