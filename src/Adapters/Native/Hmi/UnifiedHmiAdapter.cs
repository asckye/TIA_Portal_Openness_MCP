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
        private readonly HmiSession _session;

        public UnifiedHmiAdapter(HmiSession session) => _session = session;

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

            var steps = new HmiArray();
            meta["steps"] = steps;

            void Step(string name, bool ok, string? detail = null)
            {
                var o = new HmiObject
                {
                    ["step"] = name,
                    ["ok"] = ok
                };
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
                            Step("dyn:LAMP_Run", false, "Not implemented yet (see CreateSignatures)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Step("dyn:LAMP_Run", false, ex.InnerException?.Message ?? ex.Message);
                }

                // envelope: legacy-single-verdict
                meta["success"] = steps.All(step => step?.Field("ok") is true);
                meta["operationSuccess"] = meta["success"];
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
            finally { meta["mayHaveChanged"] = _session.MayHaveChanged || meta["mayHaveChanged"] is true; }

        }

        public HmiMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath, string screenName, uint width = 0, uint height = 0)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiScreen", meta => _session.Capture(meta, _ =>
            {
                EngineeringInputValidation.NameOrAbsolutePath(screenName);
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var screens = TryGetPropertyValue(sw, "Screens");
                if (screens == null) throw new InvalidOperationException("HMI Screens collection not found.");

                object? screen = null;
                try { screen = HmiExactAccess.Screen(sw, screenName); }
                catch (PlcSoftwareException error) when (error.Code == "NotFound") { /* swallow(native-fallback): an absent screen permits creation only in the resolved existing parent */ }
                var action = "exists";
                if (screen == null)
                {
                    var createName = screenName;
                    if (screenName.StartsWith("/", StringComparison.Ordinal))
                    {
                        int slash = screenName.LastIndexOf('/');
                        var group = HmiExchangePolicy.Folder(sw, screenName.Substring(0, slash), "ScreenGroups");
                        screens = TryGetPropertyValue(group, "Screens") ?? throw new PlcSoftwareException("NotFound", "Screens unavailable.");
                        createName = Uri.UnescapeDataString(screenName.Substring(slash + 1));
                    }
                    var mCreate = screens.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (mCreate == null) throw new InvalidOperationException($"Create(string) not found on {screens.GetType().FullName}.");
                    meta["mayHaveChanged"] = true;
                    screen = InvokeCreate(mCreate, screens, new object[] { createName });
                    action = "created";
                }

                if (screen == null) throw new InvalidOperationException("Screen create/find returned null.");
                var dimensions = new Dictionary<string, object?>();
                if (width > 0) dimensions["Width"] = width;
                if (height > 0) dimensions["Height"] = height;
                HmiExchangePolicy.WriteScalars(screen, dimensions, () => meta["mayHaveChanged"] = true);

                meta["action"] = action;
                meta["screenType"] = screen.GetType().FullName;
                return $"HMI screen '{screenName}' {action}.";
            }));
        }

        public HmiDescribe EnsureUnifiedHmiConnection(string hmiSoftwarePath, string connectionName = "HMI_Connection_1", string plcName = "PLC_1")
        {
            if (string.IsNullOrWhiteSpace(hmiSoftwarePath) || string.IsNullOrWhiteSpace(connectionName) || string.IsNullOrWhiteSpace(plcName))
                throw new ArgumentException("Explicit nonempty hmiSoftwarePath, connectionName and plcName required.");
            var sw = _session.ExactUnifiedRoot(hmiSoftwarePath);
            var connections = TryGetPropertyValue(sw, "Connections") ?? throw new NotSupportedException("Unified Connections unavailable.");
            var connection = HmiExchangePolicy.Find(connections, connectionName);
            if (connection == null)
                throw new NotSupportedException("Automatic Unified connection creation requires verified CommunicationDriver and InitialAddress values. " +
                    "Configure the connection in TIA or use ManageUnifiedEngineeringObject with documented properties, then read it back. DriverProperties " +
                    "and Partner/Station/Node are not written; driver-specific values require VM confirmation.");
            var partner = _session.GetSoftwareContainer(plcName)?.Parent as DeviceItem
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC software partner not found: " + plcName);
            var expectedPartner = partner.Name;
            HmiExchangePolicy.RequireConnection(connection, expectedPartner);
            return new HmiDescribe {
                ObjectKind = "HmiConnection", ObjectPath = hmiSoftwarePath + ":" + connectionName,
                TypeName = connection.GetType().FullName, Members = _session.DescribeMembers(connection, 220),
                Message = "Existing connection verified without writes. " + _session.SummarizeHmiObjectReadback(connection,
                    "Name", "CommunicationDriver", "Partner", "Station", "Node", "InitialAddress")
            };
        }

        public HmiMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath, string screenName, string itemName, string itemType = "Button", int left = 0,
                int top = 0, uint width = 120, uint height = 40, string text = "")
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiScreenItem", meta => _session.Capture(meta, _ =>
            {
                HmiExchangePolicy.RequireLegacyText(text);
                var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems");
                if (items == null) throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var itemClrType = ResolveUnifiedScreenItemType(itemType);
                var item = _session.FindExistingByName(items, itemName);
                if (item != null && item.GetType() != itemClrType)
                    throw new InvalidOperationException("Type conflict for " + itemName + ": existing " + item.GetType().FullName + ", requested " + itemClrType
                        .FullName);
                var action = "exists";
                if (item == null)
                {
                    meta["mayHaveChanged"] = true;
                    item = CreateUnifiedScreenItem(items, itemName, itemClrType, itemType);
                    action = "created";
                }

                if (item == null) throw new InvalidOperationException("Screen item create/find returned null.");
                HmiExchangePolicy.WriteScalars(item, new Dictionary<string, object?> {
                    ["Left"] = left, ["Top"] = top, ["Width"] = width, ["Height"] = height
                }, () => meta["mayHaveChanged"] = true);

                meta["action"] = action;
                meta["itemType"] = item.GetType().FullName;
                return $"HMI screen item '{itemName}' {action}.";
            }));
        }

        public HmiMessage ReadUnifiedHmiTexts(string hmiSoftwarePath, string screenName, string itemName, string textProperty)
        {
            return _session.RunHmiStepTool("GetUnifiedHmiTexts", meta =>
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

        public HmiMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath, string screenName, string designJson, bool strict = true)
        {
            return _session.RunHmiStepTool("ApplyUnifiedHmiScreenDesign", meta =>
            {
                if (string.IsNullOrWhiteSpace(designJson))
                {
                    throw new InvalidOperationException("designJson is empty.");
                }

                var root = UnifiedHmiAccess.Parse(designJson) as HmiObject
                    ?? throw new InvalidOperationException("designJson root must be a JSON object.");

                RejectDesignRenames(root);
                var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems")
                    ?? throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var writeResults = new HmiArray(); meta["writeResults"] = writeResults;
                var changed = new HmiArray();
                var itemResults = new HmiArray();
                meta["itemResults"] = itemResults;
                var failed = new HmiArray();
                var textReadback = new HmiArray();
                meta["textReadback"] = textReadback;
                meta["persistence"] = "Not saved by this tool; partial changes may remain in the open project on failure.";

                int screenWriteCount = 0;
                if (root["screen"] is HmiObject screenProps)
                {
                    if (screenProps.Count > 0) meta["mayHaveChanged"] = true;
                    ApplyJsonProperties(screen, screenProps, failed, writeResults, "screen");
                    screenWriteCount = writeResults.Count;
                }

                var itemArray = root["items"] as HmiArray
                    ?? throw new InvalidOperationException("designJson.items must be an array.");

                foreach (var itemNode in itemArray.OfType<HmiObject>())
                {
                    var name = HmiString(itemNode, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        failed.Add("item without name skipped");
                        continue;
                    }

                    var failuresBeforeItem = failed.Count;
                    // envelope: legacy-verdict-last
                    var itemStatus = new HmiObject { ["name"] = name, ["created"] = false, ["mayHaveChanged"] = false, ["success"] = false };
                    itemResults.Add(itemStatus);
                    try
                    {
                        var hasText = UnifiedMultilingualText.ReadRequest(itemNode, out var text);
                        if (hasText) System.Globalization.CultureInfo.GetCultureInfo(HmiString(itemNode, "culture") ?? "zh-CN");
                        var typeHint = HmiString(itemNode, "type");
                        if (string.IsNullOrWhiteSpace(typeHint)) typeHint = "Rectangle";

                        var itemClrType = ResolveUnifiedScreenItemType(typeHint!);
                        var item = _session.FindExistingByName(items, name!);
                        if (item != null && item.GetType() != itemClrType)
                            throw new InvalidOperationException("Type conflict for " + name + ": existing " + item.GetType().FullName + ", requested "
                                + itemClrType.FullName);
                        var action = "updated";
                        if (item == null)
                        {
                            meta["mayHaveChanged"] = true;
                            item = CreateUnifiedScreenItem(items, name!, itemClrType, typeHint!);
                            action = "created";
                            itemStatus["created"] = item != null;
                            itemStatus["mayHaveChanged"] = true;
                        }

                        if (item == null) throw new InvalidOperationException($"Create/find returned null for '{name}'.");

                        if (hasText)
                            UnifiedMultilingualText
                                .Validate(item, HmiString(itemNode, "textProperty") is string tp && !string.IsNullOrWhiteSpace(tp) ? tp : "Text",
                                HmiString(itemNode, "culture") ?? "zh-CN");

                        itemStatus["mayHaveChanged"] = true;
                        meta["mayHaveChanged"] = true;
                        if (itemNode["left"] != null)
                            ApplyJsonProperties(item, new HmiObject { ["Left"] = itemNode["left"] }, failed, writeResults, name!, allowAttributeFallback
                                : false);
                        if (itemNode["top"] != null)
                            ApplyJsonProperties(item, new HmiObject { ["Top"] = itemNode["top"] }, failed, writeResults, name!, allowAttributeFallback: false);
                        if (itemNode["width"] != null)
                            ApplyJsonProperties(item, new HmiObject { ["Width"] = itemNode["width"] }, failed, writeResults, name!, allowAttributeFallback
                                : false);
                        if (itemNode["height"] != null)
                            ApplyJsonProperties(item, new HmiObject { ["Height"] = itemNode["height"] }, failed, writeResults, name!, allowAttributeFallback
                                : false);

                        if (itemNode["properties"] is HmiObject props)
                        {
                            ApplyJsonProperties(item, props, failed, writeResults, name!, typeHint ?? string.Empty);
                        }

                        if (hasText)
                        {
                            var textTarget = HmiString(itemNode, "textProperty");
                            if (string.IsNullOrWhiteSpace(textTarget)) textTarget = "Text";
                            var readback = UnifiedMultilingualText.WriteDetailed(item, textTarget!, text, HmiString(itemNode, "culture") ?? "zh-CN");
                            readback["name"] = name;
                            readback["languages"] = readback["after"]?.DeepClone();
                            textReadback.Add(readback);
                            if (!readback["verified"]!.GetValue<bool>())
                                throw new InvalidOperationException(readback["error"]?.ToString() ?? "Text verification failed.");
                        }

                        if (itemNode["font"] is HmiObject font)
                        {
                            var fontPart = TryGetPropertyValue(item, "Font");
                            if (fontPart != null) ApplyJsonProperties(fontPart, font, failed, writeResults, name + ".Font");
                            else failed.Add($"{name}.Font: part not found");
                        }

                        if (itemNode["content"] is HmiObject content)
                        {
                            var contentPart = TryGetPropertyValue(item, "Content");
                            if (contentPart != null) ApplyJsonProperties(contentPart, content, failed, writeResults, name + ".Content");
                            else failed.Add($"{name}.Content: part not found");
                        }

                        if (itemNode["padding"] is HmiObject padding)
                        {
                            var paddingPart = TryGetPropertyValue(item, "Padding");
                            if (paddingPart != null) ApplyJsonProperties(paddingPart, padding, failed, writeResults, name + ".Padding");
                            else failed.Add($"{name}.Padding: part not found");
                        }

                        changed.Add($"{action}:{name}:{item.GetType().Name}");
                        itemStatus["success"] = failed.Count == failuresBeforeItem;
                    }
                    catch (Exception ex)
                    {
                        itemStatus["error"] = (ex.InnerException ?? ex).Message;
                        failed
                            .Add($"{name} (textProperty={HmiString(itemNode, "textProperty") ?? "Text"}, culture={HmiString(itemNode, "culture") ?? "zh-CN"}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                meta["changed"] = changed;
                meta["changedMeaning"] =
                    "Items that reached the end of processing; not a guarantee of no other changes. Inspect itemResults and textReadback, including on " +
                    "failure.";
                meta["failed"] = failed;
                meta["strict"] = strict;
                meta["operationSuccess"] = failed.Count == 0;
                if (strict && failed.Count > 0)
                {
                    throw new InvalidOperationException($"Unified HMI design apply had {failed.Count} failed writes: {string.Join(" | ", failed.Select(x => x?.ToString() ?? string.Empty))}");
                }
                return HmiExchangePolicy.UnifiedDesignSummary(screenName, writeResults, screenWriteCount, changed.Count, failed.Count);
            });
        }

        public HmiMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath, string screenName, string buttonName, string tagName)
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
                    meta["mayHaveChanged"] = true;
                    existing = InvokeCreate(mCreate, pressedStateTags, Array.Empty<object>());
                    action = "created";
                }

                if (existing == null) throw new InvalidOperationException("Pressed-state part create/find returned null.");
                HmiExchangePolicy.WriteScalars(existing, new Dictionary<string, object?> { ["Tag"] = tagName }, () => meta["mayHaveChanged"] = true);
                var bound = true;

                meta["action"] = action;
                meta["pressedStateTagPartType"] = existing.GetType().FullName;
                meta["propertyBound"] = bound;
                if (!bound)
                {
                    meta["availableProperties"] = string
                        .Join(", ", existing.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Select(p => $"{p.Name}:{p.PropertyType.Name}"));
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

        public HmiMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath, string screenName, string buttonName, string eventType)
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
                    throw new ArgumentException("Invalid event name: " + eventType + " (valid for " + enumType.Name + ": " + string
                            .Join("/", Enum.GetNames(enumType)) + "; a WinCC Unified button click is 'Tapped').");
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
                    meta["mayHaveChanged"] = true;
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

        public HmiMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName,
                string dynamizationType = "")
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiDynamization", meta => _session.Capture(meta, _ =>
            {
                var candidates = ResolveUnifiedHmiDynamizationTypes(dynamizationType).ToList();
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var existing = find?.Invoke(dynamizations, new object[] { propertyName });
                if (existing != null)
                {
                    if (existing.GetType() != candidates.Single())
                        throw new NotSupportedException("Existing dynamization has a different documented kind; no replacement was attempted.");
                    meta["action"] = "exists";
                    meta["dynamizationType"] = existing.GetType().FullName;
                    meta["members"] = string.Join(" | ", _session.DescribeMembers(existing, 100).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                    return $"Dynamization for '{itemName}.{propertyName}' exists.";
                }

                var createMethods = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0]
                            .ParameterType == typeof(string))
                    .ToList();
                if (!createMethods.Any())
                {
                    throw new InvalidOperationException($"Generic Create<T>(string) not found on {dynamizations.GetType().FullName}.");
                }

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
                        meta["mayHaveChanged"] = true;
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
            }));
        }

        /// <summary>
        /// Unified TagDynamization: PLC address may live on property <c>Address</c>, <c>LogicalAddress</c>,
        /// or only as an engineering attribute — plain <see cref="TrySetProperty"/> often misses it.
        /// </summary>
        private bool TrySetTagDynamizationAddress(object dyn, string address)
        {
            HmiExchangePolicy.WriteScalars(dyn, new Dictionary<string, object?> { ["Address"] = address }, () => { });
            return true;
        }

        public HmiMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string tagName,
                string dataType = "Bool", string plcTag = "", string address = "")
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
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0]
                                .ParameterType == typeof(string));
                    if (create == null) throw new InvalidOperationException($"Create<T>(string) not found on {dynamizations.GetType().FullName}.");

                    meta["mayHaveChanged"] = true;
                    dyn = create.MakeGenericMethod(tagDynType).Invoke(dynamizations, new object[] { propertyName });
                    action = "created";
                }

                if (dyn == null) throw new InvalidOperationException("Dynamization create/find returned null.");
                var results = new HmiArray(); meta["writeResults"] = results;
                void Write(string property, string value, Func<bool> setter)
                {
                    meta["mayHaveChanged"] = true;
                    var result = UnifiedUiModelLogic.VerifiedWrite(dyn, property, value, setter);
                    results.Add(result); meta["set" + property] = result["trySetResult"];
                }
                Write("Tag", tagName, () => _session.TrySetProperty(dyn, "Tag", tagName));
                Write("DataType", dataType, () => _session.TrySetProperty(dyn, "DataType", dataType));
                if (!string.IsNullOrWhiteSpace(plcTag)) Write("PlcTag", plcTag, () => _session.TrySetProperty(dyn, "PlcTag", plcTag));
                if (!string.IsNullOrWhiteSpace(address)) Write("Address", address, () => TrySetTagDynamizationAddress(dyn, address));
                meta["action"] = action; meta["dynamizationType"] = dyn.GetType().FullName;
                if (string.IsNullOrWhiteSpace(plcTag)) meta["setPlcTag"] = "skipped";
                if (string.IsNullOrWhiteSpace(address)) meta["setAddress"] = "skipped";
                meta["members"] = string.Join(" | ", _session.DescribeMembers(dyn, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                meta["after"] = UnifiedRequestScalar.Read(dyn);
                if (results.Any(r => r!.Field("verified") is not true))
                    throw new InvalidOperationException("Tag dynamization write/readback failed; inspect writeResults. Partial edits are not rolled back.");

                return $"Tag dynamization for '{itemName}.{propertyName}' bound to '{tagName}'.";
            });
        }

        private object ResolveHmiScreenItemOrThrow(string hmiSoftwarePath, string screenName, string itemName)
        {
            var screen = _session.ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
            var items = TryGetPropertyValue(screen, "ScreenItems");
            if (items == null)
            {
                throw new PlcSoftwareException("NotFound", $"ScreenItems collection not found on screen '{screenName}'.");
            }

            var item = HmiExactAccess.Named(items, itemName);
            if (item == null)
            {
                throw new PlcSoftwareException("NotFound", $"Screen item '{itemName}' not found on screen '{screenName}'.");
            }

            return item;
        }

        private object EnsureHmiTagTableObject(object hmiSoftware, string tagTableName)
            => HmiExchangePolicy.EnsureTagTable(hmiSoftware, tagTableName, _session.MarkHmiChanged);

        private object? TryCreateNamedEngineeringObject(object collection, string name, out string? error)
        {
            error = null;
            var create = collection.GetType().GetMethod("Create", new[] { typeof(string) });
            if (create == null) throw new NotSupportedException("Documented Create(string) unavailable on " + collection.GetType().FullName);
            _session.MarkHmiChanged();
            return InvokeCreate(create, collection, new object[] { name });
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
            var name = (itemType ?? string.Empty).Trim();
            var fullName = name == "Button" || name == "HmiButton" ? "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton"
                : name == "Text" || name == "HmiText" ? "Siemens.Engineering.HmiUnified.UI.Shapes.HmiText"
                : name == "Rectangle" || name == "Lamp" || name == "HmiRectangle" ? "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle"
                : name == "IOField" || name == "HmiIOField" ? "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField" : name;
            var type = typeof(HmiSoftware).Assembly.GetType(fullName, false, false);
            if (type == null || type.IsAbstract || !typeof(Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase).IsAssignableFrom(type))
                throw new NotSupportedException("Documented concrete Unified screen item type unavailable: " + itemType);
            return type;
        }

        private static IEnumerable<Type> ResolveUnifiedHmiDynamizationTypes(string dynamizationType)
        {
            var requested = string.IsNullOrWhiteSpace(dynamizationType) ? "Tag" : dynamizationType.Trim();
            var match = UnifiedUiModelLogic.DynamizationKinds.SingleOrDefault(p => requested == p.Key
                || requested == p.Value || requested == p.Value.Substring(p.Value.LastIndexOf('.') + 1));
            if (match.Value == null) throw new NotSupportedException("Unknown documented dynamization kind: " + requested);
            var type = typeof(HmiSoftware).Assembly.GetType(match.Value, false, false)
                ?? throw new NotSupportedException("Dynamization kind is unavailable in the selected SDK: " + match.Key);
            yield return type;
        }

        private static object? CreateUnifiedScreenItem(object items, string itemName, Type? itemClrType, string itemTypeHint)
        {
            if (itemClrType == null) throw new NotSupportedException("Screen item type unavailable: " + itemTypeHint);
            var create = items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            return create.MakeGenericMethod(itemClrType).Invoke(items, new object[] { itemName });
        }

        private static object? FindPressedStateTag(object pressedStateTags, string tagName)
        {
            var matches = HmiExchangePolicy.Items(pressedStateTags).Where(item =>
                string.Equals(TryGetPropertyValue(item, "Tag") as string, tagName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous pressed-state tag: " + tagName);
            return matches.SingleOrDefault();
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

        private static void RejectDesignRenames(HmiObject root, bool item = false)
        {
            foreach (var pair in root)
            {
                if (pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase) && !(item && pair.Key == "name"))
                    throw new ArgumentException("Renaming through screen design is refused; use an explicit rename operation.");
                if (pair.Value is HmiObject nested) RejectDesignRenames(nested);
                else if (pair.Value is HmiArray items) foreach (var child in items.OfType<HmiObject>()) RejectDesignRenames(child, pair.Key == "items");
            }
        }

        private void ApplyJsonProperties(object target, HmiObject props, HmiArray failed, HmiArray writeResults, string path, string typeHint = "",
                bool allowAttributeFallback = true)
        {
            foreach (var kv in props)
            {
                var schemaError = ValidateUnifiedHmiDesignProperty(typeHint, kv.Key);
                if (!string.IsNullOrEmpty(schemaError))
                {
                    failed.Add($"{path}.{kv.Key}: {schemaError}");
                    continue;
                }

                var value = HmiObjectValue(kv.Value);
                var result = UnifiedUiModelLogic.VerifiedWrite(target, kv.Key, value,
                    () => _session.TrySetProperty(target, kv.Key, value), allowAttributeFallback ? () => TrySetEngineeringAttribute(target, kv.Key, value)
                        : (Func<bool>?)null);
                result["path"] = path; writeResults.Add(result);
                if (result["verified"] is not true) failed.Add($"{path}.{kv.Key}: {result["error"]}");
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
                    return "unsupported on Rectangle. Use a separate HmiText item for text/foreground/font, and keep Rectangle for " +
                        "BackColor/BorderColor/BorderWidth.";
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
                    return "not in the stable IOField property set for generated screens. Bind runtime values with BindUnifiedHmiTagDynamization instead of " +
                        "ad-hoc ProcessValue properties.";
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

        private static string? HmiString(HmiObject obj, string propertyName)
            => obj[propertyName] is string text ? text : obj[propertyName] == null ? null : UnifiedHmiAccess.SerializeValue(obj[propertyName]);
        private static object? HmiObjectValue(object? node) => node is HmiObject || node is HmiArray ? UnifiedHmiAccess.SerializeValue(node) : node;

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

#endif
