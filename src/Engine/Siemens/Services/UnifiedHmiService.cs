using UnifiedHmiPlcPartnerInfo = TiaMcpServer.Siemens.Portal.UnifiedHmiPlcPartnerInfo;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedHmiService
    {
        private readonly IEngineeringSession _session;

        public UnifiedHmiService(IEngineeringSession session) => _session = session;

        public ResponseMessage EnsureStartStopUnifiedHmi(
            string hmiSoftwarePath,
            string screenName = "Main",
            string tagTableName = "默认变量表",
            string plcName = "PLC_1",
            string connectionName = "HMI_Connection_1")
        {
            var meta = ResponseMeta.Basic(DateTime.Now, false);

            var steps = new JsonArray();
            meta["steps"] = steps;

            void Step(string name, bool ok, string? detail = null)
            {
                var o = new JsonObject
                {
                    ["step"] = name,
                    ["ok"] = ok
                };
                if (!string.IsNullOrWhiteSpace(detail)) o["detail"] = detail;
                steps.Add(o);
            }

            object? FindExistingByName(object compositionOrEnumerable, string name)
            {
                try
                {
                    if (compositionOrEnumerable is System.Collections.IEnumerable en)
                    {
                        foreach (var it in en)
                        {
                            var n = _session.TryGetName(it);
                            if (!string.IsNullOrWhiteSpace(n) &&
                                string.Equals(n!.Trim(), name, StringComparison.OrdinalIgnoreCase))
                            {
                                return it;
                            }
                        }
                    }
                }
                catch /* swallow(enumerate-optional): Unavailable collection enumeration preserves the missing-item fallback. */ { }
                return null;
            }

            try
            {
                var totalDeadline = DateTime.UtcNow.AddSeconds(25); // hard timeout for this tool
                bool TimedOut() => DateTime.UtcNow > totalDeadline;

                if (_session.IsProjectNull())
                {
                    Step("precheck", false, "Project is null");
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var sc = _session.GetSoftwareContainer(hmiSoftwarePath);
                if (sc?.Software == null)
                {
                    Step("resolveSoftware", false, $"SoftwareContainer not found at '{hmiSoftwarePath}'");
                    return new ResponseMessage { Message = "HMI software not found", Meta = meta };
                }

                var sw = sc.Software;
                Step("resolveSoftware", true, sw.GetType().FullName);

                // Resolve screen + tag table
                var screen = HmiScreenTraversal.FindByName(sw, screenName);
                if (screen == null)
                {
                    Step("findScreen", false, $"Screen '{screenName}' not found");
                    return new ResponseMessage { Message = "Screen not found", Meta = meta };
                }
                Step("findScreen", true, screen.GetType().FullName);

                var tagTable = _session.TryFindByNameInCollection(sw, new[] { "TagTables" }, tagTableName);
                if (tagTable == null)
                {
                    Step("findTagTable", false, $"TagTable '{tagTableName}' not found");
                    return new ResponseMessage { Message = "Tag table not found", Meta = meta };
                }
                Step("findTagTable", true, tagTable.GetType().FullName);

                // Ensure PLC↔HMI connection with correct driver for the actual PLC CPU (1200/1500 vs 300/400).
                try
                {
                    var connDesc = EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                    Step("ensureUnifiedHmiConnection", true, connDesc.Message ?? "ok");
                }
                catch (Exception ex)
                {
                    Step("ensureUnifiedHmiConnection", false, ex.InnerException?.Message ?? ex.Message);
                }

                var connName = string.IsNullOrWhiteSpace(connectionName) ? "HMI_Connection_1" : connectionName.Trim();
                Step("resolveConnection", true, connName);

                // Ensure tags in HMI tag table
                var tagsComp = tagTable.GetType().GetProperty("Tags")?.GetValue(tagTable);
                if (tagsComp == null)
                {
                    Step("resolveTagComposition", false, "tagTable.Tags not found");
                    return new ResponseMessage { Message = "Tag composition not found", Meta = meta };
                }
                Step("resolveTagComposition", true, tagsComp.GetType().FullName);

                string[] tagNames = new[] { "StartPB", "StopPB", "EStop", "RunOut" };
                foreach (var tn in tagNames)
                {
                    if (TimedOut())
                    {
                        Step("timeout", false, "Timeout during tag ensure");
                        return new ResponseMessage { Message = "Timeout", Meta = meta };
                    }
                    try
                    {
                        // find existing
                        // 去掉 ?? TryFindByNameInCollection(tagsComp, Array.Empty<string>(), ...)：空 hints 恒返回 null。
                        var exists = FindExistingByName(tagsComp, tn);
                        if (exists != null)
                        {
                            var wr = new JsonArray();
                            BindUnifiedHmiTagToPlcSymbol(exists, connName, plcName, tn, "Bool", wr);
                            Step($"tag:{tn}", true, "exists");
                            continue;
                        }

                        // create by reflection: Create(string)
                        var mCreate = tagsComp.GetType().GetMethod("Create", new[] { typeof(string) });
                        if (mCreate == null)
                        {
                            Step($"tag:{tn}", false, $"No Create(string) on {tagsComp.GetType().FullName}");
                            continue;
                        }

                        object? tagObj = null;
                        try
                        {
                            tagObj = mCreate.Invoke(tagsComp, new object[] { tn });
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            // If name already exists, treat as idempotent and return the existing object.
                            var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                            if (msg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var existing = FindExistingByName(tagsComp, tn);
                                if (existing != null)
                                {
                                    var wrU = new JsonArray();
                                    BindUnifiedHmiTagToPlcSymbol(existing, connName, plcName, tn, "Bool", wrU);
                                    Step($"tag:{tn}", true, "exists");
                                    continue;
                                }
                            }

                            Step($"tag:{tn}", false, msg);
                            continue;
                        }
                        if (tagObj == null)
                        {
                            Step($"tag:{tn}", false, "Create returned null");
                            continue;
                        }

                        _session.TrySetProperty(tagObj, "Name", tn);
                        var wrNew = new JsonArray();
                        BindUnifiedHmiTagToPlcSymbol(tagObj, connName, plcName, tn, "Bool", wrNew);

                        Step($"tag:{tn}", true, "created");
                    }
                    catch (Exception ex)
                    {
                        Step($"tag:{tn}", false, ex.InnerException?.Message ?? ex.Message);
                    }
                }

                // Create minimal screen items (best-effort): two buttons + one lamp
                var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
                if (itemsComp == null)
                {
                    Step("resolveScreenItems", false, "screen.ScreenItems not found");
                    return new ResponseMessage { Message = "ScreenItems not found", Meta = meta };
                }
                Step("resolveScreenItems", true, itemsComp.GetType().FullName);

                // Dump all Create* method signatures on ScreenItems composition so we see what's really there.
                var itemsType = itemsComp.GetType();
                var createSigs = itemsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name))}) -> {m.ReturnType.FullName}")
                    .ToArray();
                Step("screenItems.CreateSignatures", true, string.Join(" | ", createSigs));

                // Resolve candidate HMI widget types by FullName (from Siemens.Engineering.HmiUnified).
                Type? ResolveHmiType(params string[] fullNames)
                {
                    foreach (var fn in fullNames)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                var t = asm.GetType(fn, throwOnError: false, ignoreCase: false);
                                if (t != null) return t;
                            }
                            catch /* swallow(probe-optional): A type absent from this assembly is probed in the remaining assemblies. */ { }
                        }
                    }
                    return null;
                }

                var tButton = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiButton");
                var tIOField = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiIOField");
                var tRectangle = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRectangle",
                    "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle");
                var tLabel = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiLabel",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiLabel");
                Step("hmiTypeResolve", true,
                    $"Button={tButton?.AssemblyQualifiedName ?? "null"}; IOField={tIOField?.AssemblyQualifiedName ?? "null"}; Rectangle={tRectangle?.AssemblyQualifiedName ?? "null"}; Label={tLabel?.AssemblyQualifiedName ?? "null"}");

                // Try all Create overloads and candidate types; record per-attempt outcome.
                object? CreateItem(string name, Type?[] preferTypes, string[] stringTypeHints)
                {
                    var allAttempts = new List<string>();

                    // idempotent: return existing item if already present
                    var existingByName = FindExistingByName(itemsComp, name);
                    if (existingByName != null)
                    {
                        allAttempts.Add("EXISTS");
                        Step($"ui-detail:{name}", true, "exists");
                        return existingByName;
                    }

                    foreach (var m in itemsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                               .Where(x => x.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ps = m.GetParameters();

                        // Generic Create<T>(string name)
                        if (m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var gm = m.MakeGenericMethod(t!);
                                    var obj = gm.Invoke(itemsComp, new object[] { name });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}<{t!.Name}>(name)");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}<{t!.Name}>(name): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(string name, Type type)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Type))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var obj = m.Invoke(itemsComp, new object[] { name, t! });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}(name, typeof({t!.Name}))");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}(name, typeof({t!.Name})): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(Type type, string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(Type) && ps[1].ParameterType == typeof(string))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var obj = m.Invoke(itemsComp, new object[] { t!, name });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}(typeof({t!.Name}), name)");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}(typeof({t!.Name}), name): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(string name, string typeId) / Create(string typeId, string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                        {
                            foreach (var th in stringTypeHints)
                            {
                                foreach (var order in new[] { new object[] { name, th }, new object[] { th, name } })
                                {
                                    try
                                    {
                                        var obj = m.Invoke(itemsComp, order);
                                        if (obj != null)
                                        {
                                            allAttempts.Add($"OK {m.Name}({order[0]}, {order[1]})");
                                            return obj;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        allAttempts.Add($"ERR {m.Name}({order[0]}, {order[1]}): {(ex.InnerException?.Message ?? ex.Message)}");
                                        var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                        if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            var ex2 = FindExistingByName(itemsComp, name);
                                            if (ex2 != null) return ex2;
                                        }
                                    }
                                }
                            }
                        }

                        // Create(string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        {
                            try
                            {
                                var obj = m.Invoke(itemsComp, new object[] { name });
                                if (obj != null)
                                {
                                    allAttempts.Add($"OK {m.Name}(name)");
                                    return obj;
                                }
                            }
                            catch (Exception ex)
                            {
                                allAttempts.Add($"ERR {m.Name}(name): {(ex.InnerException?.Message ?? ex.Message)}");
                                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var ex2 = FindExistingByName(itemsComp, name);
                                    if (ex2 != null) return ex2;
                                }
                            }
                        }
                    }

                    Step($"ui-detail:{name}", false, string.Join(" || ", allAttempts));
                    return null;
                }

                var hdrBar = CreateItem("HDR_Bar", new[] { tRectangle }, new[] { "HmiRectangle", "Rectangle" });
                Step("ui:HDR_Bar", hdrBar != null, hdrBar?.GetType().FullName);
                var hdrTitle = CreateItem("HDR_Title", new[] { tLabel, tIOField }, new[] { "HmiLabel", "Label", "HmiText", "Text" });
                Step("ui:HDR_Title", hdrTitle != null, hdrTitle?.GetType().FullName);

                var btnStart = CreateItem("BTN_Start", new[] { tButton }, new[] { "HmiButton", "Button" });
                Step("ui:BTN_Start", btnStart != null, btnStart?.GetType().FullName);
                var btnStop = CreateItem("BTN_Stop", new[] { tButton }, new[] { "HmiButton", "Button" });
                Step("ui:BTN_Stop", btnStop != null, btnStop?.GetType().FullName);
                var lampRun = CreateItem("LAMP_Run", new[] { tRectangle, tIOField }, new[] { "HmiRectangle", "HmiIOField", "Lamp", "HmiLamp" });
                Step("ui:LAMP_Run", lampRun != null, lampRun?.GetType().FullName);

                // Layout + styling (Unified RT): header strip + grouped controls
                try
                {
                    if (hdrBar != null)
                    {
                        _session.TrySetProperty(hdrBar, "Left", 0);
                        _session.TrySetProperty(hdrBar, "Top", 0);
                        _session.TrySetProperty(hdrBar, "Width", (uint)1280);
                        _session.TrySetProperty(hdrBar, "Height", (uint)72);
                        _session.TrySetProperty(hdrBar, "BackColor", ColorTranslator.FromHtml("#1E3A5F"));
                        _session.TrySetProperty(hdrBar, "BorderWidth", (uint)0);
                    }

                    if (hdrTitle != null)
                    {
                        _session.TrySetProperty(hdrTitle, "Left", 24);
                        _session.TrySetProperty(hdrTitle, "Top", 12);
                        _session.TrySetProperty(hdrTitle, "Width", (uint)900);
                        _session.TrySetProperty(hdrTitle, "Height", (uint)48);
                        var txtH = hdrTitle.GetType().GetProperty("Text")?.GetValue(hdrTitle);
                        if (txtH != null)
                        {
                            _session.TrySetProperty(txtH, "Item", "MCP 验证 · 起停与状态");
                            _session.TrySetProperty(txtH, "HorizontalAlignment", "Left");
                        }

                        _session.TrySetProperty(hdrTitle, "ForeColor", Color.White);
                    }

                    if (btnStart != null)
                    {
                        _session.TrySetProperty(btnStart, "Left", 48);
                        _session.TrySetProperty(btnStart, "Top", 110);
                        _session.TrySetProperty(btnStart, "Width", (uint)200);
                        _session.TrySetProperty(btnStart, "Height", (uint)72);
                        _session.TrySetProperty(btnStart, "BackColor", ColorTranslator.FromHtml("#2E7D32"));
                        var txt = btnStart.GetType().GetProperty("Text")?.GetValue(btnStart);
                        if (txt != null)
                        {
                            _session.TrySetProperty(txt, "Item", "启动 (Start)");
                            _session.TrySetProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (btnStop != null)
                    {
                        _session.TrySetProperty(btnStop, "Left", 48);
                        _session.TrySetProperty(btnStop, "Top", 200);
                        _session.TrySetProperty(btnStop, "Width", (uint)200);
                        _session.TrySetProperty(btnStop, "Height", (uint)72);
                        _session.TrySetProperty(btnStop, "BackColor", ColorTranslator.FromHtml("#C62828"));
                        var txt = btnStop.GetType().GetProperty("Text")?.GetValue(btnStop);
                        if (txt != null)
                        {
                            _session.TrySetProperty(txt, "Item", "停止 (Stop)");
                            _session.TrySetProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (lampRun != null)
                    {
                        _session.TrySetProperty(lampRun, "Left", 300);
                        _session.TrySetProperty(lampRun, "Top", 110);
                        _session.TrySetProperty(lampRun, "Width", (uint)120);
                        _session.TrySetProperty(lampRun, "Height", (uint)120);
                        _session.TrySetProperty(lampRun, "BackColor", ColorTranslator.FromHtml("#B0BEC5"));
                        _session.TrySetProperty(lampRun, "BorderWidth", (uint)2);
                    }

                    Step("ui:layout", true);
                }
                catch (Exception ex)
                {
                    Step("ui:layout", false, ex.InnerException?.Message ?? ex.Message);
                }

                // Attempt: map button pressed state to HMI tag (momentary) via PressedStateTags composition (best-effort)
                void TryBindPressedTag(object? button, string table, string tagName)
                {
                    if (button == null) return;
                    try
                    {
                        var pst = button.GetType().GetProperty("PressedStateTags")?.GetValue(button);
                        if (pst == null)
                        {
                            Step($"bind:{_session.TryGetName(button)}.PressedStateTags", false, "PressedStateTags missing");
                            return;
                        }

                        // dump create signatures once per button
                        var sigs = pst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName))})")
                            .ToArray();
                        Step($"bind:{_session.TryGetName(button)}.PressedStateTags.CreateSignatures", true, string.Join(" | ", sigs));

                        // idempotent check
                        var existing = FindExistingByName(pst, tagName);
                        if (existing != null)
                        {
                            Step($"bind:{_session.TryGetName(button)}:{tagName}", true, "exists");
                            return;
                        }

                        // Try Create() then bind HMI tag path (table/tag) for Unified RT
                        var m0 = pst.GetType().GetMethod("Create", Type.EmptyTypes);
                        if (m0 != null)
                        {
                            try
                            {
                                var o = m0.Invoke(pst, Array.Empty<object>());
                                if (o != null)
                                {
                                    var path = string.IsNullOrWhiteSpace(table) ? tagName : $"{table}/{tagName}";
                                    var bound = TrySetAnyProperty(o, path, "Tag", "TagName", "HmiTag", "HmiTagName", "Path", "HmiTagPath", "FullName")
                                                || TrySetEngineeringAttribute(o, "Tag", path)
                                                || TrySetEngineeringAttribute(o, "HmiTag", path);
                                    Step($"bind:{_session.TryGetName(button)}:{tagName}", bound, o.GetType().FullName);
                                    return;
                                }
                            }
                            catch (TargetInvocationException tie) when (tie.InnerException != null)
                            {
                                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                                Step($"bind:{_session.TryGetName(button)}:{tagName}", false, msg);
                                return;
                            }
                        }

                        // Try Create(string)
                        var m1 = pst.GetType().GetMethod("Create", new[] { typeof(string) });
                        if (m1 != null)
                        {
                            try
                            {
                                var path2 = string.IsNullOrWhiteSpace(table) ? tagName : $"{table}/{tagName}";
                                var o = m1.Invoke(pst, new object[] { path2 });
                                Step($"bind:{_session.TryGetName(button)}:{tagName}", o != null, o?.GetType().FullName);
                                return;
                            }
                            catch (TargetInvocationException tie) when (tie.InnerException != null)
                            {
                                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                                Step($"bind:{_session.TryGetName(button)}:{tagName}", false, msg);
                                return;
                            }
                        }

                        // Fallback: some parts may expose a property like TagName/Tag
                        Step($"bind:{_session.TryGetName(button)}:{tagName}", false, "No suitable Create on PressedStateTags");
                    }
                    catch (Exception ex)
                    {
                        Step($"bind:{_session.TryGetName(button)}:{tagName}", false, ex.InnerException?.Message ?? ex.Message);
                    }
                }

                TryBindPressedTag(btnStart, tagTableName, "StartPB");
                TryBindPressedTag(btnStop, tagTableName, "StopPB");

                // Attempt: lamp BackColor dynamization based on RunOut (best-effort; may need richer APIs)
                try
                {
                    if (lampRun != null)
                    {
                        var dyn = lampRun.GetType().GetProperty("Dynamizations")?.GetValue(lampRun);
                        if (dyn == null)
                        {
                            Step("dyn:LAMP_Run", false, "Dynamizations missing");
                        }
                        else
                        {
                            var sigs = dyn.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName))})")
                                .ToArray();
                            Step("dyn:LAMP_Run.CreateSignatures", true, string.Join(" | ", sigs));

                            // No universal way here without knowing specific dynamization classes;
                            // return signatures so next iteration can target correct Create overload.
                            Step("dyn:LAMP_Run", false, "Not implemented yet (see CreateSignatures)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Step("dyn:LAMP_Run", false, ex.InnerException?.Message ?? ex.Message);
                }

                // envelope: legacy-single-verdict
                meta["success"] = true;
                return new ResponseMessage
                {
                    Message = "Unified HMI start/stop skeleton created (best-effort).",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                Step("exception", false, ex.ToString());
                return new ResponseMessage { Message = "Failed creating HMI skeleton", Meta = meta };
            }
        }

        public ResponseMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath, string screenName, uint width = 0, uint height = 0)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiScreen", meta =>
            {
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var screens = TryGetPropertyValue(sw, "Screens");
                if (screens == null) throw new InvalidOperationException("HMI Screens collection not found.");

                var screen = HmiScreenTraversal.FindByName(sw, screenName);
                var action = "exists";
                if (screen == null)
                {
                    var mCreate = screens.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (mCreate == null) throw new InvalidOperationException($"Create(string) not found on {screens.GetType().FullName}.");
                    screen = InvokeCreate(mCreate, screens, new object[] { screenName });
                    action = "created";
                }

                if (screen == null) throw new InvalidOperationException("Screen create/find returned null.");
                if (width > 0) _session.TrySetProperty(screen, "Width", width);
                if (height > 0) _session.TrySetProperty(screen, "Height", height);

                meta["action"] = action;
                meta["screenType"] = screen.GetType().FullName;
                return $"HMI screen '{screenName}' {action}.";
            });
        }

        public ResponseMessage EnsureUnifiedHmiTagTable(string hmiSoftwarePath, string tagTableName)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiTagTable", meta =>
            {
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var tables = _session.TryGetHmiTagTablesCollection(sw);
                if (tables == null) throw new InvalidOperationException($"HMI TagTables collection not found. hmiType={sw.GetType().FullName}; tagRootType={_session.TryGetHmiTagRoot(sw).GetType().FullName}");

                var table = _session.TryFindHmiTagTable(sw, tagTableName);
                var action = "exists";
                if (table == null)
                {
                    table = TryCreateNamedEngineeringObject(tables, tagTableName, out var createError);
                    if (table == null) throw new InvalidOperationException(createError ?? $"Create failed on {tables.GetType().FullName}.");
                    action = "created";
                }

                if (table == null) throw new InvalidOperationException("Tag table create/find returned null.");
                meta["action"] = action;
                meta["tagTableType"] = table.GetType().FullName;
                return $"HMI tag table '{tagTableName}' {action}.";
            });
        }

        /// <summary>
        /// Unified HMI tag → PLC symbolic binding (same rules as <see cref="EnsureUnifiedHmiTag"/>).
        /// </summary>
        private void BindUnifiedHmiTagToPlcSymbol(
            object tag,
            string connectionName,
            string plcName,
            string plcTagSymbol,
            string hmiDataType,
            JsonArray writeResults,
            string address = "")
        {
            bool Set(string label, object? value, params string[] names)
            {
                var ok = TrySetAnyPropertyOrAttribute(tag, value, names);
                writeResults.Add($"{label}={ok}");
                return ok;
            }

            var tagTypeCandidates = new[]
            {
                "External",
                "ExternalTag",
                "HmiExternal",
                "ConnectedExternal",
                "PLC",
                "Plc",
                "Process",
                "ConnectionTag",
                "HmiTag"
            };

            Set("DataType", hmiDataType, "DataType", "HmiDataType");
            TrySetEngineeringAttribute(tag, "DataType", hmiDataType);
            TrySetEngineeringAttribute(tag, "HmiDataType", hmiDataType);
            var tagTypeSet = TrySetAnyEnumCandidatePropertyOrAttribute(tag, tagTypeCandidates, "TagType", "Type", "Kind");
            writeResults.Add("TagTypeCandidate=" + tagTypeSet);
            if (!string.IsNullOrWhiteSpace(plcName))
                Set("PlcName", plcName, "PlcName", "ControllerName", "Station");
            if (!string.IsNullOrWhiteSpace(connectionName))
                Set("Connection", connectionName, "Connection", "ConnectionName");
            var targetPlcTag = string.IsNullOrWhiteSpace(plcTagSymbol) ? string.Empty : plcTagSymbol;
            var targetAddress = string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim();
            // Only true PLC absolute operands (e.g. %DB200.DBX0.0, DB200.DBX0.0). Do NOT treat symbolic
            // "DB_HMI_Interface.Member" as absolute — that wrongly flipped AddressAccessMode and broke PLC binding.
            var isAbsoluteAddress =
                !string.IsNullOrWhiteSpace(targetAddress)
                || targetPlcTag.StartsWith("%", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(
                    targetPlcTag,
                    @"^(DB|IW|QW|ID|QD|IB|QB|MB|MW|MD)\d",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (isAbsoluteAddress)
            {
                if (!string.IsNullOrWhiteSpace(targetPlcTag) && !targetPlcTag.StartsWith("%", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedTag = NormalizeControllerTagName(targetPlcTag);
                    TryBindUnifiedHmiTagPlcSymbolicPaths(tag, normalizedTag, writeResults);
                }

                TrySetUnifiedHmiTagAddressingModeEnum(tag, symbolic: false, writeResults);
                var runtimeAddress = string.IsNullOrWhiteSpace(targetAddress) ? targetPlcTag : targetAddress;
                TrySetUnifiedHmiTagRuntimeAddress(tag, runtimeAddress, writeResults);
            }
            else
            {
                Set("ClearAddress", string.Empty, "Address", "LogicalAddress");
                TrySetEngineeringAttribute(tag, "Address", string.Empty);
                TrySetEngineeringAttribute(tag, "LogicalAddress", string.Empty);
                TrySetUnifiedHmiTagAddressingModeEnum(tag, symbolic: true, writeResults);
                var normalizedTag = NormalizeControllerTagName(targetPlcTag);
                TryBindUnifiedHmiTagPlcSymbolicPaths(tag, normalizedTag, writeResults);
                if (TrySetUnifiedHmiTagAccessModeByEnumScan(tag, true))
                    writeResults.Add("AccessMode_repass_symbolic=true");
            }
        }

        private void TrySetUnifiedHmiTagRuntimeAddress(object tag, string runtimeAddress, JsonArray writeResults)
        {
            var addressNames = new[]
            {
                "Address",
                "LogicalAddress",
                "ProcessValueAddress",
                "RuntimeAddress",
                "ControllerAddress",
                "ControllerTagAddress",
                "ExternalAddress",
                "PlcAddress",
                "PLCAddress",
                "TagAddress",
                "AbsoluteAddress"
            };

            var primaryOk = TrySetAnyPropertyOrAttribute(tag, runtimeAddress, "Address", "LogicalAddress");
            writeResults.Add("RuntimeAddressPrimary=" + primaryOk);

            var readback = TryReadUnifiedHmiTagRuntimeAddress(tag, addressNames);
            if (!string.Equals(readback, runtimeAddress, StringComparison.OrdinalIgnoreCase))
            {
                var extraOk = false;
                foreach (var name in addressNames.Skip(2))
                {
                    extraOk = _session.TrySetProperty(tag, name, runtimeAddress) || TrySetEngineeringAttribute(tag, name, runtimeAddress) || extraOk;
                }

                writeResults.Add("RuntimeAddressExtra=" + extraOk);
                readback = TryReadUnifiedHmiTagRuntimeAddress(tag, addressNames);
            }

            writeResults.Add("RuntimeAddressReadback=" + (readback ?? string.Empty));
        }

        private string TryReadUnifiedHmiTagRuntimeAddress(object tag, params string[] addressNames)
        {
            foreach (var name in addressNames)
            {
                try
                {
                    var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var value = prop.GetValue(tag)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value!;
                    }
                }
                catch /* swallow(probe-optional): Unreadable address properties fall through to engineering attributes and other names. */
                {
                }

                var attr = _session.TryGetEngineeringAttribute(tag, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(attr)) return attr!;
            }

            return string.Empty;
        }

        /// <summary>
        /// WinCC Unified HMI tags expose addressing mode as enums; writing display strings (e.g. "SymbolicAccess")
        /// via generic SetProperty fails silently and leaves the UI on default Absolute with empty Address/PLC tag.
        /// </summary>
        private void TrySetUnifiedHmiTagAddressingModeEnum(object tag, bool symbolic, JsonArray writeResults)
        {
            var ok = TrySetUnifiedHmiTagAccessModeByEnumScan(tag, symbolic);
            if (!ok)
            {
                var candidates = symbolic
                    ? new[] { "Symbolic", "SymbolicAccess", "FromTag", "HmiSymbolic", "ExternalSymbolic", "TagSymbolic" }
                    : new[] { "Absolute", "AbsoluteAccess", "Direct", "HmiAbsolute", "ExternalAbsolute", "TagAbsolute" };
                ok = TrySetAnyEnumCandidatePropertyOrAttribute(tag, candidates, "AddressAccessMode", "AccessMode", "TagAddressingMode", "HmiTagAddressingMode");
            }

            writeResults.Add($"AddressingMode({(symbolic ? "symbolic" : "absolute")})={ok}");
        }

        /// <summary>
        /// Unified <see cref="HmiTag"/> access mode enum names differ by TIA version; scan all declared enum members.
        /// </summary>
        private bool TrySetUnifiedHmiTagAccessModeByEnumScan(object tag, bool wantSymbolic)
        {
            foreach (var propName in new[] { "AccessMode", "AddressAccessMode", "TagAddressingMode", "HmiTagAddressingMode" })
            {
                try
                {
                    var prop = tag.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null || !prop.CanWrite || !prop.PropertyType.IsEnum) continue;
                    foreach (var enumName in Enum.GetNames(prop.PropertyType))
                    {
                        var u = enumName.ToUpperInvariant();
                        var match = wantSymbolic
                            ? u.Contains("SYMBOL") || u.Contains("NAMED")
                            : u.Contains("ABSOL") || u.Contains("DIRECT") || u.Contains("ADDRESS");
                        if (!match) continue;
                        var ev = Enum.Parse(prop.PropertyType, enumName);
                        prop.SetValue(tag, ev);
                        TrySetEngineeringAttribute(tag, propName, ev);
                        return true;
                    }
                }
                catch /* swallow(native-fallback): An unsupported tag access-mode write leaves the remaining enum candidates available. */
                {
                }
            }

            return false;
        }

        /// <summary>
        /// S7-1200/1500 PLC partner in HMI connections uses rack 0 and CPU slot 1 in almost all compact PLC projects.
        /// Missing slot shows as "?" in TIA and breaks tag resolution.
        /// </summary>
        private void TryConfigureUnifiedHmiConnectionS7PartnerRackSlot(object connection, string plcFamily)
        {
            if (plcFamily != "S71200" && plcFamily != "S71500" && plcFamily != "UNKNOWN") return;

            foreach (var slotName in new[] { "PartnerSlot", "Slot", "PlcSlot", "PartnerExpansionSlot", "ExpansionSlot", "ControllerSlot" })
            {
                foreach (var slotVal in new object[] { 1, (short)1, (ushort)1, "1" })
                {
                    if (_session.TrySetProperty(connection, slotName, slotVal) || TrySetEngineeringAttribute(connection, slotName, slotVal))
                    {
                        break;
                    }
                }
            }

            foreach (var rackName in new[] { "PartnerRack", "Rack", "PlcRack", "ControllerRack" })
            {
                foreach (var rackVal in new object[] { 0, (short)0, (ushort)0, "0" })
                {
                    if (_session.TrySetProperty(connection, rackName, rackVal) || TrySetEngineeringAttribute(connection, rackName, rackVal))
                    {
                        break;
                    }
                }
            }
        }

        private void TryBindUnifiedHmiTagPlcSymbolicPaths(object tag, string normalizedTag, JsonArray writeResults)
        {
            if (string.IsNullOrWhiteSpace(normalizedTag)) return;
            var names = new[]
            {
                "PlcTag", "ControllerTag", "ControllerTagName", "ProcessTag", "ExternalTag", "Tag", "TagName", "SymbolicAddress"
            };
            foreach (var n in names)
            {
                var ok = _session.TrySetProperty(tag, n, normalizedTag) || TrySetEngineeringAttribute(tag, n, normalizedTag);
                writeResults.Add($"{n}={ok}");
            }
        }

        /// <summary>
        /// Unified HMI connection CommunicationDriver is often an engineering attribute whose runtime type is an enum.
        /// Passing a human-readable driver string into SetAttribute then fails Enum.Parse and leaves S7-300/400 default.
        /// </summary>
        private static bool TrySetUnifiedHmiCommunicationDriverEnum(object connection, string plcFamily)
        {
            try
            {
                var get = connection.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var set = connection.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (get == null || set == null) return false;

                Type? enumType = null;
                var prop = connection.GetType().GetProperty("CommunicationDriver", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum) enumType = prop.PropertyType;
                if (enumType == null)
                {
                    try
                    {
                        var cur = get.Invoke(connection, new object[] { "CommunicationDriver" });
                        if (cur != null && cur.GetType().IsEnum) enumType = cur.GetType();
                    }
                    catch /* swallow(probe-optional): An unavailable driver attribute leaves the reflected property type as the candidate. */
                    {
                    }
                }

                if (enumType == null || !enumType.IsEnum) return false;

                var ev = SelectCommunicationDriverEnumValue(enumType, plcFamily);
                if (ev == null && (plcFamily == "UNKNOWN" || plcFamily == "S71200" || plcFamily == "S71500"))
                {
                    foreach (var name in Enum.GetNames(enumType))
                    {
                        var u = name.ToUpperInvariant();
                        if (u.Contains("1200") || u.Contains("1500") || u.Contains("S712") || u.Contains("S715") || u.Contains("PLUS"))
                        {
                            ev = Enum.Parse(enumType, name);
                            break;
                        }
                    }
                }

                if (ev == null) return false;

                set.Invoke(connection, new object[] { "CommunicationDriver", ev });
                if (prop != null && prop.CanWrite)
                {
                    try
                    {
                        prop.SetValue(connection, ev);
                    }
                    catch /* swallow(native-fallback): The engineering attribute has been set; an unsupported property mirror does not replace that result. */
                    {
                    }
                }

                return true;
            }
            catch /* swallow(native-fallback): Driver enum configuration reports false so the existing driver fallbacks can run. */
            {
                return false;
            }
        }

        public ResponseMessage EnsureUnifiedHmiTag(string hmiSoftwarePath, string tagTableName, string tagName, string hmiDataType = "Bool", string plcName = "PLC_1", string plcTag = "", string connectionName = "", string address = "", bool requireVerifiedBinding = true)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiTag", meta =>
            {
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var tagTable = EnsureHmiTagTableObject(sw, tagTableName);
                var tags = TryGetPropertyValue(tagTable, "Tags");
                if (tags == null) throw new InvalidOperationException($"Tags collection not found on tag table '{tagTableName}'.");

                // 去掉 ?? TryFindByNameInCollection(tags, Array.Empty<string>(), ...)：空 hints 恒返回 null。
                var tag = _session.FindExistingByName(tags, tagName);
                var action = "exists";
                if (tag == null)
                {
                    tag = TryCreateNamedEngineeringObject(tags, tagName, out var createError);
                    if (tag == null) throw new InvalidOperationException(createError ?? $"Create failed on {tags.GetType().FullName}.");
                    action = "created";
                }

                if (tag == null) throw new InvalidOperationException("Tag create/find returned null.");
                var writeResults = new JsonArray();
                var targetPlcTag = string.IsNullOrWhiteSpace(plcTag) ? tagName : plcTag;
                BindUnifiedHmiTagToPlcSymbol(tag, connectionName, plcName, targetPlcTag, hmiDataType, writeResults, address);

                meta["action"] = action;
                meta["tagType"] = tag.GetType().FullName;
                meta["tagEnumHints"] = DescribeWritableEnumProperties(tag, "TagType", "AccessMode", "AddressAccessMode");
                meta["requestedPlcTag"] = targetPlcTag;
                meta["requestedAddress"] = address ?? string.Empty;
                meta["writeResults"] = writeResults;
                var binding = ClassifyUnifiedHmiTagBinding(tag, connectionName, targetPlcTag, address ?? string.Empty);
                meta["readback"] = binding.Readback;
                meta["bindingStatus"] = binding.Status;
                meta["bindingVerified"] = binding.Verified;
                meta["bindingGuidance"] = binding.Guidance;
                meta["requireVerifiedBinding"] = requireVerifiedBinding;
                if (requireVerifiedBinding && !binding.Verified)
                {
                    throw new InvalidOperationException($"HMI tag '{tagName}' binding is not verified. Status={binding.Status}; {binding.Guidance}; Readback={binding.Readback}");
                }

                return $"HMI tag '{tagName}' {action}. Binding={binding.Status}.";
            });
        }

        public ModelContextProtocol.ResponseObjectDescribe EnsureUnifiedHmiConnection(string hmiSoftwarePath, string connectionName = "HMI_Connection_1", string plcName = "PLC_1")
        {
            var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new InvalidOperationException($"Connections collection not found on HMI software '{hmiSoftwarePath}'.");

            // 去掉 ?? TryFindByNameInCollection(connections, Array.Empty<string>(), ...)：空 hints 恒返回 null。
            var connection = _session.FindExistingByName(connections, connectionName);
            if (connection == null)
            {
                var create = connections.GetType().GetMethod("Create", new[] { typeof(string) });
                if (create == null) throw new InvalidOperationException($"Create(string) not found on {connections.GetType().FullName}.");
                connection = InvokeCreate(create, connections, new object[] { connectionName });
            }

            if (connection == null) throw new InvalidOperationException("Connection create/find returned null.");

            _session.TrySetProperty(connection, "Name", connectionName);
            var partner = ResolveUnifiedHmiPlcPartner(plcName);
            TryConfigureUnifiedHmiConnectionPartner(connection, partner);
            TryConfigureUnifiedHmiConnectionS7PartnerRackSlot(connection, partner.Family);
            // Driver last so partner binding cannot clobber S7-1200/1500 selection.
            TryConfigureUnifiedHmiCommunicationDriver(connection, plcName);
            ValidateUnifiedHmiCommunicationDriver(connection, partner.Family);

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                ObjectKind = "HmiConnection",
                ObjectPath = $"{hmiSoftwarePath}:{connectionName}",
                TypeName = connection.GetType().FullName,
                Members = _session.DescribeMembers(connection, 220),
                Message = $"HMI connection '{connectionName}' ensured. PartnerResolved={partner.Summary}; {_session.SummarizeHmiObjectReadback(connection, "Name", "CommunicationDriver", "Partner", "Station", "Node", "InitialAddress", "PlcName", "ControllerName", "PartnerName")}"
            };
        }

        public ResponseMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath, string screenName, string itemName, string itemType = "Button", int left = 0, int top = 0, uint width = 120, uint height = 40, string text = "")
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiScreenItem", meta =>
            {
                var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems");
                if (items == null) throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var item = _session.FindExistingByName(items, itemName);
                var action = "exists";
                if (item == null)
                {
                    var itemClrType = ResolveUnifiedScreenItemType(itemType);
                    item = CreateUnifiedScreenItem(items, itemName, itemClrType, itemType);
                    action = "created";
                }

                if (item == null) throw new InvalidOperationException("Screen item create/find returned null.");
                _session.TrySetProperty(item, "Left", left);
                _session.TrySetProperty(item, "Top", top);
                _session.TrySetProperty(item, "Width", width);
                _session.TrySetProperty(item, "Height", height);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var textPart = TryGetPropertyValue(item, "Text", "DisplayName");
                    if (textPart != null) _session.TrySetProperty(textPart, "Item", text);
                }

                meta["action"] = action;
                meta["itemType"] = item.GetType().FullName;
                return $"HMI screen item '{itemName}' {action}.";
            });
        }

        public ResponseMessage ReadUnifiedHmiTexts(string hmiSoftwarePath, string screenName, string itemName, string textProperty)
        {
            return _session.RunHmiStepTool("ReadUnifiedHmiTexts", meta =>
            {
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var text = TryGetPropertyValue(item, textProperty)
                    ?? throw new InvalidOperationException(textProperty + " is unavailable.");
                meta["languages"] = UnifiedMultilingualText.Read(text);
                meta["itemName"] = itemName;
                meta["textProperty"] = textProperty;
                return "Multilingual text readback completed.";
            });
        }

        public ResponseMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath, string screenName, string designJson, bool strict = true)
        {
            return _session.RunHmiStepTool("ApplyUnifiedHmiScreenDesignJson", meta =>
            {
                if (string.IsNullOrWhiteSpace(designJson))
                {
                    throw new InvalidOperationException("designJson is empty.");
                }

                var root = JsonNode.Parse(designJson) as JsonObject
                    ?? throw new InvalidOperationException("designJson root must be a JSON object.");

                var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems")
                    ?? throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var changed = new JsonArray();
                var itemResults = new JsonArray();
                meta["itemResults"] = itemResults;
                var failed = new JsonArray();
                var textReadback = new JsonArray();
                meta["textReadback"] = textReadback;
                meta["persistence"] = "Not saved by this tool; partial changes may remain in the open project on failure.";

                if (root["screen"] is JsonObject screenProps)
                {
                    ApplyJsonProperties(screen, screenProps, failed, "screen");
                }

                var itemArray = root["items"] as JsonArray
                    ?? throw new InvalidOperationException("designJson.items must be an array.");

                foreach (var itemNode in itemArray.OfType<JsonObject>())
                {
                    var name = JsonString(itemNode, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        failed.Add("item without name skipped");
                        continue;
                    }

                    var failuresBeforeItem = failed.Count;
                    // envelope: legacy-verdict-last
                    var itemStatus = new JsonObject { ["name"] = name, ["created"] = false, ["mayHaveChanged"] = false, ["success"] = false };
                    itemResults.Add(itemStatus);
                    try
                    {
                        var hasText = UnifiedMultilingualText.ReadRequest(itemNode, out var text);
                        if (hasText) System.Globalization.CultureInfo.GetCultureInfo(JsonString(itemNode, "culture") ?? "zh-CN");
                        var typeHint = JsonString(itemNode, "type");
                        if (string.IsNullOrWhiteSpace(typeHint)) typeHint = "Rectangle";

                        var item = _session.FindExistingByName(items, name!);
                        var action = "updated";
                        if (item == null)
                        {
                            var itemClrType = ResolveUnifiedScreenItemType(typeHint!);
                            item = CreateUnifiedScreenItem(items, name!, itemClrType, typeHint!);
                            action = "created";
                            itemStatus["created"] = item != null;
                            itemStatus["mayHaveChanged"] = true;
                        }

                        if (item == null) throw new InvalidOperationException($"Create/find returned null for '{name}'.");

                        if (hasText)
                            UnifiedMultilingualText.Validate(item, JsonString(itemNode, "textProperty") is string tp && !string.IsNullOrWhiteSpace(tp) ? tp : "Text",
                                JsonString(itemNode, "culture") ?? "zh-CN");

                        itemStatus["mayHaveChanged"] = true;
                        if (itemNode["left"] != null) _session.TrySetProperty(item, "Left", JsonObjectValue(itemNode["left"]));
                        if (itemNode["top"] != null) _session.TrySetProperty(item, "Top", JsonObjectValue(itemNode["top"]));
                        if (itemNode["width"] != null) _session.TrySetProperty(item, "Width", JsonObjectValue(itemNode["width"]));
                        if (itemNode["height"] != null) _session.TrySetProperty(item, "Height", JsonObjectValue(itemNode["height"]));

                        if (itemNode["properties"] is JsonObject props)
                        {
                            ApplyJsonProperties(item, props, failed, name!, typeHint ?? string.Empty);
                        }

                        if (hasText)
                        {
                            var textTarget = JsonString(itemNode, "textProperty");
                            if (string.IsNullOrWhiteSpace(textTarget)) textTarget = "Text";
                            var readback = UnifiedMultilingualText.WriteDetailed(item, textTarget!, text, JsonString(itemNode, "culture") ?? "zh-CN");
                            readback["name"] = name;
                            readback["languages"] = readback["after"]?.DeepClone();
                            textReadback.Add(readback);
                            if (!readback["verified"]!.GetValue<bool>())
                                throw new InvalidOperationException(readback["error"]?.ToString() ?? "Text verification failed.");
                        }

                        if (itemNode["font"] is JsonObject font)
                        {
                            var fontPart = TryGetPropertyValue(item, "Font");
                            if (fontPart != null) ApplyJsonProperties(fontPart, font, failed, name + ".Font");
                            else failed.Add($"{name}.Font: part not found");
                        }

                        if (itemNode["content"] is JsonObject content)
                        {
                            var contentPart = TryGetPropertyValue(item, "Content");
                            if (contentPart != null) ApplyJsonProperties(contentPart, content, failed, name + ".Content");
                            else failed.Add($"{name}.Content: part not found");
                        }

                        if (itemNode["padding"] is JsonObject padding)
                        {
                            var paddingPart = TryGetPropertyValue(item, "Padding");
                            if (paddingPart != null) ApplyJsonProperties(paddingPart, padding, failed, name + ".Padding");
                            else failed.Add($"{name}.Padding: part not found");
                        }

                        changed.Add($"{action}:{name}:{item.GetType().Name}");
                        itemStatus["success"] = failed.Count == failuresBeforeItem;
                    }
                    catch (Exception ex)
                    {
                        itemStatus["error"] = (ex.InnerException ?? ex).Message;
                        failed.Add($"{name} (textProperty={JsonString(itemNode, "textProperty") ?? "Text"}, culture={JsonString(itemNode, "culture") ?? "zh-CN"}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                meta["changed"] = changed;
                meta["changedMeaning"] = "Items that reached the end of processing; not a guarantee of no other changes. Inspect itemResults and textReadback, including on failure.";
                meta["failed"] = failed;
                meta["strict"] = strict;
                meta["operationSuccess"] = failed.Count == 0;
                if (strict && failed.Count > 0)
                {
                    throw new InvalidOperationException($"Unified HMI design apply had {failed.Count} failed writes: {string.Join(" | ", failed.Select(x => x?.ToString() ?? string.Empty))}");
                }
                return $"Applied Unified HMI design to '{screenName}'. changed={changed.Count}, failed={failed.Count}.";
            });
        }

        public ResponseMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath, string screenName, string buttonName, string tagName)
        {
            return _session.RunHmiStepTool("BindUnifiedHmiButtonPressedTag", meta =>
            {
                var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems");
                if (items == null) throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var button = _session.FindExistingByName(items, buttonName);
                if (button == null) throw new InvalidOperationException($"Screen item '{buttonName}' not found.");

                var pressedStateTags = TryGetPropertyValue(button, "PressedStateTags");
                if (pressedStateTags == null) throw new InvalidOperationException($"PressedStateTags not found on '{buttonName}'.");

                var existing = FindPressedStateTag(pressedStateTags, tagName);
                var action = "exists";
                if (existing == null)
                {
                    var mCreate = pressedStateTags.GetType().GetMethod("Create", Type.EmptyTypes);
                    if (mCreate == null) throw new InvalidOperationException($"Create() not found on {pressedStateTags.GetType().FullName}.");
                    existing = InvokeCreate(mCreate, pressedStateTags, Array.Empty<object>());
                    action = "created";
                }

                if (existing == null) throw new InvalidOperationException("Pressed-state part create/find returned null.");
                var bound = TrySetAnyProperty(existing, tagName, "Tag", "TagName", "HmiTag", "HmiTagName", "Name", "TagPath");

                meta["action"] = action;
                meta["pressedStateTagPartType"] = existing.GetType().FullName;
                meta["propertyBound"] = bound;
                if (!bound)
                {
                    meta["availableProperties"] = string.Join(", ", existing.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{p.Name}:{p.PropertyType.Name}"));
                }

                return bound
                    ? $"Button '{buttonName}' pressed-state tag bound to '{tagName}'."
                    : $"Pressed-state part created for '{buttonName}', but no writable tag-name property was found.";
            });
        }

        public List<string> ListUnifiedHmiApiTypes(string nameContains = "", int limit = 500)
        {
            var filter = nameContains?.Trim() ?? string.Empty;
            var result = new List<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch /* swallow(probe-optional): Assemblies whose type lists cannot load are skipped during the API inventory. */
                {
                    continue;
                }

                foreach (var t in types)
                {
                    if (t.FullName == null || !t.FullName.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrWhiteSpace(filter) && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var kind = t.IsEnum ? "enum" : t.IsClass ? "class" : t.IsInterface ? "interface" : t.IsValueType ? "value" : "type";
                    var line = $"{kind}: {t.FullName}";
                    if (t.BaseType != null && t.BaseType != typeof(object))
                    {
                        line += $" : {t.BaseType.FullName}";
                    }
                    if (t.IsEnum)
                    {
                        line += $" values=[{string.Join(",", Enum.GetNames(t).Take(50))}]";
                    }

                    result.Add(line);
                    if (result.Count >= Math.Max(1, limit)) return result;
                }
            }

            return result;
        }

        public ResponseMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath, string screenName, string buttonName, string eventType)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiButtonEventHandler", meta =>
            {
                var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
                var eventHandlers = TryGetPropertyValue(button, "EventHandlers");
                if (eventHandlers == null) throw new InvalidOperationException($"EventHandlers not found on '{buttonName}'.");

                var create = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                if (create == null) throw new InvalidOperationException($"Create(enum) not found on {eventHandlers.GetType().FullName}.");

                var enumType = create.GetParameters()[0].ParameterType;
                if (!Enum.GetNames(enumType).Any(n => string.Equals(n, eventType, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Invalid event name: " + eventType + " (valid for " + enumType.Name + ": " + string.Join("/", Enum.GetNames(enumType)) + "; a WinCC Unified button click is 'Tapped').");
                var enumValue = Enum.Parse(enumType, eventType, ignoreCase: true);

                object? handler = null;
                var find = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == enumType);
                if (find != null)
                {
                    handler = find.Invoke(eventHandlers, new[] { enumValue });
                }

                var action = "exists";
                if (handler == null)
                {
                    handler = InvokeCreate(create, eventHandlers, new[] { enumValue });
                    action = "created";
                }

                if (handler == null) throw new InvalidOperationException("Event handler create/find returned null.");
                meta["action"] = action;
                meta["eventEnumType"] = enumType.FullName;
                meta["eventType"] = enumValue.ToString();
                meta["handlerType"] = handler.GetType().FullName;
                meta["handlerMembers"] = string.Join(" | ", _session.DescribeMembers(handler, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                return $"Button event handler '{eventValueToText(enumValue)}' on '{buttonName}' {action}.";
            });

            static string eventValueToText(object value) => value.ToString() ?? string.Empty;
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath, string screenName, string buttonName, string eventType, int maxMembers = 200)
        {
            try
            {
                if (_session.IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
                }

                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType, createIfMissing: false);
                var scriptProp = handler.GetType().GetProperty("Script", BindingFlags.Public | BindingFlags.Instance);
                var script = scriptProp?.GetValue(handler);

                var members = new List<ModelContextProtocol.ObjectMember>
                {
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "HandlerType",
                        Kind = "Info",
                        Type = handler.GetType().FullName ?? handler.GetType().Name,
                        Signature = null
                    },
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "ScriptProperty",
                        Kind = "Info",
                        Type = scriptProp == null ? "missing" : $"{scriptProp.PropertyType.FullName}; CanRead={scriptProp.CanRead}; CanWrite={scriptProp.CanWrite}",
                        Signature = null
                    },
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "ScriptValue",
                        Kind = "Info",
                        Type = script == null ? "null" : (script.GetType().FullName ?? script.GetType().Name),
                        Signature = null
                    }
                };

                if (script != null)
                {
                    members.AddRange(_session.DescribeMembers(script, Math.Max(10, Math.Min(2000, maxMembers))));
                    try
                    {
                        var infos = script.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(script, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new ModelContextProtocol.ObjectMember
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
                    members.AddRange(_session.DescribeMembers(handler, Math.Max(10, Math.Min(2000, maxMembers))));
                    try
                    {
                        var infos = handler.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(handler, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new ModelContextProtocol.ObjectMember
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

                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "OK",
                    Meta = ResponseMeta.Unstamped(true, ("operationSuccess", true), ("exists", true)),
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    TypeName = script == null ? scriptProp?.PropertyType.FullName : script.GetType().FullName,
                    Members = members
                };
            }
            catch (Exception ex)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Event script read failed",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject { ["success"] = false, ["operationSuccess"] = false,
                        ["status"] = ex is PortalException pex ? pex.Code.ToString() : "ReadFailed",
                        ["error"] = (ex.InnerException ?? ex).Message },
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }
        }

        public ResponseMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath, string screenName, string buttonName, string eventType, string scriptCode, string globalDefinitionAreaScriptCode = "", bool async = false, bool syntaxCheck = false)
        {
            return _session.RunHmiStepTool("SetUnifiedHmiButtonEventScriptCode", meta =>
            {
                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType);
                var script = TryGetPropertyValue(handler, "Script");
                if (script == null)
                {
                    throw new InvalidOperationException($"Script object is null on '{buttonName}.{eventType}'. Ensure the event handler exists first.");
                }

                var setScriptCode = _session.TrySetProperty(script, "ScriptCode", scriptCode ?? string.Empty);
                var setGlobalCode = _session.TrySetProperty(script, "GlobalDefinitionAreaScriptCode", globalDefinitionAreaScriptCode ?? string.Empty);
                var setAsync = _session.TrySetProperty(script, "Async", async);

                meta["scriptType"] = script.GetType().FullName;
                meta["setScriptCode"] = setScriptCode;
                meta["setGlobalDefinitionAreaScriptCode"] = setGlobalCode;
                meta["setAsync"] = setAsync;

                // 写不进去就到此为止：ScriptCode 都没落下，再去跑 SyntaxCheck 只是拿一个
                // 已知会弄崩 V21 的调用，去检查一份根本不存在的脚本。这个判断以前排在
                // SyntaxCheck 之后，等于先冒一次崩溃风险，才发现这一步本来就该失败。
                if (!setScriptCode)
                {
                    throw new InvalidOperationException($"ScriptCode property could not be written on {script.GetType().FullName}.");
                }

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
                            meta["syntaxErrors"] = _session.ToJsonArray(syntaxErrors);
                            meta["syntaxWarnings"] = _session.ToJsonArray(syntaxWarnings);
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
                        if (ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(real))
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
            });
        }

        public ResponseMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string dynamizationType = "")
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiDynamization", meta =>
            {
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var existing = find?.Invoke(dynamizations, new object[] { propertyName });
                if (existing != null)
                {
                    meta["action"] = "exists";
                    meta["dynamizationType"] = existing.GetType().FullName;
                    meta["members"] = string.Join(" | ", _session.DescribeMembers(existing, 100).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                    return $"Dynamization for '{itemName}.{propertyName}' exists.";
                }

                var createMethods = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    .ToList();
                if (!createMethods.Any())
                {
                    throw new InvalidOperationException($"Generic Create<T>(string) not found on {dynamizations.GetType().FullName}.");
                }

                var candidates = ResolveUnifiedHmiDynamizationTypes(dynamizationType).ToList();
                meta["candidateTypes"] = string.Join(" | ", candidates.Select(t => t.FullName));
                if (!candidates.Any())
                {
                    throw new InvalidOperationException($"No dynamization type matched '{dynamizationType}'. Use ListUnifiedHmiApiTypes with nameContains='Dynamization'.");
                }

                var errors = new List<string>();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var created = createMethods[0].MakeGenericMethod(candidate).Invoke(dynamizations, new object[] { propertyName });
                        if (created == null) continue;

                        meta["action"] = "created";
                        meta["dynamizationType"] = created.GetType().FullName;
                        meta["members"] = string.Join(" | ", _session.DescribeMembers(created, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return $"Dynamization for '{itemName}.{propertyName}' created as '{created.GetType().Name}'.";
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{candidate.FullName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                meta["attemptErrors"] = string.Join(" || ", errors);
                throw new InvalidOperationException($"Unable to create dynamization for '{itemName}.{propertyName}'.");
            });
        }

        /// <summary>
        /// Unified TagDynamization: PLC address may live on property <c>Address</c>, <c>LogicalAddress</c>,
        /// or only as an engineering attribute — plain <see cref="TrySetProperty"/> often misses it.
        /// </summary>
        private bool TrySetTagDynamizationAddress(object dyn, string address)
        {
            if (string.IsNullOrWhiteSpace(address) || dyn == null) return false;
            foreach (var attr in new[] { "Address", "LogicalAddress", "ControllerTagAddress", "PlcAddress" })
            {
                if (_session.TrySetProperty(dyn, attr, address)) return true;
                if (TrySetEngineeringAttribute(dyn, attr, address)) return true;
            }

            return false;
        }

        public ResponseMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string tagName, string dataType = "Bool", string plcTag = "", string address = "")
        {
            return _session.RunHmiStepTool("BindUnifiedHmiTagDynamization", meta =>
            {
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var dyn = find?.Invoke(dynamizations, new object[] { propertyName });
                var action = "exists";
                if (dyn == null)
                {
                    var tagDynType = ResolveUnifiedHmiDynamizationTypes("TagDynamization").FirstOrDefault(t => t.Name == "TagDynamization");
                    if (tagDynType == null) throw new InvalidOperationException("TagDynamization type not found.");

                    var create = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (create == null) throw new InvalidOperationException($"Create<T>(string) not found on {dynamizations.GetType().FullName}.");

                    dyn = create.MakeGenericMethod(tagDynType).Invoke(dynamizations, new object[] { propertyName });
                    action = "created";
                }

                if (dyn == null) throw new InvalidOperationException("Dynamization create/find returned null.");
                var setTag = _session.TrySetProperty(dyn, "Tag", tagName);
                var setDataType = _session.TrySetProperty(dyn, "DataType", dataType);
                var setPlcTag = !string.IsNullOrWhiteSpace(plcTag) && _session.TrySetProperty(dyn, "PlcTag", plcTag);
                var setAddress = false;
                if (!string.IsNullOrWhiteSpace(address))
                {
                    setAddress = TrySetTagDynamizationAddress(dyn, address);
                }

                meta["action"] = action;
                meta["dynamizationType"] = dyn.GetType().FullName;
                meta["setTag"] = setTag;
                meta["setDataType"] = setDataType;
                meta["setPlcTag"] = string.IsNullOrWhiteSpace(plcTag) ? "skipped" : setPlcTag;
                meta["setAddress"] = string.IsNullOrWhiteSpace(address) ? "skipped" : setAddress;
                meta["members"] = string.Join(" | ", _session.DescribeMembers(dyn, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));

                if (!setTag)
                {
                    throw new InvalidOperationException($"Tag property could not be written on {dyn.GetType().FullName}.");
                }

                return $"Tag dynamization for '{itemName}.{propertyName}' bound to '{tagName}'.";
            });
        }

        private object ResolveHmiScreenItemOrThrow(string hmiSoftwarePath, string screenName, string itemName)
        {
            var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
            var items = TryGetPropertyValue(screen, "ScreenItems");
            if (items == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"ScreenItems collection not found on screen '{screenName}'.");
            }

            var item = HmiExactAccess.Named(items, itemName);
            if (item == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Screen item '{itemName}' not found on screen '{screenName}'.");
            }

            return item;
        }

        private object ResolveHmiButtonEventHandlerOrThrow(string hmiSoftwarePath, string screenName, string buttonName, string eventType, bool createIfMissing = true)
        {
            var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
            var handlers = button.GetType().GetProperty("EventHandlers")?.GetValue(button)
                ?? throw new PortalException(PortalErrorCode.NotFound, "EventHandlers not found on " + buttonName);
            return HmiEventAccess.Resolve(handlers, eventType, createIfMissing);
        }

        private object EnsureHmiTagTableObject(object hmiSoftware, string tagTableName)
        {
            var tagRoot = _session.TryGetHmiTagRoot(hmiSoftware);
            var table = _session.TryFindHmiTagTable(hmiSoftware, tagTableName);
            if (table != null) return table;

            var tables = _session.TryGetHmiTagTablesCollection(hmiSoftware);
            if (tables == null)
            {
                throw new InvalidOperationException($"HMI TagTables collection not found. hmiType={hmiSoftware.GetType().FullName}; tagRootType={tagRoot.GetType().FullName}; tagRootMembers={string.Join(" | ", _session.DescribeMembers(tagRoot, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"))}");
            }

            table = TryCreateNamedEngineeringObject(tables, tagTableName, out var createError);
            if (table == null)
            {
                throw new InvalidOperationException(createError ?? $"Failed to create HMI tag table '{tagTableName}'.");
            }

            return table;
        }

        private object? TryCreateNamedEngineeringObject(object collection, string name, out string? error)
        {
            error = null;
            var attempts = new List<string>();

            foreach (var method in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.GetParameters().Length))
            {
                var ps = method.GetParameters();
                var sig = $"{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.FullName + " " + p.Name))})";

                object?[]? args = null;
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    args = new object?[] { name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { name, name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsEnum)
                {
                    args = new object?[] { name, Enum.ToObject(ps[1].ParameterType, 0) };
                }
                else if (ps.Length == 2 && ps[0].ParameterType.IsEnum && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { Enum.ToObject(ps[0].ParameterType, 0), name };
                }
                else
                {
                    attempts.Add($"SKIP {sig}");
                    continue;
                }

                try
                {
                    var created = method.Invoke(collection, args);
                    if (created != null) return created;
                    attempts.Add($"NULL {sig}");
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    attempts.Add($"ERR {sig}: {msg}");
                    if (msg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var existing = _session.FindExistingByName(collection, name);
                        if (existing != null) return existing;
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add($"ERR {sig}: {ex.Message}");
                }
            }

            error = $"No supported Create overload succeeded on {collection.GetType().FullName}. Attempts: {string.Join(" | ", attempts)}";
            return null;
        }

        private static object? InvokeCreate(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                throw new InvalidOperationException(msg, tie.InnerException);
            }
        }

        private static Type? ResolveUnifiedScreenItemType(string itemType)
        {
            var key = (itemType ?? string.Empty).Trim();
            var candidates = key.Equals("Button", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiButton", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton" }
                : key.Equals("Text", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiText", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiText" }
                : key.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) || key.Equals("Lamp", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRectangle" }
                    : key.Equals("IOField", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField" }
                        : new[] { key };

            foreach (var name in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(name, throwOnError: false, ignoreCase: false);
                        if (t != null) return t;
                    }
                    catch /* swallow(probe-optional): The screen-item type may be available in another loaded assembly. */ { }
                }
            }

            return null;
        }

        private static IEnumerable<Type> ResolveUnifiedHmiDynamizationTypes(string dynamizationType)
        {
            var filter = (dynamizationType ?? string.Empty).Trim();
            var preferredNames = string.IsNullOrWhiteSpace(filter)
                ? new[]
                {
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.DiscreteDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.RangeDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.ScriptDynamization"
                }
                : filter.Contains(".")
                    ? new[] { filter }
                    : new[]
                    {
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}",
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}Dynamization",
                        filter
                    };

            foreach (var name in preferredNames)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetType(name, throwOnError: false, ignoreCase: false); } catch /* swallow(probe-optional): The dynamization type may be available in another loaded assembly. */ { }
                    if (t != null) yield return t;
                }
            }

            if (!string.IsNullOrWhiteSpace(filter) && !filter.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                    catch /* swallow(probe-optional): Assemblies whose types cannot load cannot supply dynamization candidates. */ { continue; }

                    foreach (var t in types)
                    {
                        if (t.FullName == null) continue;
                        if (!t.FullName.StartsWith("Siemens.Engineering.HmiUnified.UI.Dynamization.", StringComparison.Ordinal)) continue;
                        if (t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) yield return t;
                    }
                }
            }
        }

        private static object? CreateUnifiedScreenItem(object items, string itemName, Type? itemClrType, string itemTypeHint)
        {
            var methods = items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (itemClrType != null && m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    var created = m.MakeGenericMethod(itemClrType).Invoke(items, new object[] { itemName });
                    if (created != null) return created;
                }

                if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    foreach (var args in new[] { new object[] { itemName, itemTypeHint }, new object[] { itemTypeHint, itemName } })
                    {
                        try
                        {
                            var created = m.Invoke(items, args);
                            if (created != null) return created;
                        }
                        catch /* swallow(native-fallback): An unsupported screen-item creation overload leaves the remaining overloads available. */ { }
                    }
                }
            }

            throw new InvalidOperationException($"Unable to create screen item '{itemName}' as '{itemTypeHint}'. ResolvedType={itemClrType?.FullName ?? "null"}.");
        }

        private static object? FindPressedStateTag(object pressedStateTags, string tagName)
        {
            try
            {
                if (pressedStateTags is IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        foreach (var propName in new[] { "Tag", "TagName", "HmiTag", "HmiTagName", "Name", "TagPath" })
                        {
                            var value = TryGetPropertyValue(it, propName)?.ToString();
                            if (!string.IsNullOrWhiteSpace(value) &&
                                string.Equals(value!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                            {
                                return it;
                            }
                        }
                    }
                }
            }
            catch /* swallow(enumerate-optional): Unavailable pressed-state tag enumeration preserves the missing-tag result. */ { }

            return null;
        }

        private bool TrySetAnyProperty(object target, string value, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (_session.TrySetProperty(target, propName, value)) return true;
            }

            return false;
        }

        private bool TrySetAnyPropertyOrAttribute(object target, object? value, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                any = _session.TrySetProperty(target, propName, value) || any;
                any = TrySetEngineeringAttribute(target, propName, value) || any;
            }

            return any;
        }

        private bool TrySetAnyEnumCandidatePropertyOrAttribute(object target, IEnumerable<string> valueCandidates, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(prop.PropertyType, candidate, ignoreCase: true);
                            prop.SetValue(target, enumValue);
                            any = true;
                            break;
                        }
                        catch /* swallow(native-fallback): An unsupported enum property write leaves the next candidate available. */ { }
                    }
                }

                var oldValue = _session.TryGetEngineeringAttribute(target, propName);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(oldValue.GetType(), candidate, ignoreCase: true);
                            if (TrySetEngineeringAttribute(target, propName, enumValue))
                            {
                                any = true;
                                break;
                            }
                        }
                        catch /* swallow(native-fallback): An unsupported enum attribute write leaves the next candidate available. */ { }
                    }
                }
            }

            return any;
        }

        private string DescribeWritableEnumProperties(object target, params string[] propertyNames)
        {
            var parts = new List<string>();
            foreach (var name in propertyNames)
            {
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum)
                {
                    parts.Add($"{name}:{prop.PropertyType.FullName}=[{string.Join(",", Enum.GetNames(prop.PropertyType))}]");
                    continue;
                }

                var oldValue = _session.TryGetEngineeringAttribute(target, name);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    parts.Add($"{name}:attr:{oldValue.GetType().FullName}=[{string.Join(",", Enum.GetNames(oldValue.GetType()))}]");
                }
            }

            return string.Join(" | ", parts);
        }

        private sealed class UnifiedHmiTagBindingReadback
        {
            public string Status { get; set; } = "Failed";
            public bool Verified { get; set; }
            public string Readback { get; set; } = string.Empty;
            public string Guidance { get; set; } = string.Empty;
        }

        private UnifiedHmiTagBindingReadback ClassifyUnifiedHmiTagBinding(object tag, string connectionName, string plcTag, string address)
        {
            string Attr(params string[] names)
            {
                foreach (var name in names)
                {
                    try
                    {
                        var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanRead)
                        {
                            var v = prop.GetValue(tag)?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) return v!;
                        }
                    }
                    catch /* swallow(probe-optional): Unavailable tag properties fall through to attribute readback and other names. */ { }

                    try
                    {
                        var v = _session.TryGetEngineeringAttribute(tag, name)?.ToString();
                        if (!string.IsNullOrWhiteSpace(v)) return v!;
                    }
                    catch /* swallow(probe-optional): Unavailable tag attributes leave the remaining binding names available. */ { }
                }

                return string.Empty;
            }

            var readback = _session.SummarizeHmiObjectReadback(tag, "Connection", "AccessMode", "AddressAccessMode", "TagType", "PlcName", "ControllerName", "Station", "PlcTag", "ControllerTag", "ControllerTagName", "Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress", "DataType", "HmiDataType");
            var connection = Attr("Connection", "ConnectionName");
            var accessMode = Attr("AccessMode", "AddressAccessMode");
            var symbol = Attr("PlcTag", "ControllerTag", "ControllerTagName");
            var runtimeAddress = Attr("Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress");
            var requestedAddress = (address ?? string.Empty).Trim();
            var requestedSymbol = NormalizeControllerTagName(plcTag ?? string.Empty);
            var expectedConnection = (connectionName ?? string.Empty).Trim();

            var connectionOk = string.IsNullOrWhiteSpace(expectedConnection) ||
                string.Equals(connection, expectedConnection, StringComparison.OrdinalIgnoreCase);
            var absoluteOk = !string.IsNullOrWhiteSpace(requestedAddress) &&
                connectionOk &&
                string.Equals(runtimeAddress, requestedAddress, StringComparison.OrdinalIgnoreCase);
            var symbolicOk = !string.IsNullOrWhiteSpace(requestedSymbol) &&
                connectionOk &&
                string.Equals(NormalizeControllerTagName(symbol), requestedSymbol, StringComparison.OrdinalIgnoreCase) &&
                accessMode.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0;

            if (symbolicOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "SymbolicVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "PLC symbolic HMI tag binding read back successfully."
                };
            }

            if (absoluteOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "AbsoluteVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "Absolute-address HMI tag binding read back successfully."
                };
            }

            var status = string.IsNullOrWhiteSpace(connection) || connection.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0 || connection.IndexOf("内部", StringComparison.OrdinalIgnoreCase) >= 0
                ? "InternalOnly"
                : "Unverified";
            return new UnifiedHmiTagBindingReadback
            {
                Status = status,
                Verified = false,
                Readback = readback,
                Guidance = "Pass connectionName plus a verified PLC symbol or absolute address, then read back Connection/AccessMode/PlcTag/Address. Internal HMI tags are not accepted by the stable project-generation path."
            };
        }

        private static string NormalizeControllerTagName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
            return tagName.Replace("\"", string.Empty);
        }



        private UnifiedHmiPlcPartnerInfo ResolveUnifiedHmiPlcPartner(string plcSoftwarePath)
        {
            plcSoftwarePath ??= string.Empty;
            var info = new UnifiedHmiPlcPartnerInfo
            {
                SoftwarePath = plcSoftwarePath,
                DeviceName = FirstPathSegment(plcSoftwarePath),
                StationName = FirstPathSegment(plcSoftwarePath),
                Family = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath)
            };

            try
            {
                var sc = _session.GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                if (di != null)
                {
                    info.StationName = _session.TryGetName(di) ?? di.Name ?? info.StationName;
                    var root = GetTopDeviceItem(di);
                    if (root != null)
                    {
                        info.DeviceName = _session.TryGetName(root) ?? root.Name ?? info.DeviceName;
                        info.StationName = _session.TryGetName(root) ?? root.Name ?? info.StationName;
                        _session.FillUnifiedHmiPartnerNetworkInfo(root, info);
                    }
                }
            }
            catch /* swallow(probe-optional): Unavailable software parent metadata preserves path-derived partner values and device lookup. */
            {
            }

            if (string.IsNullOrWhiteSpace(info.NodeName))
            {
                try
                {
                    var root = _session.GetDeviceItemByPath(info.DeviceName);
                    if (root != null) _session.FillUnifiedHmiPartnerNetworkInfo(root, info);
                }
                catch /* swallow(probe-optional): Unavailable device network metadata preserves the partner values already resolved. */
                {
                }
            }

            if (string.IsNullOrWhiteSpace(info.DeviceName)) info.DeviceName = FirstPathSegment(plcSoftwarePath);
            if (string.IsNullOrWhiteSpace(info.StationName)) info.StationName = info.DeviceName;
            return info;
        }

        private static string FirstPathSegment(string path)
        {
            return (path ?? string.Empty).Trim()
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }

        private static DeviceItem? GetTopDeviceItem(DeviceItem item)
        {
            var current = item;
            while (current.Parent is DeviceItem parent)
            {
                current = parent;
            }

            return current;
        }



        private void TryConfigureUnifiedHmiConnectionPartner(object connection, UnifiedHmiPlcPartnerInfo partner)
        {
            var deviceName = string.IsNullOrWhiteSpace(partner.DeviceName) ? partner.SoftwarePath : partner.DeviceName;
            var stationName = string.IsNullOrWhiteSpace(partner.StationName) ? deviceName : partner.StationName;

            TrySetAnyPropertyOrAttribute(connection, deviceName, "Partner", "PartnerName", "DeviceName", "PlcName", "ControllerName");
            TrySetAnyPropertyOrAttribute(connection, stationName, "Station", "StationName", "ControllerStation");
            TrySetAnyPropertyOrAttribute(connection, deviceName, "Controller", "Device", "Plc", "Target");

            if (!string.IsNullOrWhiteSpace(partner.NodeName))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.NodeName, "Node", "PartnerNode", "Interface", "NetworkNode", "AccessPoint");
            }

            if (!string.IsNullOrWhiteSpace(partner.InitialAddress))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.InitialAddress, "InitialAddress", "Address", "IpAddress", "IPAddress", "PartnerAddress");
            }
        }

        /// <summary>
        /// Infer PLC CPU family from the PLC software path (device TypeIdentifier / order number).
        /// </summary>
        private string InferUnifiedPlcFamilyFromSoftwarePath(string plcSoftwarePath)
        {
            try
            {
                var sc = _session.GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                while (di != null)
                {
                    var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                    var t = tid.ToUpperInvariant();
                    // Catalog MLFB often contains spaces (e.g. "OrderNumber:6ES7 211-1BE40-0XB0/...").
                    // Compact whitespace before matching the MLFB so S7-1200 devices keep their matching driver.
                    var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                    if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71200";
                    if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71500";
                    if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7300";
                    if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7400";
                    di = di.Parent as DeviceItem;
                }

                var fromDevices = TryInferPlcFamilyFromProjectDevices(plcSoftwarePath);
                if (!string.IsNullOrEmpty(fromDevices)) return fromDevices;
            }
            catch /* swallow(probe-optional): Unavailable hardware metadata preserves the UNKNOWN PLC-family result. */
            {
            }

            return "UNKNOWN";
        }

        /// <summary>
        /// When <see cref="SoftwareContainer.Parent"/> is not a <see cref="DeviceItem"/>, CPU TypeIdentifier may still
        /// exist on nested rack/CPU items under the PLC device — walk the device tree by PLC software path head name.
        /// </summary>
        private string TryInferPlcFamilyFromProjectDevices(string plcSoftwarePath)
        {
            try
            {
                if (_session.CurrentProject?.Devices == null) return string.Empty;
                var head = (plcSoftwarePath ?? string.Empty).Trim()
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(head)) return string.Empty;

                foreach (var device in _session.CurrentProject.Devices)
                {
                    if (!device.Name.Equals(head, StringComparison.OrdinalIgnoreCase)) continue;
                    var stack = new Stack<DeviceItem>(device.DeviceItems ?? Enumerable.Empty<DeviceItem>());
                    while (stack.Count > 0)
                    {
                        var di = stack.Pop();
                        if (di == null) continue;
                        if (di.DeviceItems != null)
                        {
                            foreach (var ch in di.DeviceItems) stack.Push(ch);
                        }

                        var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                        var t = tid.ToUpperInvariant();
                        var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                        if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71200";
                        if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71500";
                        if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7300";
                        if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7400";
                    }
                }
            }
            catch /* swallow(enumerate-optional): Unavailable project device traversal leaves the PLC-family fallback unresolved. */
            {
            }

            return string.Empty;
        }

        private static object? SelectCommunicationDriverEnumValue(Type enumType, string plcFamily)
        {
            object? best = null;
            var bestScore = -1;
            foreach (var name in Enum.GetNames(enumType))
            {
                var u = name.ToUpperInvariant();
                var score = 0;
                if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                {
                    if (u.Contains("300") && !u.Contains("1500")) continue;
                    if (u.Contains("400") && !u.Contains("1500")) continue;
                    if (u.Contains("318") || u.Contains("319")) continue;
                    if (u.Contains("1200") || u.Contains("1500") || u.Contains("S712") || u.Contains("S715") || u.Contains("PLUS"))
                        score += 10;
                    if (u.Contains("UNIFIED") || u.Contains("PLUS")) score += 2;
                }
                else if (plcFamily == "S7300")
                {
                    if (u.Contains("300") || u.Contains("318") || u.Contains("319")) score += 10;
                }
                else if (plcFamily == "S7400")
                {
                    if (u.Contains("400") || u.Contains("414") || u.Contains("416")) score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = Enum.Parse(enumType, name);
                }
            }

            return bestScore > 0 ? best : null;
        }

        private void TryConfigureUnifiedDriverProperties(object connection, string plcFamily)
        {
            try
            {
                var dps = TryGetPropertyValue(connection, "DriverProperties");
                if (dps is not IEnumerable en) return;

                foreach (var dp in en)
                {
                    if (dp == null) continue;
                    var n = TryGetPropertyValue(dp, "Name")?.ToString() ?? _session.TryGetName(dp) ?? string.Empty;
                    var nu = n.ToUpperInvariant();
                    if (nu.Contains("DRIVER") || nu.Contains("FAMILY") || nu.Contains("CPU") || nu.Contains("CONTROLLER"))
                    {
                        if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                        {
                            _session.TrySetProperty(dp, "Value", "SIMATIC S7-1200/1500");
                            TrySetEngineeringAttribute(dp, "Value", "SIMATIC S7-1200/1500");
                        }
                    }
                }
            }
            catch /* swallow(native-fallback): Optional driver-property writes retain the existing communication-driver fallbacks. */
            {
            }
        }

        /// <summary>
        /// WinCC Unified HMI connection: pick CommunicationDriver enum / attribute that matches the PLC hardware.
        /// </summary>
        private void TryConfigureUnifiedHmiCommunicationDriver(object connection, string plcSoftwarePath)
        {
            var plcFamily = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath);

            try
            {
                var prop = connection.GetType().GetProperty("CommunicationDriver", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    var ev = SelectCommunicationDriverEnumValue(prop.PropertyType, plcFamily);
                    if (ev != null)
                    {
                        prop.SetValue(connection, ev);
                        return;
                    }
                }
            }
            catch /* swallow(native-fallback): An unsupported enum property write falls through to engineering attribute configuration. */
            {
            }

            // CommunicationDriver is commonly exposed as an engineering attribute typed as an enum; string writes fail.
            if (TrySetUnifiedHmiCommunicationDriverEnum(connection, plcFamily))
            {
                return;
            }

            var driverCandidates = plcFamily switch
            {
                "S7300" => new[] { "SIMATIC S7 300/400", "SIMATIC S7-300/400", "SIMATIC S7 300", "SIMATIC S7-300" },
                "S7400" => new[] { "SIMATIC S7 400", "SIMATIC S7-400", "SIMATIC S7 300/400", "SIMATIC S7-300/400" },
                _ => new[]
                {
                    "SIMATIC S7-1200/1500",
                    "SIMATIC S7 1200/1500",
                    "SIMATIC S7-1200",
                    "SIMATIC S7 1200",
                    "SIMATIC S7-1500",
                    "SIMATIC S7 1500",
                    "S7-1200/1500",
                    "S7-1200",
                    "S7-1500",
                    "S71200",
                    "S71500"
                }
            };

            foreach (var driver in driverCandidates)
            {
                if (_session.TrySetProperty(connection, "CommunicationDriver", driver) ||
                    TrySetEngineeringAttribute(connection, "CommunicationDriver", driver))
                {
                    return;
                }
            }

            TrySetCommunicationDriverFromAttributeInfos(connection, driverCandidates);

            TryConfigureUnifiedDriverProperties(connection, plcFamily);
        }

        private void ValidateUnifiedHmiCommunicationDriver(object connection, string plcFamily)
        {
            var driver = ReadUnifiedHmiCommunicationDriver(connection);
            var normalized = (driver ?? string.Empty).ToUpperInvariant().Replace("-", "").Replace(" ", "");
            if (plcFamily == "S7300" || plcFamily == "S7400") return;

            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("HMI connection CommunicationDriver did not read back. S7-1200/S7-1500 projects must read back a 1200/1500 driver before HMI tags are created.");
            }

            if (normalized.Contains("300/400") || normalized.Contains("S7300") || normalized.Contains("S7400"))
            {
                throw new InvalidOperationException($"HMI connection CommunicationDriver read back as '{driver}', but the PLC family is {plcFamily}. Use SIMATIC S7-1200/1500 for S7-1200/S7-1500 projects.");
            }
        }

        private string ReadUnifiedHmiCommunicationDriver(object connection)
        {
            foreach (var name in new[] { "CommunicationDriver", "Driver", "Protocol" })
            {
                try
                {
                    var prop = connection.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var value = prop.GetValue(connection)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value!;
                    }
                }
                catch /* swallow(probe-optional): Unreadable driver properties fall through to engineering attribute readback. */
                {
                }

                var attr = _session.TryGetEngineeringAttribute(connection, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(attr)) return attr!;
            }

            return string.Empty;
        }

        /// <summary>
        /// Some Unified builds expose the driver only under a localized or version-specific engineering attribute name.
        /// </summary>
        private void TrySetCommunicationDriverFromAttributeInfos(object connection, string[] driverCandidates)
        {
            try
            {
                var getInfos = connection.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes);
                if (getInfos == null) return;
                var infos = getInfos.Invoke(connection, null) as System.Collections.IEnumerable;
                if (infos == null) return;
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    var n = TryGetPropertyValue(info, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var nu = n.ToUpperInvariant();
                    if (!nu.Contains("COMMUNICATIONDRIVER") && !nu.Contains("DRIVER") && !n.Contains("通信")) continue;
                    foreach (var driver in driverCandidates)
                    {
                        try
                        {
                            if (TrySetEngineeringAttribute(connection, n, driver)) return;
                        }
                        catch /* swallow(native-fallback): An unsupported driver candidate leaves the remaining candidates available. */
                        {
                        }
                    }
                }
            }
            catch /* swallow(probe-optional): Unavailable attribute metadata leaves driver validation to the existing caller. */
            {
            }
        }

        private void ApplyJsonProperties(object target, JsonObject props, JsonArray failed, string path, string typeHint = "")
        {
            foreach (var kv in props)
            {
                var schemaError = ValidateUnifiedHmiDesignProperty(typeHint, kv.Key);
                if (!string.IsNullOrEmpty(schemaError))
                {
                    failed.Add($"{path}.{kv.Key}: {schemaError}");
                    continue;
                }

                var value = JsonObjectValue(kv.Value);
                if (_session.TrySetProperty(target, kv.Key, value)) continue;
                if (TrySetEngineeringAttribute(target, kv.Key, value)) continue;
                failed.Add($"{path}.{kv.Key}: property/attribute write failed");
            }
        }

        private static string ValidateUnifiedHmiDesignProperty(string typeHint, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) return "property name is empty";
            var type = (typeHint ?? string.Empty).Trim();
            var prop = propertyName.Trim();

            if (type.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Lamp", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Equals("ForeColor", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Content", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Padding", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported on Rectangle. Use a separate HmiText item for text/foreground/font, and keep Rectangle for BackColor/BorderColor/BorderWidth.";
                }
            }

            if (type.Equals("IOField", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase))
            {
                var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BackColor", "ForeColor", "BorderColor", "BorderWidth", "Visible", "Enabled", "Name"
                };
                if (!stable.Contains(prop))
                    return "not in the stable IOField property set for generated screens. Bind runtime values with BindUnifiedHmiTagDynamization instead of ad-hoc ProcessValue properties.";
            }

            return string.Empty;
        }

        private bool TrySetEngineeringAttribute(object target, string attributeName, object? value)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var set = target.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (set == null) return false;

                object? oldValue = null;
                try { oldValue = get?.Invoke(target, new object[] { attributeName }); } catch /* swallow(probe-optional): An unreadable old attribute value leaves the supplied value uncoerced. */ { }
                var typed = oldValue == null ? value : _session.CoerceReflectionValue(value, oldValue.GetType());
                set.Invoke(target, new[] { attributeName, typed });
                return true;
            }
            catch /* swallow(native-fallback): Unsupported attribute conversion or assignment reports false to the existing caller fallback. */
            {
                return false;
            }
        }

        private static string? JsonString(JsonObject obj, string propertyName)
        {
            var node = obj[propertyName];
            if (node == null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return node.ToJsonString();
        }

        private static object? JsonObjectValue(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<double>(out var d)) return d;
                return value.ToJsonString();
            }

            return node.ToJsonString();
        }

        private static IEnumerable<string> TryGetEnumerableStrings(object target, string propertyName)
        {
            try
            {
                var value = TryGetPropertyValue(target, propertyName);
                if (value is IEnumerable en)
                {
                    foreach (var item in en)
                    {
                        if (item != null) yield return item.ToString() ?? string.Empty;
                    }
                }
            }
            finally
            {
            }
        }
    }
}
