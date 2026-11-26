using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Engineering;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static string? HardwareSiemensReason(Exception error)
        {
            if (error is not Siemens.Engineering.EngineeringException native) return null;
            RecordSiemensReason(native);
            return native.Data["nativeMessage"] as string;
        }

        public HardwareAddressingReply HardwareLegacySetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
        {
            // envelope: legacy-multiple-dynamic-fields
            var meta = HardwareStepMeta(DateTime.Now, false);
            foreach (var pair in new Dictionary<string, object?>
            {
                ["deviceItemPath"] = deviceItemPath,
                ["attributeName"] = attributeName,
                ["mayHaveChanged"] = false
            }) meta[pair.Key] = pair.Value;

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["error"] = "Project is null";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }

                var di = HardwareResolveItem(deviceItemPath);
                if (di == null)
                {
                    meta["error"] = "Device item not found";
                    return new HardwareAddressingReply { Message = "Device item not found", Meta = meta };
                }

                var info = di.GetAttributeInfos().FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    meta["error"] = "Attribute not found";
                    meta["availableAttributes"] =
                        HardwareToArray(di.GetAttributeInfos().Select(x => x.Name ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return new HardwareAddressingReply { Message = "Attribute not found", Meta = meta };
                }

                object? oldValue = null;
                bool hadOldValue = false;
                try { oldValue = di.GetAttribute(info.Name); hadOldValue = true; } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["oldValue"] = oldValue?.ToString() ?? string.Empty;
                meta["attributeDataType"] = HardwareAttributeTypes(info);
                meta["attributeAccessMode"] = info.AccessMode.ToString();
                meta["attributeWritable"] = HardwareAttributeWritable(info);

                // EngineeringAttributeInfo.AccessMode decides writability; a read-only attribute is never written.
                if (!HardwareAttributeWritable(info))
                {
                    meta["refusalReason"] = "Attribute '" + info.Name + "' is not writable (AccessMode=" + info.AccessMode + "); nothing was written.";
                    return new HardwareAddressingReply { Message = $"Device item attribute '{attributeName}' is not writable", Meta = meta };
                }
                if (!HardwareTryCoerceAttributeValue(value, oldValue, info, out var typedValue, out var typeProblem))
                {
                    meta["refusalReason"] = typeProblem + " Nothing was written.";
                    return new HardwareAddressingReply { Message = $"Device item attribute '{attributeName}' value type is unknown", Meta = meta };
                }
                meta["requestedValue"] = typedValue.ToString() ?? string.Empty;
                meta["mayHaveChanged"] = true;
                meta["writeOutcomeUnknown"] = true;
                try { di.SetAttribute(info.Name, typedValue); }
                catch (Exception error)
                {
                    if (HardwareAddressingPolicy.ScalarUnchanged(error, hadOldValue, oldValue, () => di.GetAttribute(info.Name)))
                    {
                        meta["mayHaveChanged"] = false;
                        meta["writeOutcomeUnknown"] = false;
                        meta["apiCallSuccess"] = false;
                    }
                    throw;
                }

                object? newValue = null;
                try { newValue = di.GetAttribute(info.Name); meta["writeOutcomeUnknown"] = false; } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["newValue"] = newValue?.ToString() ?? string.Empty;
                // TIA may ignore or normalize a written value: success only when the read-back equals the request.
                bool matched = !Equals(meta["writeOutcomeUnknown"], true) && HardwareAddressingPolicy.SameValue(newValue, typedValue);
                if (!Equals(meta["writeOutcomeUnknown"], true)) { meta["postStateKnown"] = true; meta["valueMismatch"] = !matched; }
                // envelope: legacy-single-verdict
                meta["success"] = matched;
                return new HardwareAddressingReply {
                    Message = matched ? $"Device item attribute '{attributeName}' set and read back"
                        : $"Device item attribute '{attributeName}' read back a different value than requested", Meta = meta
                };
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                if (HardwareSiemensReason(ex) is string reason)
                {
                    if (string.Equals(attributeName, "Name", StringComparison.OrdinalIgnoreCase)
                        && meta.TryGetValue("requestedValue", out var requested) && requested is string name && string.IsNullOrWhiteSpace(name))
                    {
                        var inner = ex.InnerException?.Message;
                        reason = NativeFailureText.Compose(reason, string.IsNullOrWhiteSpace(inner)
                            ? "TIA refused the name without a reason. An empty name is not allowed." : inner, Array.Empty<string>());
                    }
                    meta["nativeMessage"] = reason;
                }
                meta["error"] = HardwareExceptionDetail(ex);
                return new HardwareAddressingReply { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        public HardwareAddressingReply HardwareLegacySetCpuCommonSettings(string cpuPath, Dictionary<string, HardwareScalar> settingsJson)
        {
            var meta = HardwareStepMeta(DateTime.Now, false); meta["cpuPath"] = cpuPath; meta["mayHaveChanged"] = false;
            bool verifiedMismatch = false;
            using var journal = new EngineeringWriteJournal();

            try
            {
                if (HardwareProjectMissing())
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };

                var di = HardwareResolveItem(cpuPath);
                if (di == null)
                {
                    meta["error"] = "CPU device item not found";
                    meta["status"] = "NotFound"; meta["parameter"] = "cpuPath";
                    meta["nativeMessage"] = "CPU device item not found";
                    return new HardwareAddressingReply { Message = "CPU device item not found", Meta = meta };
                }

                var exact = settingsJson;
                if (exact == null || exact.Count == 0)
                {
                    meta["error"] =
                        "settingsJson must contain exactAttributes. Attribute names must come from GetDeviceItemInfo/GetDeviceItemNetworkInfo readback.";
                    return new HardwareAddressingReply { Message = "No exact attributes supplied", Meta = meta };
                }

                var infos = di.GetAttributeInfos().ToList();
                EngineeringWriteJournal
                    .CaptureAttributes(di, exact.Keys
                        .Select(name => infos.FirstOrDefault(info => string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .Where(info => info != null && HardwareAttributeWritable(info)).Select(info => info!.Name), name => di.GetAttribute(name));
                var applied = new List<object?>();
                var rejected = new List<Dictionary<string, object?>>();
                meta["applied"] = applied; meta["rejected"] = rejected;

                foreach (var kv in exact)
                {
                    var name = kv.Key;
                    var value = kv.Value?.Text ?? string.Empty;
                    var info = infos.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        rejected.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = name,
                            ["reason"] = "Attribute not found on CPU device item. Read exact names first."
                        });
                        continue;
                    }

                    if (!HardwareAttributeWritable(info))
                    {
                        rejected.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = "Attribute is not writable according to TIA attribute metadata."
                        });
                        continue;
                    }

                    bool writeIssued = false;
                    bool readbackComplete = false;
                    object? oldValue = null;
                    bool hadOldValue = false;
                    try
                    {
                        try { oldValue = di.GetAttribute(info.Name); hadOldValue = true; } catch /* swallow(probe-optional): Optional attribute readback must not interrupt the per-attribute settings report. */ { }
                        if (!HardwareTryCoerceAttributeValue(value, oldValue, info, out var typedValue, out var typeProblem))
                        {
                            rejected.Add(new Dictionary<string, object?> { ["attribute"] = info.Name, ["reason"] = typeProblem });
                            continue;
                        }
                        EngineeringWriteJournal.Attribute(di, info.Name, () => di.GetAttribute(info.Name), previous => di.SetAttribute(info.Name, previous));
                        writeIssued = true; meta["mayHaveChanged"] = true;
                        di.SetAttribute(info.Name, typedValue);
                        object? newValue = null;
                        try { newValue = di.GetAttribute(info.Name); readbackComplete = true; } catch /* swallow(probe-optional): Optional attribute readback must not interrupt the per-attribute settings report. */ { }
                        if (!readbackComplete || !HardwareAddressingPolicy.SameValue(newValue, typedValue))
                        {
                            if (readbackComplete) meta["valueMismatch"] = true;
                            else { meta["writeOutcomeUnknown"] = true; meta["readbackComplete"] = false; }
                            rejected.Add(new Dictionary<string, object?> {
                                ["attribute"] = info.Name, ["writeIssued"] = true,
                                ["requestedValue"] = typedValue?.ToString(), ["newValue"] = newValue?.ToString(),
                                ["reason"] = readbackComplete ? "CPU attribute readback differs." : "CPU attribute readback unavailable."
                            });
                            continue;
                        }
                        applied.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = info.Name,
                            ["oldValue"] = oldValue?.ToString() ?? string.Empty,
                            ["newValue"] = newValue?.ToString() ?? string.Empty,
                            ["readbackComplete"] = readbackComplete,
                            ["dataType"] = HardwareAttributeTypes(info)
                        });
                    }
                    catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                    {
                        EngineeringWriteJournal.ObserveFailure(ex);
                        if (writeIssued && !HardwareAddressingPolicy.ScalarUnchanged(ex, hadOldValue, oldValue, () => di.GetAttribute(info.Name)))
                            meta["writeOutcomeUnknown"] = true;
                        string? reason = HardwareSiemensReason(ex);
                        rejected.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = reason ?? ex.GetType().Name,
                            ["writeIssued"] = writeIssued
                        });
                    }
                }

                meta["applied"] = applied;
                meta["rejected"] = rejected;
                if (rejected.Count > 0)
                    meta["nativeMessage"] = string.Join(" | ", rejected.Select(row => row["attribute"] + ": " + row["reason"]));
                meta["readback"] = HardwareBuildDeviceItemNetworkReadbackJson(cpuPath);
                verifiedMismatch = meta.TryGetValue("valueMismatch", out var mismatch) && mismatch is true;
                meta["success"] = applied.Count > 0 && rejected.Count == 0;
                return new HardwareAddressingReply
                {
                    Message = Equals(meta["success"], true)
                        ? "CPU common settings applied and read back"
                        : "CPU common settings completed with rejected attributes",
                    Meta = meta
                };
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                EngineeringWriteJournal.ObserveFailure(ex);
                if (HardwareSiemensReason(ex) is string reason) meta["nativeMessage"] = reason;
                meta["error"] = HardwareExceptionDetail(ex);
                return new HardwareAddressingReply { Message = "Failed setting CPU common settings", Meta = meta };
            }
            finally
            {
                if ((meta["success"] is true || verifiedMismatch) && !(meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true))
                {
                    if (verifiedMismatch) meta["postStateKnown"] = true;
                    journal.Commit();
                }
                else journal.Rollback(meta);
            }
        }
    }
}
