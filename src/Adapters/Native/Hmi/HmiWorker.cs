using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<object?>? HmiBindingIdentity { get; set; }
        internal static object DefaultHmiSoftware(IPlcOrganisationSession session, string path)
            => session.GetSoftwareContainer(path)?.Software ?? throw new PlcSoftwareException("NotFound", "HMI software not found: " + path);
        internal object HmiLibraryFolder(object library, string path, string root)
        {
            object folder = PlcGroupOperations.Get(library, root);
            foreach (var part in PlcGroupOperations.Parts(path, true))
                folder = PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Folders"), part)
                    ?? throw new PlcSoftwareException("NotFound", "Library folder not found: " + path);
            return folder;
        }
        public HmiReply ExecuteHmi(HmiRequest request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting HMI preview mode.");
            var unavailable = HmiCapabilities.Unsupported(ReleaseKey, request.Operation);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var session = new HmiSession(this, EngineeringPlcSession, request);
            var reply = new HmiReply();
            var previous = HmiValues.Renderer;
            var previousExportFailure = HmiValues.ExportFailure;
            var previousTraceWriter = HmiValues.TraceWriter;
            HmiValues.Renderer = EngineeringPlcSession.SerializeEvidence;
            HmiValues.ExportFailure = EngineeringPlcSession.RecordExportFailure;
            HmiValues.TraceWriter = WriteEngineeringHmiTrace;
            try
            {
                switch (request.Operation)
                {
                    case "GetHmiProgramInfo": reply.Value = new HmiDescribeAdapter(session).GetHmiProgramInfo(request.SoftwarePath); break;
                    case "DescribeHmiSoftware": reply.Value = new HmiDescribeAdapter(session).DescribeHmiSoftware(request.SoftwarePath, request.MaxMembers); break;
                    case "DescribeHmiScreen": reply.Value = new HmiDescribeAdapter(session).DescribeHmiScreen(request.SoftwarePath, request.ScreenName, request.MaxMembers); break;
                    case "DescribeHmiTagTable": reply.Value = new HmiDescribeAdapter(session).DescribeHmiTagTable(request.SoftwarePath, request.TagTableName, request.MaxMembers); break;
                    case "DescribeHmiTag": reply.Value = new HmiDescribeAdapter(session).DescribeHmiTag(request.SoftwarePath, request.TagTableName, request.TagName, request.MaxMembers); break;
                    case "DescribeHmiScreenItem": reply.Value = new HmiDescribeAdapter(session).DescribeHmiScreenItem(request.SoftwarePath, request.ScreenName, request.ItemName, request.MaxMembers); break;
                    case "ListHmiScreens": reply.Value = new HmiExchangeAdapter(session).GetHmiScreens(request.SoftwarePath); break;
                    case "ListHmiTagTables": reply.Value = new HmiExchangeAdapter(session).GetHmiTagTables(request.SoftwarePath); break;
                    case "ListHmiTags": reply.Value = new HmiExchangeAdapter(session).GetHmiTags(request.SoftwarePath, request.TagTableName); break;
                    case "ListHmiConnections": reply.Value = new HmiExchangeAdapter(session).GetHmiConnections(request.SoftwarePath); break;
                    case "ExportHmiScreen": reply.Value = new HmiExchangeAdapter(session).ExportHmiScreen(request.SoftwarePath, request.ScreenName, request.ExportPath); break;
                    case "ExportHmiTagTable": reply.Value = new HmiExchangeAdapter(session).ExportHmiTagTable(request.SoftwarePath, request.TagTableName, request.ExportPath); break;
                    case "ExportHmiConnection": reply.Value = new HmiExchangeAdapter(session).ExportHmiConnection(request.SoftwarePath, request.ConnectionName, request.ExportPath); break;
                    case "ExportHmiProgram": reply.Value = new HmiExchangeAdapter(session).ExportHmiProgram(request.SoftwarePath, request.ExportDir, request.ExportScreens, request.ExportTagTables); break;
                    case "ImportHmiScreen": new HmiExchangeAdapter(session).ImportHmiScreen(request.SoftwarePath, request.FolderPath, request.ImportPath); break;
                    case "ImportHmiTagTable": new HmiExchangeAdapter(session).ImportHmiTagTable(request.SoftwarePath, request.FolderPath, request.ImportPath); break;
                    case "ImportHmiConnection": new HmiExchangeAdapter(session).ImportHmiConnection(request.SoftwarePath, request.ImportPath); break;
                    case "ImportHmiScreensFromDirectory": reply.Value = new HmiExchangeAdapter(session).ImportHmiScreensFromDirectory(request.SoftwarePath, request.FolderPath, request.Dir, request.RegexName, request.Overwrite); break;
                    case "ImportHmiTagTablesFromDirectory": reply.Value = new HmiExchangeAdapter(session).ImportHmiTagTablesFromDirectory(request.SoftwarePath, request.FolderPath, request.Dir, request.RegexName, request.Overwrite); break;
                    case "GetHmiScreenSnapshot": reply.Value = new HmiInspectionPortAdapter(session).ReadHmiScreenSnapshot(request.SoftwarePath, request.ScreenPath, request.MaxDepth, request.MaxNodes); break;
                    case "ListHmiScreenPaths": reply.Value = new HmiInspectionPortAdapter(session).ListHmiScreenPaths(request.SoftwarePath, request.Offset, request.Limit); break;
                    case "DeleteHmiTag": reply.Value = new HmiTagDeletionAdapter(session).DeleteHmiTag(request.SoftwarePath, request.TagTablePath, request.TagName, request.DryRun, request.ConfirmDelete); break;
                    case "GetClassicHmiScreenTree": reply.Value = new ClassicHmiFoldersAdapter(session).ReadClassicHmiScreenTree(request.SoftwarePath, request.Kind, request.FolderPath, request.MaxDepth); break;
                    case "ManageClassicHmiScreenObject": reply.Value = new ClassicHmiFoldersAdapter(session).ManageClassicHmiScreenObject(request.SoftwarePath, request.ObjectKind, request.ObjectPath, request.Action, request.FilePath, request.ImportOptions, request.ConfirmDelete, request.DryRun); break;
                    case "ManageClassicHmiFolder": reply.Value = new ClassicHmiFoldersAdapter(session).ManageClassicHmiFolder(request.SoftwarePath, request.FolderKind, request.FolderPath, request.Action, request.NewName, request.ConfirmDelete, request.DryRun); break;
                    case "ManageClassicHmiGraphic": reply.Value = new ClassicHmiFoldersAdapter(session).ManageClassicHmiGraphic(request.SoftwarePath, request.Action, request.Name, request.FilePath, request.ImportOptions, request.ConfirmDelete, request.DryRun); break;
                    case "ListClassicHmiScripts": reply.Value = new MotionProDiagClassicHmiAdapter(session).ReadClassicHmiScripts(request.SoftwarePath, request.FolderPath, request.Offset, request.Limit); break;
                    case "ManageClassicHmiScript": reply.Value = new MotionProDiagClassicHmiAdapter(session).ManageClassicHmiScript(request.SoftwarePath, request.ScriptPath, request.Action, request.FilePath, request.ImportOptions, request.AttributesJson, request.ConfirmDelete, request.DryRun); break;
                    case "ManageClassicHmiCycle": reply.Value = new MotionProDiagClassicHmiAdapter(session).ManageClassicHmiCycle(request.SoftwarePath, request.Action, request.CycleName, request.FilePath, request.ImportOptions, request.AttributesJson, request.Offset, request.Limit, request.ConfirmDelete, request.DryRun); break;
                    case "ManageClassicHmiTextGraphicList": reply.Value = new MotionProDiagClassicHmiAdapter(session).ManageClassicHmiTextGraphicList(request.SoftwarePath, request.ListKind, request.Action, request.ListName, request.CompositionName, request.EntryName, request.TypeName, request.FilePath, request.ImportOptions, request.AttributesJson, request.Offset, request.Limit, request.ConfirmDelete, request.DryRun); break;
                    case "GetClassicHmiGlobalization": reply.Value = new MotionProDiagClassicHmiAdapter(session).ReadClassicHmiGlobalization(request.SoftwarePath, request.Offset, request.Limit); break;
                    case "ListClassicHmiFaceplates": reply.Value = new MotionProDiagClassicHmiAdapter(session).ReadClassicHmiFaceplates(request.Kind, request.LibraryName, request.FolderPath, request.Offset, request.Limit); break;
                    default: throw new ArgumentException("Unregistered HMI operation: " + request.Operation);
                }
                reply.MayHaveChanged = session.MayHaveChanged;
                if (reply.Value is HmiMessage message)
                    reply.RequiresSessionReset = message.Meta.TryGetValue("requiresExplicitRebind", out var blocked) && blocked is true
                        || session.MayHaveChanged && message.Meta.TryGetValue("operationSuccess", out var success) && success is false;
                if (reply.Value is HmiImportBatch batch && session.MayHaveChanged && batch.Failed.Count != 0)
                    reply.RequiresSessionReset = true;
            }
            catch (Exception error) when (error.GetBaseException() is not Siemens.Engineering.NonRecoverableException)
            {
                reply.Failure = EngineeringReflectionFailure != null ? EngineeringReflectionFailure(error)
                    : new PlcSoftwareFailure { Code = error is PlcSoftwareException native ? native.Code : error.GetType().Name, Message = error.Message };
                reply.MayHaveChanged = session.MayHaveChanged; reply.RequiresSessionReset = session.MayHaveChanged;
            }
            finally { HmiValues.Renderer = previous; HmiValues.ExportFailure = previousExportFailure; HmiValues.TraceWriter = previousTraceWriter; }
            return reply;
        }
    }
}
