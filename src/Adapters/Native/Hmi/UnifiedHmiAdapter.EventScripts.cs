#if HMI_UNIFIED
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using static TiaMcp.Adapters.Native.Hmi.UnifiedHmiAccess;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed partial class UnifiedHmiAdapter
    {
        public HmiDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath, string screenName, string buttonName, string eventType, int maxMembers =
                200)
        {
            try
            {
                if (_session.IsProjectNull())
                {
                    throw new PlcSoftwareException("InvalidState", "Project is null");
                }

                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType, createIfMissing: false);
                var scriptProp = handler.GetType().GetProperty("Script", BindingFlags.Public | BindingFlags.Instance);
                var script = scriptProp?.GetValue(handler);

                var members = new List<OpennessMember>
                {
                    new OpennessMember
                    {
                        Name = "HandlerType",
                        Kind = "Info",
                        Type = handler.GetType().FullName ?? handler.GetType().Name,
                        Signature = null
                    },
                    new OpennessMember
                    {
                        Name = "ScriptProperty",
                        Kind = "Info",
                        Type = scriptProp == null ? "missing"
                            : $"{scriptProp.PropertyType.FullName}; CanRead={scriptProp.CanRead}; CanWrite={scriptProp.CanWrite}",
                        Signature = null
                    },
                    new OpennessMember
                    {
                        Name = "ScriptValue",
                        Kind = "Info",
                        Type = script == null ? "null" : (script.GetType().FullName ?? script.GetType().Name),
                        Signature = null
                    }
                };

                if (script != null)
                {
                    members.AddRange(_session.DescribeMembers(script, MemberListCap.Effective(maxMembers)));
                    try
                    {
                        var infos = script.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(script, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new OpennessMember
                                {
                                    Name = $"AttributeInfo:{TryGetPropertyValue(info, "Name") ?? info}",
                                    Kind = "AttributeInfo",
                                    Type = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                                    Signature = info.ToString()
                                });
                            }
                        }
                    }
                    catch /* swallow(probe-optional): Unavailable attribute metadata leaves the other script member descriptions intact. */ { }
                }
                else
                {
                    members.AddRange(_session.DescribeMembers(handler, MemberListCap.Effective(maxMembers)));
                    try
                    {
                        var infos = handler.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(handler, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new OpennessMember
                                {
                                    Name = $"HandlerAttributeInfo:{TryGetPropertyValue(info, "Name") ?? info}",
                                    Kind = "AttributeInfo",
                                    Type = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                                    Signature = info.ToString()
                                });
                            }
                        }
                    }
                    catch /* swallow(probe-optional): Unavailable invocation metadata leaves the other script member descriptions intact. */ { }
                }

                return new HmiDescribe
                {
                    Message = "OK",
                    Meta = UnifiedHmiAccess.Unstamped(true, ("operationSuccess", true), ("exists", true),
                        ("totalMembers", members.Count), ("truncated", members.Count > MemberListCap.Effective(maxMembers))),
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    TypeName = script == null ? scriptProp?.PropertyType.FullName : script.GetType().FullName,
                    Members = members.Take(MemberListCap.Effective(maxMembers)).ToList()
                };
            }
            catch (Exception ex)
            {
                return new HmiDescribe
                {
                    Message = "Event script read failed",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new HmiObject { ["success"] = false, ["operationSuccess"] = false,
                        ["status"] = ex is PlcSoftwareException pex ? pex.Code : "ReadFailed",
                        ["error"] = (ex.InnerException ?? ex).Message },
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    Members = Array.Empty<OpennessMember>()
                };
            }
        }

        public HmiMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath, string screenName, string buttonName, string eventType, string scriptCode,
                string? globalDefinitionAreaScriptCode = null, bool? async = null, bool syntaxCheck = false)
        {
            return _session.RunHmiStepTool("SetUnifiedHmiButtonEventScriptCode", meta => _session.Capture(meta, _ =>
            {
                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType);
                var script = TryGetPropertyValue(handler, "Script");
                if (script == null)
                {
                    throw new InvalidOperationException($"Script object is null on '{buttonName}.{eventType}'. Ensure the event handler exists first.");
                }

                HmiExchangePolicy.UpdateScript(script, scriptCode ?? string.Empty, globalDefinitionAreaScriptCode, async,
                    () => meta["mayHaveChanged"] = true);
                meta["scriptType"] = script.GetType().FullName;
                meta["setScriptCode"] = true;
                meta["setGlobalDefinitionAreaScriptCode"] = globalDefinitionAreaScriptCode != null ? true : (bool?)null;
                meta["setAsync"] = async.HasValue ? true : (bool?)null;

                // SyntaxCheck 默认不跑（issue #36）：TIA V21 上对 Unified 的 Script 对象调
                // SyntaxCheck() 会偶发抛 NonRecoverableException 并带走整个 Portal 进程，
                // 脚本已写进内存却随进程一起丢掉。检查是可选的增值动作，不该让「写脚本」
                // 这件必须成功的事去赌它。需要证据的调用方显式传 syntaxCheck: true。
                meta["syntaxCheckRequested"] = syntaxCheck;
                if (!syntaxCheck)
                {
                    // 不发 syntaxErrorCount：缺席必须读成「没查」，而不是「查了 0 个错」。
                    meta["syntaxCheckStatus"] = "skipped";
                    meta["syntaxCheckSkippedReason"] =
                        "SyntaxCheck was not run (default). On TIA V21 it can crash the Portal process " +
                        "(NonRecoverableException) and take the just-written ScriptCode with it. " +
                        "Pass syntaxCheck=true only when you need the evidence and can afford the risk.";
                }
                else
                {
                    object? syntaxResult = null;
                    try
                    {
                        syntaxResult = script.GetType().GetMethod("SyntaxCheck", Type.EmptyTypes)?.Invoke(script, Array.Empty<object>());
                        if (syntaxResult != null)
                        {
                            var syntaxErrors = TryGetEnumerableStrings(syntaxResult, "Errors").ToList();
                            var syntaxWarnings = TryGetEnumerableStrings(syntaxResult, "Warnings").ToList();
                            meta["syntaxCheckStatus"] = "ran";
                            meta["syntaxResultType"] = syntaxResult.GetType().FullName;
                            meta["syntaxResult"] = syntaxResult.ToString();
                            meta["syntaxErrors"] = _session.ToHmiArray(syntaxErrors);
                            meta["syntaxWarnings"] = _session.ToHmiArray(syntaxWarnings);
                            meta["syntaxErrorCount"] = syntaxErrors.Count;
                            meta["syntaxWarningCount"] = syntaxWarnings.Count;
                            meta["syntaxPropertyName"] = TryGetPropertyValue(syntaxResult, "PropertyName")?.ToString() ?? string.Empty;
                            meta["syntaxMembers"] = string.Join(" | ", _session.DescribeMembers(syntaxResult, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        }
                        else
                        {
                            // 这个 Script 类型上根本没有 SyntaxCheck 方法，同样不是「0 个错」。
                            meta["syntaxCheckStatus"] = "unavailable";
                            meta["syntaxCheckSkippedReason"] = $"No SyntaxCheck() method on {script.GetType().FullName}.";
                        }
                    }
                    catch (Exception ex)
                    {
                        var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        meta["syntaxCheckStatus"] = "faulted";
                        meta["syntaxError"] = $"{real.GetType().FullName}: {real.Message}";

                        // NonRecoverableException 不是「这一步没做成」，是 Portal 进程已经没了。
                        // 刚写进去的 ScriptCode 没保存就随进程消失，这时候再返回 Success 是在撒谎。
                        if (TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(real))
                        {
                            throw new InvalidOperationException(
                                "SyntaxCheck killed the TIA Portal process (" + real.GetType().Name + "). " +
                                "The ScriptCode was written in memory but is NOT saved - the whole session is gone. " +
                                "Reconnect, re-apply the script with syntaxCheck=false, and save. This is issue #36.", real);
                        }
                    }
                }

                return syntaxCheck
                    ? $"ScriptCode set for '{buttonName}.{eventType}'."
                    : $"ScriptCode set for '{buttonName}.{eventType}' (SyntaxCheck skipped by default; see syntaxCheckSkippedReason).";
            }));
        }

        private object ResolveHmiButtonEventHandlerOrThrow(string hmiSoftwarePath, string screenName, string buttonName, string eventType,
                bool createIfMissing = true)
        {
            var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
            var handlers = button.GetType().GetProperty("EventHandlers")?.GetValue(button)
                ?? throw new PlcSoftwareException("NotFound", "EventHandlers not found on " + buttonName);
            if (!createIfMissing) return HmiEventAccess.Resolve(handlers, eventType, false);
            try { return HmiEventAccess.Resolve(handlers, eventType, false); }
            catch (PlcSoftwareException error) when (error.Code == "NotFound")
            {
                _session.MarkHmiChanged();
                return HmiEventAccess.Resolve(handlers, eventType, true);
            }
        }

    }
}
#endif
