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
        private readonly HmiSession _session;

        public UnifiedHmiAdapter(HmiSession session) => _session = session;

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
                if (button == null) throw new PlcSoftwareException("NotFound", $"Screen item '{buttonName}' not found.");

                var pressedStateTags = TryGetPropertyValue(button, "PressedStateTags");
                if (pressedStateTags == null) throw new InvalidOperationException($"PressedStateTags not found on '{buttonName}'.");

                var existing = FindPressedStateTag(pressedStateTags, tagName);
                var action = "exists";
                if (existing == null)
                {
                    var mCreate = pressedStateTags.GetType().GetMethod("Create", Type.EmptyTypes);
                    if (mCreate == null) throw new InvalidOperationException($"Create() not found on {pressedStateTags.GetType().FullName}.");
                    bool tracked = false;
                    EngineeringWriteJournal.Record(hmiSoftwarePath + "/Screens/" + screenName + "/" + buttonName + "/PressedStateTags/" + tagName
                        + ": reconcile unconfirmed anonymous part creation",
                        () => { if (!tracked) throw new NotSupportedException("Create did not return an exact public object to delete safely."); });
                    meta["mayHaveChanged"] = true;
                    existing = InvokeCreate(mCreate, pressedStateTags, Array.Empty<object>());
                    if (existing != null) EngineeringWriteJournal
                        .Created(existing, hmiSoftwarePath + "/Screens/" + screenName + "/" + buttonName + "/PressedStateTags/" + tagName,
                            () => FindPressedStateTag(pressedStateTags, tagName) == null);
                    tracked = existing != null;
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
                    EngineeringWriteJournal.Created(handler, hmiSoftwarePath + "/Screens/" + screenName + "/" + buttonName + "/EventHandlers/" + eventType,
                        () => find == null ? throw new NotSupportedException("Event Find unavailable.") : find
                            .Invoke(eventHandlers, new[] { enumValue }) == null);
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
        public HmiMessage EnsureUnifiedHmiButtonAction(string software, string screen, string button, string eventType,
            string targetTag, string scriptCode, bool syntaxCheck)
            => _session.RunHmiStepTool("EnsureUnifiedHmiButtonAction", meta => {
                HmiExchangePolicy.RequireTag(_session.ResolveHmiSoftwareOrThrow(software), targetTag, "targetTag");
                var ensure = EnsureUnifiedHmiButtonEventHandler(software, screen, button, eventType);
                meta["ensureMeta"] = ensure.Meta;
                if (!ensure.Meta.TryGetValue("operationSuccess", out var ensured) || ensured is not true)
                    throw new InvalidOperationException("Event creation did not complete.");
                var set = SetUnifiedHmiButtonEventScriptCode(software, screen, button, eventType, scriptCode, null, null, syntaxCheck);
                meta["setMeta"] = set.Meta;
                if (!set.Meta.TryGetValue("operationSuccess", out var written) || written is not true)
                    throw new InvalidOperationException("Event script write did not complete"
                        + (set.Meta["nativeMessage"] is string nativeMessage && nativeMessage.Length != 0 ? ": " + nativeMessage : "."));
                return "Unified HMI button action applied and read back.";
            });

        public HmiMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string tagName,
                string dataType = "", string plcTag = "", string address = "")
        {
            return _session.RunHmiStepTool("BindUnifiedHmiTagDynamization", meta =>
            {
                HmiExchangePolicy.RequireTag(_session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath), tagName, "tagName");
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
                    EngineeringWriteJournal
                        .Created(dyn, hmiSoftwarePath + "/Screens/" + screenName + "/" + itemName + "/Dynamizations/" + propertyName, collection
                            : dynamizations);
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
                void Expected(string property, string value)
                {
                    meta["set" + property] = string.IsNullOrWhiteSpace(value) ? "skipped" : "bound-tag";
                    if (!string.IsNullOrWhiteSpace(value)) results.Add(UnifiedUiModelLogic.VerifiedDerived(dyn, property, value));
                }
                Expected("DataType", dataType);
                Expected("PlcTag", plcTag);
                Expected("Address", address);
                meta["action"] = action; meta["dynamizationType"] = dyn.GetType().FullName;
                meta["members"] = string.Join(" | ", _session.DescribeMembers(dyn, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                meta["after"] = UnifiedRequestScalar.Read(dyn);
                if (results.Any(r => r!.Field("verified") is not true))
                    throw new InvalidOperationException("Tag dynamization write/readback failed; inspect writeResults. Inspect rollbackCompleted and " +
                    "cleanupRequired.");

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
            var created = InvokeCreate(create, collection, new object[] { name });
            EngineeringWriteJournal.Created(created, collection.GetType().FullName + "/" + name, collection: collection);
            return created;
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

        private static object? CreateUnifiedScreenItem(object items, string itemName, Type? itemClrType, string itemTypeHint)
        {
            if (itemClrType == null) throw new NotSupportedException("Screen item type unavailable: " + itemTypeHint);
            var create = items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var created = create.MakeGenericMethod(itemClrType).Invoke(items, new object[] { itemName });
            EngineeringWriteJournal.Created(created, items.GetType().FullName + "/" + itemName, collection: items);
            return created;
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
