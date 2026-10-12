using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<Type, string, Dictionary<string, object?>?>? UnifiedPropertyAdmission { get; set; }
        public HmiReply ExecuteUnifiedHmiMetadata(UnifiedHmiRequest request, bool dryRun = true)
        {
            if (!dryRun || !request.DryRun || request.Operation != "ListUnifiedHmiApiTypes"
                && request.Operation != "DescribeUnifiedScreenItemType")
                throw new ArgumentException("Only Unified HMI API metadata reads are admitted.");
            return ExecuteUnifiedHmi(request, dryRun);
        }
        public HmiReply ExecuteUnifiedHmi(UnifiedHmiRequest request, bool dryRun = true)
        {
            Check();
            if (request.IsReadOnly != dryRun) throw new ArgumentException("Conflicting HMI preview mode.", nameof(dryRun));
            if (!PortedFamilies.Available(ReleaseKey, request.Operation)) throw new NotSupportedException("WinCC Unified HMI API requires V19 or later.");
            if (UnifiedHmiCapabilities.Unsupported(ReleaseKey, request.Operation, request.Action, request.ItemType, request.DynamizationType) is string unavailable)
                throw new NotSupportedException(unavailable);
            if (request.Inputs.TryGetValue(request.DesignJson, out var design) && design.Kind == "object"
                && design.Properties.TryGetValue("items", out var items) && items.Kind == "array")
                foreach (var item in items.Items)
                    if (item.Kind == "object" && item.Properties.TryGetValue("type", out var type) && type.Kind == "string"
                        && UnifiedHmiCapabilities.Unsupported(ReleaseKey, request.Operation, request.Action, type.Text) is string missingType)
                        throw new NotSupportedException(missingType);
            var session = new HmiSession(this, EngineeringPlcSession, new HmiRequest { Operation = request.Operation, DryRun = dryRun });
            var reply = new HmiReply();
            var previous = HmiValues.Renderer;
            var previousParse = UnifiedHmiAccess.Inputs;
            var previousToken = HmiExactAccess.ContentToken;
            var previousTrace = HmiValues.TraceWriter;
            var previousExportFailure = HmiValues.ExportFailure;
            HmiValues.Renderer = EngineeringPlcSession.SerializeEvidence;
            UnifiedHmiAccess.Inputs = request.Inputs;
            HmiExactAccess.ContentToken = UnifiedHmiToken;
            HmiValues.TraceWriter = WriteEngineeringHmiTrace;
            HmiValues.ExportFailure = EngineeringPlcSession.RecordExportFailure;
            try
            {
#if HMI_UNIFIED
                switch (request.Operation)
                {
                    case "SetUnifiedHmiRuntimeState": reply.Value = new UnifiedHmiAdapter(session).EnsureStartStopUnifiedHmi(request.HmiSoftwarePath, request.ScreenName, request.TagTableName, request.PlcName, request.ConnectionName); break;
                    case "EnsureUnifiedHmiScreen": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiScreen(request.HmiSoftwarePath, request.ScreenName, request.Width, request.Height); break;
                    case "EnsureUnifiedHmiTagTable": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiTagTable(request.HmiSoftwarePath, request.TagTableName); break;
                    case "EnsureUnifiedHmiTag": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiTag(request.HmiSoftwarePath, request.TagTableName, request.TagName, request.HmiDataType, request.PlcName, request.PlcTag, request.ConnectionName, request.Address, request.RequireVerifiedBinding); break;
                    case "EnsureUnifiedHmiConnection": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiConnection(request.HmiSoftwarePath, request.ConnectionName, request.PlcName); break;
                    case "EnsureUnifiedHmiScreenItem": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiScreenItem(request.HmiSoftwarePath, request.ScreenName, request.ItemName, request.ItemType, request.Left, request.Top, request.Width, request.Height, request.Text); break;
                    case "GetUnifiedHmiTexts": reply.Value = new UnifiedHmiAdapter(session).ReadUnifiedHmiTexts(request.HmiSoftwarePath, request.ScreenName, request.ItemName, request.TextProperty); break;
                    case "ApplyUnifiedHmiScreenDesign": reply.Value = new UnifiedHmiAdapter(session).ApplyUnifiedHmiScreenDesignJson(request.HmiSoftwarePath, request.ScreenName, request.DesignJson, request.Strict); break;
                    case "BindUnifiedHmiButtonPressedTag": reply.Value = new UnifiedHmiAdapter(session).BindUnifiedHmiButtonPressedTag(request.HmiSoftwarePath, request.ScreenName, request.ButtonName, request.TagName); break;
                    case "ListUnifiedHmiApiTypes": reply.Value = new UnifiedHmiAdapter(session).ListUnifiedHmiApiTypes(request.NameContains, request.Limit); break;
                    case "EnsureUnifiedHmiButtonEventHandler": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiButtonEventHandler(request.HmiSoftwarePath, request.ScreenName, request.ButtonName, request.EventType); break;
                    case "DescribeUnifiedHmiButtonEventScript": reply.Value = new UnifiedHmiAdapter(session).DescribeUnifiedHmiButtonEventScript(request.HmiSoftwarePath, request.ScreenName, request.ButtonName, request.EventType, request.MaxMembers); break;
                    case "SetUnifiedHmiButtonEventScriptCode": reply.Value = new UnifiedHmiAdapter(session).SetUnifiedHmiButtonEventScriptCode(request.HmiSoftwarePath, request.ScreenName, request.ButtonName, request.EventType, request.ScriptCode, request.GlobalDefinitionAreaScriptCode, request.Async, request.SyntaxCheck); break;
                    case "EnsureUnifiedHmiDynamization": reply.Value = new UnifiedHmiAdapter(session).EnsureUnifiedHmiDynamization(request.HmiSoftwarePath, request.ScreenName, request.ItemName, request.PropertyName, request.DynamizationType); break;
                    case "BindUnifiedHmiTagDynamization": reply.Value = new UnifiedHmiAdapter(session).BindUnifiedHmiTagDynamization(request.HmiSoftwarePath, request.ScreenName, request.ItemName, request.PropertyName, request.TagName, request.DataType, request.PlcTag, request.Address); break;
                    case "DescribeUnifiedScreenItemType": reply.Value = new UnifiedScreenItemsAdapter(session).DescribeUnifiedScreenItemType(request.ItemType, request.Depth); break;
                    case "ManageUnifiedScreenItem": reply.Value = new UnifiedScreenItemsAdapter(session).ManageUnifiedScreenItem(request.SoftwarePath, request.ScreenPath, request.Action, request.ItemName, request.ItemType, request.PropertiesJson, request.Depth, request.ConfirmDelete, request.Offset, request.Limit, request.DryRun, request.ContainedType); break;
                    case "ManageUnifiedHmiGroup": reply.Value = new UnifiedHmiGroupsAdapter(session).ManageUnifiedHmiGroup(request.SoftwarePath, request.Family, request.GroupPath, request.Action, request.NewName, request.DryRun); break;
                    case "GetUnifiedHmiButtonEvent": reply.Value = new UnifiedHmiInspectionAdapter(session).ReadUnifiedHmiButtonEvent(request.SoftwarePath, request.ScreenPath, request.ButtonName, request.EventType); break;
                    case "DeleteUnifiedHmiButtonEvent": reply.Value = new UnifiedHmiInspectionAdapter(session).DeleteUnifiedHmiButtonEvent(request.SoftwarePath, request.ScreenPath, request.ButtonName, request.EventType, request.DryRun, request.ExpectedToken, request.OnlyIfEmpty); break;
                    case "GetUnifiedHmiDynamization": reply.Value = new UnifiedHmiInspectionAdapter(session).ReadUnifiedHmiDynamization(request.SoftwarePath, request.ScreenPath, request.ItemName, request.PropertyName); break;
                    case "DeleteUnifiedHmiDynamization": reply.Value = new UnifiedHmiInspectionAdapter(session).DeleteUnifiedHmiDynamization(request.SoftwarePath, request.ScreenPath, request.ItemName, request.PropertyName, request.DryRun, request.ExpectedToken); break;
                    case "DeleteEmptyUnifiedHmiScreenGroup": reply.Value = new UnifiedHmiInspectionAdapter(session).DeleteEmptyUnifiedHmiScreenGroup(request.SoftwarePath, request.GroupPath, request.DryRun); break;
                    default: throw new ArgumentException("Unknown Unified HMI operation.");
                }
#else
                throw new NotSupportedException("WinCC Unified HMI API requires V19 or later.");
#endif
            }
            catch (Exception error)
            {
                var cause = error.GetBaseException();
                reply.Failure = new PlcSoftwareFailure { Code = cause is PlcSoftwareException native ? native.Code : cause.GetType().Name, Message = cause.Message };
            }
            finally
            {
                HmiValues.Renderer = previous;
                UnifiedHmiAccess.Inputs = previousParse;
                HmiExactAccess.ContentToken = previousToken;
                HmiValues.TraceWriter = previousTrace;
                HmiValues.ExportFailure = previousExportFailure;
            }
            reply.MayHaveChanged = session.MayHaveChanged;
            if (reply.Value is HmiMessage message) reply.RequiresSessionReset = message.Meta.TryGetValue("requiresExplicitRebind", out var reset) && reset is true;
            return reply;
        }
    }
}
