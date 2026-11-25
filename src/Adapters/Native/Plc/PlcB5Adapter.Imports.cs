using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using ResponseMeta = TiaMcp.Adapters.Native.Plc.PlcB5Meta;
#if PLC_ALARM_TEXTS
using Siemens.Engineering.SW.Alarm;
#endif
#if PLC_ALARM_LISTS
using Siemens.Engineering.SW.Alarm.TextLists;
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
        private static PlcB5Reply ImportFailure(string message, bool issued)
            => new PlcB5Reply { Message = message, MayHaveChanged = issued, OutcomeUnknown = issued,
                Meta = PlcB5Meta.Unstamped(false, ("apiCallSuccess", false), ("mayHaveChanged", issued), ("writeOutcomeUnknown", issued)) };

#if PLC_ALARM_TEXTS
#if PLC_ALARM_CLASSES
        public PlcB5Reply ImportAlarmClasses(string softwarePath, string importPath)
        {
            if (_session.IsProjectNull()) return new PlcB5Reply {
                Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new PlcB5Reply {
                Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta
                    .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
            };

            bool issued = false;
            try
            {
                var provider = plc.GetService<AlarmClassDataProvider>() ?? _session.CurrentProject?.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new PlcB5Reply {
                        Message = "AlarmClassDataProvider not available for this PLC or project.", Meta = ResponseMeta
                            .Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false))
                    };

                if (!File.Exists(importPath)) return new PlcB5Reply {
                    Message = "Alarm class import file not found.", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
                };
                if (!importPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    return new PlcB5Reply {
                        Message = "importPath must be an existing .DAT file written by ExportAlarmClasses (official AlarmClassDataProvider format); got '"
                            + importPath + "'.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "INVALID_ARGUMENT"), ("mayHaveChanged", false))
                    };
                var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                issued = true;
                AlarmClassExportImportResult result = provider.Import(input);
                var state = result?.State.ToString() ?? "Unknown";
                var errCount = result?.ErrorCount ?? 0;
                bool ok = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses, state);
                return new PlcB5Reply
                {
                    Message = ok
                        ? $"Alarm classes imported from '{importPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class import failed. State={state}, Errors={errCount}.",
                    Meta =
                        NativeXlsxEvidence(new PlcB5Object {
                            ["importPath"] = importPath, ["state"] = state, ["errorCount"] = errCount, ["warningCount"] = result?.WarningCount ?? 0,
                                ["messages"] = AlarmClassMessages(result)
                        }, state, true, null, importPath, TiaMcp.Adapters.Contracts.NativeResultStates.AlarmClasses)
                };
            }
            catch (Exception ex)
            {
                return ImportFailure($"Import failed: {ex.Message}", issued);
            }
        }
#endif

        internal PlcB5Reply ImportTextLists(string softwarePath, string importPath)
        {
            if (_session.IsProjectNull()) return new PlcB5Reply {
                Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new PlcB5Reply {
                Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta
                    .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
            };
            bool issued = false;
            try
            {
                var provider = plc.GetService<PlcAlarmTextListProvider>();
                if (provider == null) return new PlcB5Reply {
                    Message = "PlcAlarmTextListProvider service not available on this PLC.", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false))
                };
                if (!File.Exists(importPath)) return new PlcB5Reply {
                    Message = $"Import file not found: {importPath}", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
                };
                var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                issued = true;
                TextListXlsxResult result = provider.ImportFromXlsx(input, ImportOptions.None);
                var state = result?.State.ToString() ?? "Unknown";
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State);
                return new PlcB5Reply
                {
                    Message = ok ? $"Alarm text lists imported from '{importPath}' (State={state}); compile afterwards."
                        : $"Alarm text list import reported Error (State={state}, log {result?.LogFilePath?.FullName}).",
                    Meta =
                        NativeXlsxEvidence(new PlcB5Object {
                            ["importPath"] = importPath, ["state"] = state, ["logFile"] = result?.LogFilePath?.FullName, ["success"] = ok }, state, true, result
                            ?.LogFilePath?.FullName, importPath, TiaMcp.Adapters.Contracts.NativeResultStates.TextLists)
                };
            }
            catch (Exception ex)
            {
                return ImportFailure($"Import failed: {ex.GetBaseException().Message}", issued);
            }
        }
#endif

#if PLC_OPC_UA
        public PlcB5Reply ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (_session.IsProjectNull()) return new PlcB5Reply {
                Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.ResolvePlc(softwarePath, true);
            if (plc == null) return new PlcB5Reply {
                Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta
                    .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
            };

            bool issued = false;
            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new PlcB5Reply {
                    Message = "ServerInterfaceGroup not accessible.", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false))
                };

                string collectionProp = interfaceType switch
                {
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = PlcB5Scalar.TryProperty(sig, collectionProp);
                if (collection == null) return new PlcB5Reply {
                    Message = $"{collectionProp} collection not accessible.", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY"), ("mayHaveChanged", false))
                };

                // ServerInterfaceComposition.Create(name) then Import(file)
                // OR find existing and call Import
                // Check the file before creation, propagate Import failures and attempt to remove a newly created interface on failure.
                // Native observation: importing a missing file left an empty server interface when the exception was discarded.
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath));
                if (!fi.Exists) return new PlcB5Reply {
                    Message = $"Import file not found: {importPath}", Meta = ResponseMeta
                        .Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false))
                };
                var interfaceName = interfaceType == "ServerInterface" ? PlcOpcUaImportDocument.InterfaceName(fi) : Path
                    .GetFileNameWithoutExtension(importPath);
                string[]? FreshNames()
                {
                    var group = GetOpcUaServerInterfaceGroup(plc);
                    if (group == null || group.GetType().GetProperty(collectionProp)?.GetValue(group) is not IEnumerable items || items is string) return null;
                    var names = items.Cast<object>().Select(item => item?.GetType().GetProperty("Name")?.GetValue(item) as string).ToArray();
                    return names.Any(string.IsNullOrEmpty) ? null : names.OrderBy(name => name, StringComparer.Ordinal).ToArray()!;
                }
                var existing = FindByName(collection, interfaceName);

                object? target = existing; bool createdNow = false;
                if (target == null)
                {
                    var create = collection.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (create == null) return ImportFailure("Native interface Create(string) unavailable.", false);
                    var proof = new PlcB5Object();
                    try
                    {
                        NativeRefusalProbe.Run(proof, () => FreshNames(), () => {
                            issued = true;
                            target = create.Invoke(collection, new object[] { interfaceName });
                        });
                    }
                    catch (Exception error)
                    {
                        return ImportFailure(error.GetBaseException().Message, proof.TryGetValue("mayHaveChanged", out var changed) && changed is true);
                    }
                    if (target == null) return ImportFailure($"Could not confirm creation of {interfaceType} '{interfaceName}'.", issued);
                    createdNow = true;
                }
                var import = target.GetType().GetMethod("Import", new[] { typeof(FileInfo) });
                if (import == null) return ImportFailure($"Import(FileInfo) is not exposed by {target.GetType().Name}.", issued);
                issued = true;
                try { import.Invoke(target, new object[] { fi }); }
                catch (TargetInvocationException tie)
                {
                    bool removed = false;
                    if (createdNow)
                    {
                        try
                        {
                            target.GetType().GetMethod("Delete", Type.EmptyTypes)?.Invoke(target, null);
                        }
                        catch { /* swallow(cleanup): the fresh collection can still confirm removal after a native failure. */ }
                        try
                        {
                            removed = FreshNames() is { } names && !names.Contains(interfaceName, StringComparer.OrdinalIgnoreCase);
                        }
                        catch { /* swallow(cleanup): retain import uncertainty when removal or fresh name lookup fails */ }
                    }
                    return ImportFailure($"Import failed: {(tie.InnerException ?? tie).Message}"
                        + (removed ? $" (the new {interfaceType} '{interfaceName}' was removed again)" : ""), !removed);
                }
                return new PlcB5Reply { Message = createdNow
                    ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                    : $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'.", Meta = ResponseMeta.Unstamped(true, ("created", createdNow))
                    };
            }
            catch (Exception ex)
            {
                return ImportFailure($"Import failed: {ex.Message}", issued);
            }
        }

        private static object? FindByName(object? collection, string name)
        {
            if (collection is not IEnumerable items || collection is string) return null;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (string.Equals(PlcB5Scalar.TryProperty(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }
#endif
    }
}
