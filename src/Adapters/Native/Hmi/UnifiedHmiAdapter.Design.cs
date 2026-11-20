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
                    EngineeringWriteJournal.Created(screen, hmiSoftwarePath + "/Screens/" + screenName, collection: screens);
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
                    "Create the connection in TIA Portal (Devices & networks), then read it back. DriverProperties " +
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
            EngineeringWriteJournal
                .CaptureProperties(target, props.Select(pair => UnifiedUiModelLogic.FindProperty(target.GetType(), pair.Key))
                    .Where(property => property?.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true).Cast<PropertyInfo>());
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
                EngineeringWriteJournal
                    .Attribute(target, attributeName, () => get == null ? throw new NotSupportedException("Public GetAttribute unavailable.") : get
                        .Invoke(target, new object[] { attributeName }),
                    previous => set.Invoke(target, new[] { attributeName, previous }));
                set.Invoke(target, new[] { attributeName, typed });
                return true;
            }
            catch /* swallow(native-fallback): Unsupported attribute conversion or assignment reports false to the existing caller fallback. */
            {
                return false;
            }
        }
    }
}
#endif
