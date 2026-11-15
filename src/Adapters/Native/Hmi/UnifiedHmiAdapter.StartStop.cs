using TiaMcp.Adapters.Native.Engineering;
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

        public HmiMessage EnsureStartStopUnifiedHmi(
            string hmiSoftwarePath,
            string screenName = "Main",
            string tagTableName = UnifiedHmiDefaults.DefaultTagTableName,
            string plcName = "PLC_1",
            string connectionName = "HMI_Connection_1")
        {
            EngineeringInputValidation.NameOrAbsolutePath(screenName);
            EngineeringInputValidation.NameOrAbsolutePath(tagTableName);
            var meta = HmiMeta.Basic(false);
            using var journal = new EngineeringWriteJournal();

            var steps = new HmiArray();
            meta["steps"] = steps;

            void Step(string name, bool ok, string? detail = null, bool informational = false)
            {
                var o = new HmiObject
                {
                    ["step"] = name,
                    ["ok"] = ok
                };
                if (informational) o["informational"] = true;
                if (!string.IsNullOrWhiteSpace(detail)) o["detail"] = detail;
                steps.Add(o);
            }

            try
            {
                meta["mayHaveChanged"] = false;
                var totalDeadline = DateTime.UtcNow.AddSeconds(25); // hard timeout for this tool
                bool TimedOut() => DateTime.UtcNow > totalDeadline;

                if (_session.IsProjectNull())
                {
                    meta["v4Rejection"] = "PROJECT_NOT_BOUND";
                    Step("precheck", false, "Project is null");
                    return new HmiMessage { Message = "Project is null", Meta = meta };
                }

                var sc = _session.GetSoftwareContainer(hmiSoftwarePath);
                if (sc?.Software == null)
                {
                    meta["v4Rejection"] = "NOT_FOUND";
                    Step("resolveSoftware", false, $"SoftwareContainer not found at '{hmiSoftwarePath}'");
                    return new HmiMessage { Message = "HMI software not found", Meta = meta };
                }

                var sw = sc.Software;
                Step("resolveSoftware", true, sw.GetType().FullName);

                // Resolve screen + tag table
                var screen = HmiExactAccess.Screen(sw, screenName);
                if (screen == null)
                {
                    meta["v4Rejection"] = "NOT_FOUND";
                    Step("findScreen", false, $"Screen '{screenName}' not found");
                    return new HmiMessage { Message = "Screen not found", Meta = meta };
                }
                Step("findScreen", true, screen.GetType().FullName);

                var tagTable = _session.TryFindHmiTagTable(sw, tagTableName);
                if (tagTable == null)
                {
                    meta["v4Rejection"] = "NOT_FOUND";
                    Step("findTagTable", false, $"TagTable '{tagTableName}' not found");
                    return new HmiMessage { Message = "Tag table not found", Meta = meta };
                }
                Step("findTagTable", true, tagTable.GetType().FullName);

                // Verify the preconfigured connection before attempting any tag or UI writes.
                try
                {
                    var connDesc = EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                    Step("ensureUnifiedHmiConnection", true, connDesc.Message ?? "ok");
                }
                catch (Exception ex)
                {
                    Step("ensureUnifiedHmiConnection", false, HmiMeta.Failure(meta, ex));
                    meta["v4Rejection"] = ex.GetBaseException() is NotSupportedException ? "UNSUPPORTED_CAPABILITY" : "PRECONDITION_FAILED";
                    return new HmiMessage { Message = "Connection step failed; no tags or screen items were attempted.", Meta = meta };
                }

                var connName = string.IsNullOrWhiteSpace(connectionName) ? "HMI_Connection_1" : connectionName.Trim();
                Step("resolveConnection", true, connName);

                // Ensure tags in HMI tag table
                var tagsComp = tagTable.GetType().GetProperty("Tags")?.GetValue(tagTable);
                if (tagsComp == null)
                {
                    Step("resolveTagComposition", false, "tagTable.Tags not found");
                    return new HmiMessage { Message = "Tag composition not found", Meta = meta };
                }
                Step("resolveTagComposition", true, tagsComp.GetType().FullName);

                string[] tagNames = new[] { "StartPB", "StopPB", "EStop", "RunOut" };
                foreach (var tn in tagNames)
                {
                    if (TimedOut())
                    {
                        Step("timeout", false, "Timeout during tag ensure");
                        return new HmiMessage { Message = "Timeout", Meta = meta };
                    }
                    try
                    {
                        var existed = HmiExchangePolicy.Find(TryGetPropertyValue(sw, "Tags")
                            ?? throw new NotSupportedException("Unified Tags unavailable."), tn) != null;
                        var tagObj = HmiExchangePolicy.EnsureTag(sw, tagTable, tn, () => meta["mayHaveChanged"] = true);
                        var writeResults = new HmiArray();
                        BindUnifiedHmiTagToPlcSymbol(tagObj, connName, plcName, tn, "Bool", writeResults);
                        Step($"tag:{tn}", true, existed ? "exists" : "created");
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
                    return new HmiMessage { Message = "ScreenItems not found", Meta = meta };
                }
                Step("resolveScreenItems", true, itemsComp.GetType().FullName);

                // Dump all Create* method signatures on ScreenItems composition so we see what's really there.
                var itemsType = itemsComp.GetType();
                var createSigs = itemsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m =>
                            $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name))}) -> {m.ReturnType.FullName}")
                    .ToArray();
                Step("screenItems.CreateSignatures", true, string.Join(" | ", createSigs));

                var tButton = ResolveUnifiedScreenItemType("Button");
                var tIOField = ResolveUnifiedScreenItemType("IOField");
                var tRectangle = ResolveUnifiedScreenItemType("Rectangle");
                var tLabel = ResolveUnifiedScreenItemType("Text");
                object CreateItem(string name, Type?[] types, string[] hints)
                {
                    var existing = HmiExchangePolicy.Find(itemsComp, name);
                    if (existing != null) return existing;
                    var type = types[0] ?? throw new NotSupportedException("Documented screen item type unavailable: " + hints[0]);
                    meta["mayHaveChanged"] = true;
                    return CreateUnifiedScreenItem(itemsComp, name, type, hints[0])
                        ?? throw new InvalidOperationException("Screen item creation returned null: " + name);
                }
                void WriteRequiredProperty(object target, string property, object value)
                {
                    if (property == "Item")
                        throw new NotSupportedException("Multilingual captions require an explicit culture; use SetUnifiedMultilingualProperty. No Item " +
                            "property is written.");
                    var edits = UnifiedUiModelLogic.PrepareNested(target.GetType(), new HmiObject {
                        [property] = value is Color color ? UnifiedUiModelLogic.ColorText(color) : value });
                    UnifiedUiModelLogic.ApplyNested(target, edits, meta);
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
                        WriteRequiredProperty(hdrBar, "Left", 0);
                        WriteRequiredProperty(hdrBar, "Top", 0);
                        WriteRequiredProperty(hdrBar, "Width", (uint)1280);
                        WriteRequiredProperty(hdrBar, "Height", (uint)72);
                        WriteRequiredProperty(hdrBar, "BackColor", ColorTranslator.FromHtml("#1E3A5F"));
                        WriteRequiredProperty(hdrBar, "BorderWidth", (uint)0);
                    }

                    if (hdrTitle != null)
                    {
                        WriteRequiredProperty(hdrTitle, "Left", 24);
                        WriteRequiredProperty(hdrTitle, "Top", 12);
                        WriteRequiredProperty(hdrTitle, "Width", (uint)900);
                        WriteRequiredProperty(hdrTitle, "Height", (uint)48);
                        var txtH = hdrTitle.GetType().GetProperty("Text")?.GetValue(hdrTitle);
                        if (txtH != null)
                        {
                            WriteRequiredProperty(txtH, "Item", "MCP 验证 · 起停与状态");
                            WriteRequiredProperty(txtH, "HorizontalAlignment", "Left");
                        }

                        WriteRequiredProperty(hdrTitle, "ForeColor", Color.White);
                    }

                    if (btnStart != null)
                    {
                        WriteRequiredProperty(btnStart, "Left", 48);
                        WriteRequiredProperty(btnStart, "Top", 110);
                        WriteRequiredProperty(btnStart, "Width", (uint)200);
                        WriteRequiredProperty(btnStart, "Height", (uint)72);
                        WriteRequiredProperty(btnStart, "BackColor", ColorTranslator.FromHtml("#2E7D32"));
                        var txt = btnStart.GetType().GetProperty("Text")?.GetValue(btnStart);
                        if (txt != null)
                        {
                            WriteRequiredProperty(txt, "Item", "启动 (Start)");
                            WriteRequiredProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (btnStop != null)
                    {
                        WriteRequiredProperty(btnStop, "Left", 48);
                        WriteRequiredProperty(btnStop, "Top", 200);
                        WriteRequiredProperty(btnStop, "Width", (uint)200);
                        WriteRequiredProperty(btnStop, "Height", (uint)72);
                        WriteRequiredProperty(btnStop, "BackColor", ColorTranslator.FromHtml("#C62828"));
                        var txt = btnStop.GetType().GetProperty("Text")?.GetValue(btnStop);
                        if (txt != null)
                        {
                            WriteRequiredProperty(txt, "Item", "停止 (Stop)");
                            WriteRequiredProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (lampRun != null)
                    {
                        WriteRequiredProperty(lampRun, "Left", 300);
                        WriteRequiredProperty(lampRun, "Top", 110);
                        WriteRequiredProperty(lampRun, "Width", (uint)120);
                        WriteRequiredProperty(lampRun, "Height", (uint)120);
                        WriteRequiredProperty(lampRun, "BackColor", ColorTranslator.FromHtml("#B0BEC5"));
                        WriteRequiredProperty(lampRun, "BorderWidth", (uint)2);
                    }

                    Step("ui:layout", true);
                }
                catch (Exception ex)
                {
                    Step("ui:layout", false, ex.InnerException?.Message ?? ex.Message);
                }

                // V21 exposes exactly PressedStateTags.Create() and HmiPressedStateTagPart.Tag.
                void TryBindPressedTag(object? button, string table, string tagName)
                {
                    if (button == null) return;
                    try
                    {
                        var pressed = TryGetPropertyValue(button, "PressedStateTags")
                            ?? throw new NotSupportedException("PressedStateTags is available only in the V21 SDK; no alternative property is guessed.");
                        var existing = FindPressedStateTag(pressed, tagName);
                        if (existing == null)
                        {
                            var create = pressed.GetType().GetMethod("Create", Type.EmptyTypes)
                                ?? throw new NotSupportedException("PressedStateTags.Create() unavailable.");
                            meta["mayHaveChanged"] = true;
                            existing = InvokeCreate(create, pressed, Array.Empty<object>())
                                ?? throw new InvalidOperationException("PressedStateTags.Create returned null.");
                        }
                        HmiExchangePolicy.WriteScalars(existing, new Dictionary<string, object?> { ["Tag"] = tagName },
                            () => meta["mayHaveChanged"] = true);
                        Step("bind:" + _session.TryGetName(button), true, tagName);
                    }
                    catch (Exception error) { Step("bind:" + _session.TryGetName(button), false, error.GetBaseException().Message); }
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
                            Step("dyn:LAMP_Run", false, "Not implemented yet (see CreateSignatures)", informational: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Step("dyn:LAMP_Run", false, ex.InnerException?.Message ?? ex.Message);
                }

                // envelope: legacy-single-verdict
                HmiExchangePolicy.CompleteSteps(meta, steps);
                return new HmiMessage
                {
                    Message = "Unified HMI start/stop skeleton created (best-effort).",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                Step("exception", false, HmiMeta.Failure(meta, ex));
                return new HmiMessage { Message = "Failed creating HMI skeleton", Meta = meta };
            }
            finally
            {
                if (meta["operationSuccess"] is true) journal.Commit();
                else journal.Rollback(meta);
                meta["mayHaveChanged"] = meta["rollbackCompleted"] is true ? false : _session.MayHaveChanged || meta["mayHaveChanged"] is true;
            }

        }
    }
}
#endif
