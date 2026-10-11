using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
        private readonly DocumentExchange exchange = new DocumentExchange();
        private sealed class DocumentExchange
        {
            internal readonly List<PlcB5DocumentEvent> Events = new List<PlcB5DocumentEvent>();
            internal void StartWrite(string stage) => Events.Add(new PlcB5DocumentEvent { Kind = "start", Stage = stage });
            internal void Changed() => Events.Add(new PlcB5DocumentEvent { Kind = "changed" });
            internal void ConfirmWrite() => Events.Add(new PlcB5DocumentEvent { Kind = "confirm" });
            internal void Observe(string key, object? value) => Events.Add(new PlcB5DocumentEvent { Kind = "field", Stage = key, Value = value });
            internal void ObserveNativeResult(string? state, bool changesProject, object? messages, string directory, string name)
                => Events.Add(new PlcB5DocumentEvent { Kind = "native", State = state, ChangesProject = changesProject, Value = messages, Directory = directory, Name = name });
        }
#if PLC_DOCUMENT_EXPORT
        private static object? AttributeValue(object? value)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid || type.IsEnum) return type.IsEnum ? Convert.ToInt32(value) : value;
            try { return value.ToString(); } catch { /* swallow(probe-optional): Preserve the original nonserializable attribute placeholder. */ return "<" + type.Name + ">"; }
        }
        private static PlcB5Block BlockRow(PlcBlock block)
        {
            var attributes = new List<PlcB5Attribute>();
            foreach (var info in ((IEngineeringObject)block).GetAttributeInfos())
            {
                object? value;
                try { value = ((IEngineeringObject)block).GetAttribute(info.Name); }
                catch (Exception error) { value = "<unreadable: " + error.GetType().Name + ">"; /* swallow(probe-optional): Preserve the per-attribute snapshot probe. */ }
                attributes.Add(new PlcB5Attribute { Name = info.Name, Value = AttributeValue(value), AccessMode = info.AccessMode.ToString() });
            }
            return new PlcB5Block { Name = block.Name, TypeName = block.GetType().Name, Namespace = block.Namespace,
                ProgrammingLanguage = Enum.GetName(typeof(Siemens.Engineering.SW.Blocks.ProgrammingLanguage), block.ProgrammingLanguage), MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                IsConsistent = block.IsConsistent, HeaderName = block.HeaderName, ModifiedDate = block.ModifiedDate,
                IsKnowHowProtected = block.IsKnowHowProtected, Attributes = attributes, Description = block.ToString() };
        }
        private bool AutoOffline(Func<bool> operation)
        {
            if (engine.PlcB5AutoOffline != null) return engine.PlcB5AutoOffline(request.SoftwarePath, operation);
            try { return operation(); }
            catch (Exception error) when (OnlineModeError(error))
            {
                try { GoOfflineForDocuments(); } catch { /* swallow(native-fallback): Preserve the existing one retry and its failure. */ }
                return operation();
            }
        }
        private static bool OnlineModeError(Exception error)
        {
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
            {
                string message = cause.Message ?? string.Empty;
                if (message.IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    message.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        private void GoOfflineForDocuments()
        {
            if (_session.IsProjectNull()) return;
            foreach (var plc in AllDocumentPlcs())
            {
                var name = plc.Name;
                try
                {
                    var provider = _session.ResolvePlcService<Siemens.Engineering.Online.OnlineProvider>(plc.Name, plc);
                    var before = provider?.State.ToString() ?? "Unknown";
                    provider?.GoOffline();
                    var after = provider?.State.ToString() ?? "Unknown";
                }
                catch { /* swallow(native-fallback): Preserve per-PLC best effort before the single document retry. */ }
            }
        }
        private List<PlcSoftware> AllDocumentPlcs()
        {
            var result = new List<PlcSoftware>();
            var project = _session.CurrentProject;
            if (project == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(IEnumerable<Siemens.Engineering.HW.DeviceItem>? roots)
            {
                if (roots == null) return;
                var stack = new Stack<Siemens.Engineering.HW.DeviceItem>(roots.Where(x => x != null));
                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    if (item == null) continue;
                    try
                    {
                        if (item.GetService<Siemens.Engineering.HW.Features.SoftwareContainer>()?.Software is PlcSoftware plc &&
                            !string.IsNullOrEmpty(plc.Name) && seen.Add(plc.Name)) result.Add(plc);
                    }
                    catch { /* swallow(probe-optional): Continue discovering PLCs on other device items. */ }
                    try { if (item.DeviceItems != null) foreach (var child in item.DeviceItems) if (child != null) stack.Push(child); }
                    catch { /* swallow(enumerate-optional): Continue with readable device items. */ }
                }
            }
            void WalkDevices(Siemens.Engineering.HW.DeviceComposition? devices)
            {
                if (devices == null) return;
                foreach (var device in devices) { try { Collect(device.DeviceItems); } catch { /* swallow(enumerate-optional): Continue with sibling devices. */ } }
            }
            void WalkGroups(Siemens.Engineering.HW.DeviceUserGroupComposition? groups)
            {
                if (groups == null) return;
                foreach (var group in groups) { try { WalkDevices(group.Devices); WalkGroups(group.Groups); } catch { /* swallow(enumerate-optional): Continue with sibling groups. */ } }
            }
            try { WalkDevices(project.Devices); } catch { /* swallow(enumerate-optional): Still inspect grouped devices. */ }
            try { WalkGroups(project.DeviceGroups); } catch { /* swallow(enumerate-optional): Retain PLCs already discovered. */ }
            return result;
        }
        internal PlcB5Reply LegacyDocuments()
        {
            var reply = new PlcB5Reply();
            try
            {
                switch (request.Operation)
                {
                    case "ExportPlcBlockDocuments": reply.Value = AutoOffline(() => ExportAsDocuments(request.SoftwarePath, request.BlockPath, request.ExportPath, request.PreservePath)); break;
                    case "ExportPlcBlocksDocuments":
                        if (request.Action == "count") reply.Count = _session.GetBlocks(request.SoftwarePath, request.RegexName)?.Count;
                        else reply.Blocks = ExportBlocksAsDocuments(request.SoftwarePath, request.ExportPath, request.RegexName, request.PreservePath)?.Select(BlockRow).ToArray();
                        break;
                    case "ImportPlcBlockDocuments":
                        if (request.Action == "verify") { LastImportedDocumentBlocks = request.ImportedNames; reply.Value = VerifyLastDocumentImport(request.SoftwarePath, request.GroupPath); }
                        else reply.Value = AutoOffline(() => ImportFromDocuments(request.SoftwarePath, request.GroupPath, request.ImportPath, request.FileNameWithoutExtension, (Siemens.Engineering.SW.ImportDocumentOptions)Enum.Parse(typeof(Siemens.Engineering.SW.ImportDocumentOptions), request.ImportOption)));
                        break;
                    case "ImportPlcBlocksDocuments": reply.Blocks = ImportBlocksFromDocuments(request.SoftwarePath, request.GroupPath, request.ImportPath, request.RegexName, (Siemens.Engineering.SW.ImportDocumentOptions)Enum.Parse(typeof(Siemens.Engineering.SW.ImportDocumentOptions), request.ImportOption))?.Select(BlockRow).ToArray(); break;
                }
            }
            catch (Exception error)
            {
                reply.Failure = new PlcSoftwareFailure { Code = error is PlcSoftwareException failure ? failure.Code : error.GetType().Name, Message = error.Message };
            }
            reply.Events = exchange.Events; reply.ExportFailures = LastExportAsDocumentsFailures.ToArray(); reply.ImportFailures = LastImportFromDocumentsFailures.ToArray();
            reply.ImportedNames = LastImportedDocumentBlocks; reply.Scanned = LastImportFromDocumentsScanned; reply.Selected = LastImportFromDocumentsSelected;
            reply.Attempted = LastImportFromDocumentsAttempted; reply.Succeeded = LastImportFromDocumentsSucceeded; reply.Stopped = LastImportFromDocumentsStopped;
            reply.MayHaveChanged = exchange.Events.Any(e => e.Kind == "start"); reply.RequiresSessionReset = reply.MayHaveChanged && (reply.Failure != null || reply.Stopped);
            return reply;
        }
#endif
    }
}
