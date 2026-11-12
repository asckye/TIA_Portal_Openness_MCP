using TiaMcp.Adapters.Native.Engineering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hardware;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<string, object?>? ResolveEngineeringItem { get; set; }
        private DeviceItem? HardwareResolveItem(string path) => ResolveEngineeringItem != null ? ResolveEngineeringItem(path) as DeviceItem
            : HardwareLegacyItem(path);
        private Device? HardwareResolveDevice(string path) => ResolveEngineeringDevice != null ? ResolveEngineeringDevice(path) as Device
            : HardwareLegacyDevice(path);
        private static string HardwareNormalizeAttrName(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        private static List<object?> HardwareToArray(IEnumerable<string> values) => values.Cast<object?>().ToList();
        private static object? HardwareTryProperty(object? owner, params string[] names)
        {
            if (owner == null) return null;
            foreach (string name in names) { try { var prop = owner.GetType().GetProperty(name); if (prop != null) return prop.GetValue(owner); } catch { /* swallow(probe-optional): catalog metadata varies by SDK. */ } }
            return null;
        }
        private void HardwareLegacyRequireCatalogBinding() { Check(); HardwareCatalogPolicy.RequireBound(portal != null && project != null, false); }

        // Device creation (CreateDevice, CreateHardwareCatalogDevice, CreateGsdDevice, the HardwareCreateDeviceFallback
        // operation and the BuildProjectScaffold addDevice step) follows DeviceCreationRules: exactly the requested type
        // identifier and name, one CreateWithItem call, and a typed refusal with the hardware-catalog candidates.
        private List<Device> HardwareDeviceInventory()
            => ProjectDeviceWalk.Devices(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup).ToList();

        private Device HardwareLegacyAddDevice(string orderNumber, string version, string deviceName)
            =>
                HardwareCreateExactDevice(DeviceCreationRules.RequestedTypeIdentifier(orderNumber, version, "orderNumber", "version"), deviceName,
                    "orderNumber/version");

        // Kept operation name for the scaffold and engine wire; the family is a label only, never a list of alternatives.
        private (Device? Device, string? MlfbUsed, string? VersionUsed, List<string> Attempts, string? Error) HardwareLegacyAddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family)
        {
            var typeIdentifier = DeviceCreationRules.RequestedTypeIdentifier(preferredMlfb, preferredVersion, "preferredMlfb", "preferredVersion");
            var device = HardwareCreateExactDevice(typeIdentifier, deviceName, "preferredMlfb/preferredVersion");
            return (device, preferredMlfb, preferredVersion, new List<string> { typeIdentifier + " -> created (one CreateWithItem call)" }, null);
        }

        private static IEnumerable<string> BuildHardwareCatalogFilters(string keyword)
        {
            var raw = (keyword ?? string.Empty).Trim();
            var tokens = raw.Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return new[] { raw }
                .Concat(tokens.Where(t => t.Length >= 3))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string? FindGsdText(XDocument doc, string? textId)
        {
            if (string.IsNullOrWhiteSpace(textId)) return null;
            var text = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text" && string.Equals(e.Attribute("TextId")?.Value, textId, StringComparison.OrdinalIgnoreCase));
            return text?.Attribute("Value")?.Value;
        }

        private static bool ContainsAnyKeywordToken(string text, string keyword)
        {
            return SplitSearchTokens(keyword).Any(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ContainsAllKeywordTokens(string text, string keyword)
        {
            var tokens = SplitSearchTokens(keyword).ToList();
            return tokens.Count > 0 && tokens.All(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> SplitSearchTokens(string keyword)
        {
            return (keyword ?? string.Empty)
                .Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string ShortExceptionMessage(Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            var text = (inner.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length > 220 ? text.Substring(0, 220) + "..." : text;
        }
        // TIA V21 project (2026-09-20; docs/reference/real-machine-ledger.md, crash ⑧): Devices.CreateWithItem("OrderNumber:6AV2 128-3GB06-0AXx/20.0.0.0", ...) - a WinCC Unified
        // Comfort panel of the PREVIOUS device version - made TIA Portal V21 exit; the same panel with /21.0.0.0 was created normally.
        // The device version of a Unified panel has to match the Portal major version.
        private void GuardUnifiedPanelVersion(string orderNumber, string version)
            => DeviceCreationRules.RequireUnifiedPanelVersion(orderNumber, version, HardwareMajorVersion);

        internal static string NormalizeOrderNumber(string s)
        {
            return (s ?? string.Empty).Replace(" ", "").Trim();
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
                try { oldValue = di.GetAttribute(info.Name); } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
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
                di.SetAttribute(info.Name, typedValue);

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
                meta["error"] = HardwareExceptionDetail(ex);
                return new HardwareAddressingReply { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        public Dictionary<string, object?> HardwareLegacyDumpDeviceAttributes(string devicePath, string? nameFilter = null, int maxItems = 500)
        {
            if (HardwareProjectMissing()) return new Dictionary<string, object?> { ["found"] = false, ["message"] = "No project open." };
            var device = HardwareResolveDevice(devicePath);
            if (device == null) return new Dictionary<string, object?> {
                ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            // Match alternatives separated by '|' or ',' independently instead of treating them as one substring.
            var filters = (nameFilter ?? string.Empty).Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(HardwareNormalizeAttrName)
                .Where(f => f.Length > 0).ToArray();
            var hasFilter = filters.Length > 0;

            var itemsArr = new List<object?>();
            int itemCount = 0;
            int totalAttrs = 0, writableAttrs = 0;

            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in HardwareTraverseDeviceItems(root, root.Name))
                {
                    if (itemCount >= maxItems) break;
                    var it = (DeviceItem)tup.Item1;

                    System.Collections.Generic.IList<EngineeringAttributeInfo>? infos = null;
                    try { infos = it.GetAttributeInfos(); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ }
                    if (infos == null || infos.Count == 0) continue;

                    var attrsArr = new List<object?>();
                    foreach (var info in infos)
                    {
                        var name = info?.Name ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (hasFilter && !filters.Any(f => HardwareNormalizeAttrName(name).Contains(f))) continue;

                        var access = TryGetAttributeInfoAccess(info!);
                        object? val = null; bool readErr = false;
                        try { val = it.GetAttribute(name); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ readErr = true; }

                        var isWritable = access?.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                         || access?.IndexOf("readwrite", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isWritable) writableAttrs++;
                        totalAttrs++;

                        attrsArr.Add(new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["access"] = access ?? string.Empty,
                            ["value"] = val?.ToString() ?? (readErr ? "<read-error>" : string.Empty),
                            ["valueType"] = val?.GetType().Name ?? string.Empty
                        });
                    }

                    if (attrsArr.Count == 0) continue;
                    itemsArr.Add(new Dictionary<string, object?>
                    {
                        ["item"] = it.Name,
                        ["path"] = tup.Item2,
                        ["deviceItemPath"] = tup.Item2,
                        ["attributeCount"] = attrsArr.Count,
                        ["attributes"] = attrsArr
                    });
                    itemCount++;
                }
            }

            var reply = new Dictionary<string, object?>
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["nameFilter"] = nameFilter ?? string.Empty,
                ["itemCount"] = itemsArr.Count,
                ["totalAttributes"] = totalAttrs,
                ["writableAttributes"] = writableAttrs,
                ["items"] = itemsArr
            };
            var prefix = Native.Hardware.ProjectDeviceWalk.DevicePathPrefix(
                project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup, device);
            foreach (var row in itemsArr.OfType<Dictionary<string, object?>>())
                row["deviceItemPath"] = prefix + "/" + row["path"];
            return reply;
        }
        private static string? TryGetAttributeInfoAccess(object info)
        {
            foreach (var pn in new[] { "AccessMode", "Access", "ReadOnly", "IsReadOnly" })
            {
                try
                {
                    var p = info.GetType().GetProperty(pn);
                    if (p != null)
                    {
                        var v = p.GetValue(info);
                        if (v != null) return pn + "=" + v;
                    }
                }
                catch { /* swallow(probe-optional): Access metadata varies by SDK; continue with the next supported property name. */ }
            }
            return null;
        }

        public string HardwareCreateDevice(string orderNumber, string version, string deviceName)
            => HardwareLegacyAddDevice(orderNumber, version, deviceName).Name;
        public Dictionary<string, object?> HardwareCreateCatalogDevice(string keyword, string deviceName, string preferredText = "")
        {
            var result = HardwareLegacyAddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
            return new Dictionary<string, object?> {
                ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["Candidate"] = result.Candidate,
                    ["Candidates"] = result.Candidates, ["Attempts"] = result.Attempts, ["Error"] = result.Error
            };
        }
        public Dictionary<string, object?> HardwareCreateGsdDevice(string keyword, string deviceName, string preferredDap = "")
        {
            var result = HardwareLegacyAddGsdDeviceWithProbe(keyword, deviceName, preferredDap);
            return new Dictionary<string, object?> {
                ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["Candidate"] = result.Candidate,
                    ["Candidates"] = result.Candidates, ["Attempts"] = result.Attempts, ["Error"] = result.Error
            };
        }
        // ListDevices: root devices, nested user groups and the ungrouped system group (one shared walk).
        public List<Device> HardwareReadDevicesRaw(string regexName = "")
        {
            if (HardwareProjectMissing()) return [];
            return EnumerateAllDevices().Where(device => HardwareNameMatches(device.Name, regexName)).ToList();
        }
        private static bool HardwareNameMatches(string name, string regexName)
        {
            if (string.IsNullOrEmpty(regexName)) return true;
            try { return Regex.IsMatch(name, regexName, RegexOptions.IgnoreCase); }
            catch (ArgumentException) /* swallow(parse-fallback): an invalid regex filter matches no device */ { return false; }
        }
        public List<HardwareDeviceDescription> HardwareDescribeDevices(string regexName = "")
            => HardwareReadDevicesRaw(regexName).Select(HardwareDescribeDevice).ToList();
        public HardwareDeviceDescription? HardwareDescribeDeviceAt(string path)
        { var device = HardwareResolveDevice(path); return device == null ? null : HardwareDescribeDevice(device); }
        public HardwareDeviceDescription? HardwareDescribeItemAt(string path)
        { var item = HardwareResolveItem(path); return item == null ? null : HardwareDescribeDevice(item); }
        private static HardwareDeviceDescription HardwareDescribeDevice(HardwareObject obj)
        {
            var result = new HardwareDeviceDescription();
            IEngineeringObject attributes = obj;
            foreach (var attr in attributes.GetAttributeInfos())
            {
                object? value;
                try { value = attributes.GetAttribute(attr.Name); } catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */ { value = "<unreadable: " + ex.GetType().Name + ">"; }
                if (value != null
                        && !(value.GetType().IsPrimitive || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid
                            || value.GetType().IsEnum))
                    try { value = value.ToString(); } catch { /* swallow(probe-optional): Preserve the type marker when an optional attribute cannot be formatted. */ value = "<" + value.GetType().Name + ">"; }
                result.Attributes
                    .Add(new HardwareDeviceAttribute {
                        Name = attr.Name, Value = value, AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode) });
            }
            result.Name = obj.Name; result.Description = obj.ToString(); return result;
        }

        private static bool HardwareAttributeWritable(EngineeringAttributeInfo info)
            => info.AccessMode == EngineeringAttributeAccessMode.Write || info.AccessMode == EngineeringAttributeAccessMode.ReadWrite;

        private static IEnumerable<Type> HardwareSupportedTypes(EngineeringAttributeInfo info)
        {
#if PLC_ATTRIBUTE_SUPPORTED_TYPES
            return info.SupportedTypes?.Where(t => t != null) ?? Enumerable.Empty<Type>();
#else
            return Enumerable.Empty<Type>();
#endif
        }

        private static string HardwareAttributeTypes(EngineeringAttributeInfo info)
            => string.Join("|", HardwareSupportedTypes(info).Select(t => t.FullName ?? t.Name));

        // The type comes from the current value, then from SupportedTypes (V18+). An attribute whose type cannot be read is
        // refused instead of being written as a string.
        private static bool HardwareTryCoerceAttributeValue(string value, object? oldValue, EngineeringAttributeInfo info, out object typed, out string problem)
        {
            var candidates = (oldValue != null ? new[] { oldValue.GetType() } : Array.Empty<Type>()).Concat(HardwareSupportedTypes(info)).Distinct().ToList();
            foreach (var type in candidates)
                if (HardwareTryConvert(value, type, out typed)) { problem = ""; return true; }
            typed = value;
#if PLC_ATTRIBUTE_SUPPORTED_TYPES
            const string missing = " and no SupportedTypes).";
#else
            const string missing = "; SupportedTypes needs V18).";
#endif
            problem = candidates.Count == 0
                ? "The type of attribute '" + info.Name + "' cannot be read (no current value" + missing
                : "'" + value + "' cannot be converted to " + string.Join(" or ", candidates.Select(t => t.Name)) + " for attribute '" + info.Name + "'.";
            return false;
        }

        private static bool HardwareTryConvert(string value, Type type, out object typed)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var text = value ?? string.Empty;
            typed = text;
            try
            {
                if (type == typeof(string)) return true;
                if (type.IsEnum) { typed = Enum.Parse(type, text, ignoreCase: true); return Enum.IsDefined(type, typed) || text.IndexOf(',') >= 0; }
                if (type == typeof(bool)) { if (!bool.TryParse(text, out var b)) return false; typed = b; return true; }
                if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(long)
                        || type == typeof(ulong)
                    || type == typeof(byte) || type == typeof(sbyte) || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                { typed = Convert.ChangeType(text, type, culture); return true; }
            }
            catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException || error is InvalidCastException)
            {
                typed = error.Message;
                return false;
            }
            return false;
        }
        public string HardwareReadItemTree(string deviceItemPath, int maxDepth = 4)
        {


            if (HardwareProjectMissing())
            {
                return string.Empty;
            }

            var root = HardwareResolveItem(deviceItemPath);
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name} [DeviceItem]");
            BuildDeviceItemTree(sb, root, new List<bool>(), 0, Math.Max(0, maxDepth));
            return sb.ToString();
        }
        private static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            // Hardware components (Items)
            if (node.Items != null && node.Items.Count > 0)
            {
                var items = node.Items.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = (i == items.Count - 1) && (node.DeviceItems == null || node.DeviceItems.Count == 0);
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            // Sub device items
            if (node.DeviceItems != null && node.DeviceItems.Count > 0)
            {
                var children = node.DeviceItems.ToList();
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var isLast = i == children.Count - 1;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                    BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
                }
            }
        }
        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }
        private List<object?> HardwareBuildDeviceItemNetworkReadbackJson(string deviceItemPath)
        {
            var arr = new List<object?>();
            var attrs = HardwareGetDeviceItemNetworkInfo(deviceItemPath) ?? new List<HardwareNetworkAttribute>();
            foreach (var attr in attrs)
            {
                arr.Add(new Dictionary<string, object?>
                {
                    ["name"] = attr.Name ?? string.Empty,
                    ["value"] = attr.Value ?? string.Empty,
                    ["dataType"] = attr.DataType ?? string.Empty,
                    ["isWritable"] = attr.IsWritable
                });
            }
            return arr;
        }
        private int HardwareMajorVersion => ReleaseKey == "14sp1" ? 14 : ReleaseKey == "15.1" ? 15 : int
            .Parse(ReleaseKey, System.Globalization.CultureInfo.InvariantCulture);

        internal static IEnumerable<(DeviceItem Item, string Path)> HardwareTraverseDeviceItems(DeviceItem root, string path)
        {
            yield return (root, path);
            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in HardwareTraverseDeviceItems(child, path + "/" + child.Name))
                {

      yield return nested;
                }
            }
        }
        public Dictionary<string, object?> HardwareCreateDeviceFallback(string preferredMlfb, string preferredVersion, string deviceName, string family)
        {
            var result = HardwareLegacyAddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
            return new Dictionary<string, object?> {
                ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["MlfbUsed"] = result.MlfbUsed,
                    ["VersionUsed"] = result.VersionUsed, ["Attempts"] = result.Attempts, ["Error"] = result.Error
            };
        }
        public string[] HardwareReadPlcNames()
        {
            if (HardwareProjectMissing()) throw new HardwareAddressingException("InvalidState", "No project is open.");
            return HardwareEnumerateSoftwareContainers()
                .Select(c => c.Software).OfType<PlcSoftware>().Select(p => p.Name).ToArray();
        }
        private IEnumerable<SoftwareContainer> HardwareEnumerateSoftwareContainers()
        {
            if (project == null) yield break;
            int count = 0;
            foreach (var device in EnumerateAllDevices())
            {
                var pending = new Stack<DeviceItem>(device.DeviceItems);
                while (pending.Count > 0)
                {
                    if (++count > 100000) throw new HardwareAddressingException("OpennessError",
                        "Exact software enumeration limit reached; no target selected.");
                    var item = pending.Pop();
                    var sc = item.GetService<SoftwareContainer>();
                    if (HardwareSoftwareLookupValue(sc) != null) yield return sc!;
                    foreach (var child in item.DeviceItems) pending.Push(child);
                }
            }
        }
        private static Software? HardwareSoftwareLookupValue(SoftwareContainer? container)
            => container?.Software;
    }
}
