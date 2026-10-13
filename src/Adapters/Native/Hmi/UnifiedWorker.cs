#if HMI_UNIFIED
using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine : IUnifiedCodecs
    {
        internal TiaMcp.Adapters.Native.Plc.IPlcOrganisationSession UnifiedPlcSession => EngineeringPlcSession;
        public Func<string, string, Dictionary<string, object?>>? AnalyzeUnifiedScript { get; set; }
        internal object? UnifiedReadFault => ReadEngineeringHmiReadFault != null ? ReadEngineeringHmiReadFault() : hmiReadFault;
        private MigrationPages? unifiedPages;
        internal MigrationPages UnifiedPages => unifiedPages ??= new MigrationPages();
        public Func<string, string, UnifiedScriptPair>? ReadUnifiedScriptPair { get; set; }
        public Action<string, string>? RequireUnifiedPlainFile { get; set; }
        public Action<string>? ValidateUnifiedScript { get; set; }
        public Func<string, string, string, IEnumerable<Dictionary<string, object?>>>? DecodeUnifiedDocument { get; set; }
        public Func<Dictionary<string, object?>?>? ReadEngineeringHmiReadFault { get; set; }
        private static UnifiedHmiInput HmiInput(UnifiedValue value)
            => new UnifiedHmiInput { Kind = value.Error == null ? value.Kind : "error", Text = value.Error ?? value.Text,
                Properties = value.Properties.ToDictionary(p => p.Key, p => HmiInput(p.Value), StringComparer.Ordinal),
                Items = value.Items.Select(HmiInput).ToList() };
        public HmiReply ReleaseUnifiedReadCursor(string cursor)
        {
            Check();
            return new HmiReply { Value = new HmiMessage { Message = "Collection state released; no TIA project or process was closed.",
                Meta = HmiMeta.Unstamped(true, ("readOnly", true), ("released", UnifiedPages.Cancel(cursor))) } };
        }
        public HmiReply ExecuteUnified(UnifiedRequest request, bool dryRun = true)
        {
            Check();
            if (UnifiedCapabilities.UsesPreview(request.Operation) && request.DryRun != dryRun)
                throw new ArgumentException("Conflicting Unified preview mode.");
            var unavailable = UnifiedCapabilities.Unsupported(ReleaseKey, request);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var previousInputs = UnifiedRequestValues.Inputs;
            var previousMajor = UnifiedRequestValues.Major;
            var previousRenderer = HmiValues.Renderer;
            var previousCodecs = UnifiedCodecs.Current;
            var previousParse = UnifiedHmiAccess.Inputs;
            var previousToken = HmiExactAccess.ContentToken;
            var previousTrace = HmiValues.TraceWriter;
            var previousExportFailure = HmiValues.ExportFailure;
            UnifiedRequestValues.Inputs = request.Inputs.ToDictionary(p => p.Key, p => UnifiedRequestValues.Decode(p.Value), StringComparer.Ordinal);
            UnifiedRequestValues.Major = int.Parse(ReleaseKey);
            HmiValues.Renderer = EngineeringPlcSession.SerializeEvidence;
            UnifiedCodecs.Current = this;
            UnifiedHmiAccess.Inputs = request.Inputs.ToDictionary(p => p.Key, p => HmiInput(p.Value), StringComparer.Ordinal);
            HmiExactAccess.ContentToken = UnifiedHmiToken;
            HmiValues.TraceWriter = WriteEngineeringHmiTrace;
            HmiValues.ExportFailure = EngineeringPlcSession.RecordExportFailure;
            var reply = new HmiReply();
            var session = new UnifiedSession(this);
            try
            {
                switch (request.Operation)
                {
                    case "GetUnifiedObjectProperties": reply.Value = new UnifiedObjectServicesAdapter(session).ReadUnifiedObjectProperties(request.SoftwarePath, request.ObjectPathJson, request.Offset, request.Limit); break;
                    case "SetUnifiedObjectProperties": reply.Value = new UnifiedObjectServicesAdapter(session).UpdateUnifiedObjectProperties(request.SoftwarePath, request.ObjectPathJson, request.PropertiesJson, request.DryRun); break;
                    case "SetUnifiedMultilingualProperty": reply.Value = new UnifiedObjectServicesAdapter(session).UpdateUnifiedMultilingualProperty(request.SoftwarePath, request.ObjectPathJson, request.Property, request.Culture, request.RawText, request.DryRun); break;
                    case "ValidateUnifiedObject": reply.Value = new UnifiedObjectServicesAdapter(session).ValidateUnifiedObject(request.SoftwarePath, request.ObjectPathJson); break;
                    case "GetUnifiedCrossReferences": reply.Value = new UnifiedObjectServicesAdapter(session).GetUnifiedCrossReferences(request.SoftwarePath, request.ObjectPathJson, request.Filter); break;
                    case "ExportUnifiedEngineeringList": reply.Value = new UnifiedObjectServicesAdapter(session).ExportUnifiedEngineeringList(request.SoftwarePath, request.Category, request.Name, request.DestinationDirectory, request.DryRun); break;
                    case "ManageUnifiedLoggingTag": reply.Value = new UnifiedObjectServicesAdapter(session).ManageUnifiedLoggingTag(request.SoftwarePath, request.TagPathJson, request.Action, request.Name, request.PropertiesJson, request.DryRun); break;
                    case "SetUnifiedLogDuration": reply.Value = new UnifiedObjectServicesAdapter(session).SetUnifiedLogDuration(request.SoftwarePath, request.DurationPathJson, request.Kind, request.Days, request.Hours, request.Minutes, request.Seconds, request.HundredNanoseconds, request.DryRun); break;
                    case "ManageUnifiedOpcUaAlarmType": reply.Value = new UnifiedObjectServicesAdapter(session).ManageUnifiedOpcUaAlarmType(request.SoftwarePath, request.Name, request.NodeId, request.Connection, request.Action, request.DryRun); break;
                    case "GetUnifiedPlantObject": reply.Value = new UnifiedObjectServicesAdapter(session).ReadUnifiedPlantObject(request.PlantPath, request.ObjectPathJson, request.Offset, request.Limit); break;
                    case "ManageUnifiedPlantNode": reply.Value = new UnifiedObjectServicesAdapter(session).ManageUnifiedPlantNode(request.PlantPath, request.Action, request.PlantObjectType, request.PropertiesJson, request.DryRun); break;
                    case "SetUnifiedPlantObject": reply.Value = new UnifiedObjectServicesAdapter(session).UpdateUnifiedPlantObject(request.PlantPath, request.ObjectPathJson, request.PropertiesJson, request.DryRun); break;
                    case "GetUnifiedObjectEvents": reply.Value = new UnifiedUiModelAdapter(session).ReadUnifiedObjectEvents(request.SoftwarePath, request.ObjectPathJson, request.Offset, request.Limit); break;
                    case "ManageUnifiedObjectParts": reply.Value = new UnifiedUiModelAdapter(session).ManageUnifiedObjectParts(request.SoftwarePath, request.ObjectPathJson, request.Action, request.CollectionProperty, request.PartName, request.PartIndex, request.PartKind, request.PropertiesJson, request.ConfirmDelete, request.DryRun); break;
                    case "ManageUnifiedDynamization": reply.Value = new UnifiedUiModelAdapter(session).ManageUnifiedDynamization(request.SoftwarePath, request.ObjectPathJson, request.PropertyName, request.Action, request.DynamizationKind, request.PropertiesJson, request.MappingEntriesJson, request.ConfirmDelete, request.DryRun); break;
                    case "ManageUnifiedScreenLayout": reply.Value = new UnifiedUiModelAdapter(session).ManageUnifiedScreenLayout(request.SoftwarePath, request.ObjectPathJson, request.Action, request.Name, request.PropertiesJson, request.ConfirmDelete, request.DryRun); break;
                    case "ManageUnifiedListEntries": reply.Value = new UnifiedUiModelAdapter(session).ManageUnifiedListEntries(request.SoftwarePath, request.Category, request.ListName, request.Action, request.EntryKey, request.EntryJson, request.ConfirmDelete, request.DryRun); break;
                    case "GetUnifiedAlarmCommon": reply.Value = new UnifiedUiModelAdapter(session).ReadUnifiedAlarmCommon(request.SoftwarePath, request.Category, request.Name, request.Offset, request.Limit); break;
                    case "GetUnifiedAuditSettings": reply.Value = new UnifiedUiModelAdapter(session).ReadUnifiedAuditSettings(request.SoftwarePath, request.Category, request.Name, request.Offset, request.Limit); break;
                    case "ImportUnifiedEngineeringList": reply.Value = new UnifiedEngineeringAdapter(session).ImportUnifiedEngineeringList(request.SoftwarePath, request.Category, request.FilePath, request.ExpectedNamesJson, request.DryRun); break;
                    case "ListUnifiedEngineeringObjects": reply.Value = new UnifiedEngineeringAdapter(session).ReadUnifiedEngineeringObjects(request.SoftwarePath, request.Category, request.Name, request.Offset, request.Limit); break;
                    case "ManageUnifiedEngineeringObject": reply.Value = new UnifiedEngineeringAdapter(session).ManageUnifiedEngineeringObject(request.SoftwarePath, request.Category, request.Name, request.Action, request.PropertiesJson, request.DryRun); break;
                    case "ManageUnifiedEvent": reply.Value = new UnifiedEventsAdapter(session).ManageUnifiedEvent(request.SoftwarePath, request.ObjectPathJson, request.EventType, request.Action, request.PropertyName, request.ScriptPropertiesJson, request.ExpectedToken, request.DryRun); break;
                    case "ExchangeUnifiedTags": reply.Value = new UnifiedExchangeAdapter(session).ExchangeUnifiedTags(request.SoftwarePath, request.Action, request.Directory, request.TagTable, request.FileName, request.ExpectedTagNamesJson, request.DryRun); break;
                    case "ExchangeUnifiedScriptModules": reply.Value = new UnifiedExchangeAdapter(session).ExchangeUnifiedScriptModules(request.SoftwarePath, request.Action, request.Directory, request.ModuleName, request.FileName, request.DryRun); break;
                    case "ImportUnifiedOpcUaAlarms": reply.Value = new UnifiedExchangeAdapter(session).ImportUnifiedOpcUaAlarms(request.SoftwarePath, request.ConnectionName, request.Action, request.XmlPath, request.DisplayName, request.DryRun); break;
                    case "SetUnifiedGlobalScript": reply.Value = new GlobalScriptEditAdapter(session).UpdateUnifiedGlobalScript(request.SoftwarePath, request.ExpectedProject, request.ModuleName, request.ScriptCode, request.DryRun, request.ExpectedToken); break;
                    case "GetUnifiedGraphicSelection": reply.Value = new GraphicSelectionAdapter(session, new MigrationReadAdapter(session)).ReadUnifiedGraphicSelection(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemNamesJson, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "GetUnifiedRuntimeSettings": reply.Value = new RuntimeSettingsAdapter(session).ReadUnifiedRuntimeSettings(request.SoftwarePath, request.ExpectedProject, request.FieldsJson); break;
                    case "SetUnifiedRuntimeSettings": reply.Value = new RuntimeSettingsAdapter(session).UpdateUnifiedRuntimeSettings(request.SoftwarePath, request.ExpectedProject, request.ChangesJson, request.DryRun, request.ExpectedToken); break;
                    case "ListUnifiedGlobalScripts": reply.Value = new MigrationReadAdapter(session).ListUnifiedGlobalScripts(request.SoftwarePath, request.ExpectedProject, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "GetUnifiedGlobalScript": reply.Value = new MigrationReadAdapter(session).ReadUnifiedGlobalScript(request.SoftwarePath, request.ExpectedProject, request.ModuleName, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "ListUnifiedTagDefinitions": reply.Value = new MigrationReadAdapter(session).ReadUnifiedTagDefinitions(request.SoftwarePath, request.ExpectedProject, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "GetUnifiedScreenBranch": reply.Value = new MigrationReadAdapter(session).ReadUnifiedScreenBranch(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemName, request.BranchJson, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "GetUnifiedLibraryType": reply.Value = new MigrationReadAdapter(session).ReadUnifiedLibraryType(request.SoftwarePath, request.ExpectedProject, request.TypePath, request.Version, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "ListUnifiedLibraryFolderEntries": reply.Value = new MigrationReadAdapter(session).ListUnifiedLibraryFolder(request.SoftwarePath, request.ExpectedProject, request.FolderPath, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "GetUnifiedFaceplateInstance": reply.Value = new MigrationReadAdapter(session).ReadUnifiedFaceplateInstance(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemName, request.TypePath, request.Version, request.Cursor, request.PageSize, request.BudgetMs); break;
                    case "ReleaseUnifiedReadCursor": reply.Value = new MigrationReadAdapter(session).ReleaseUnifiedReadCursor(request.Cursor); break;
                    default: throw new ArgumentException("Unregistered Unified operation: " + request.Operation);
                }
                if (reply.Value is HmiMessage message)
                {
                    reply.MayHaveChanged = message.Meta.TryGetValue("mayHaveChanged", out var changed) && changed is true;
                    reply.RequiresSessionReset = message.Meta.TryGetValue("requiresExplicitRebind", out var blocked) && blocked is true
                        || reply.MayHaveChanged && message.Meta.TryGetValue("operationSuccess", out var success) && success is false;
                }
            }
            catch (Exception error) when (error.GetBaseException() is not Siemens.Engineering.NonRecoverableException)
            {
                reply.Failure = EngineeringReflectionFailure != null ? EngineeringReflectionFailure(error)
                    : new PlcSoftwareFailure { Code = error.GetType().Name, Message = error.Message };
            }
            finally
            {
                UnifiedRequestValues.Inputs = previousInputs; UnifiedRequestValues.Major = previousMajor;
                HmiValues.Renderer = previousRenderer; UnifiedCodecs.Current = previousCodecs;
                UnifiedHmiAccess.Inputs = previousParse; HmiExactAccess.ContentToken = previousToken;
                HmiValues.TraceWriter = previousTrace; HmiValues.ExportFailure = previousExportFailure;
            }
            return reply;
        }
    }
}
#endif
