using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Engineering;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static readonly TiaMcp.Adapters.Reflection.ReflectionSession LibraryReflectionValues = new TiaMcp.Adapters.Reflection.ReflectionSession((_, __, ___, ____) => null, () => null);
        public Func<string, object>? ResolveEngineeringLibraryDevice { get; set; }
        private object ResolveLibraryDevice(string path, string[] names)
            => ResolveEngineeringLibraryDevice != null ? ResolveEngineeringLibraryDevice(path)
                : EngineeringPlcSession.ExactEngineeringHardware(names, Array.Empty<string>());
        public Func<string, string, object>? ResolveEngineeringHmiScreen { get; set; }
        public Func<object, string>? EngineeringLibraryOptionKind { get; set; }
        public Func<object, object?>? RenderEngineeringValue { get; set; }
        private object ResolveLibraryHmiScreen(string software, string path)
            => ResolveEngineeringHmiScreen != null ? ResolveEngineeringHmiScreen(software, path)
                : ResolveReflectionObject("hmiscreen", software + ":" + path, software, true)
                    ?? throw new PlcSoftwareException("NotFound", "HMI screen '" + path + "' not found.");
        private string LibraryOptionPackageKind(object type) => EngineeringLibraryOptionKind?.Invoke(type) ?? "other";
        private static object? LibraryTypedMember(object target, string member)
        {
#if LIBRARY_V20
            if (target is LibraryTypeVersion version)
            {
                if (member == "OriginalLibrary") return version.OriginalLibrary;
            }
            if (target is LibraryType type)
            {
                if (member == "Namespace") return type.Namespace;
                if (member == "DoNotUse") return type.DoNotUse;
                if (member == "MinimumTargetDeviceVersion") return type.MinimumTargetDeviceVersion;
                if (member == "SetForUpdate") return type.SetForUpdate;
                if (member == "Status") return type.Status;
            }
            if (target is LibraryTypeFolder folder && member == "Status") return folder.Status;
#endif
            return target.GetType().GetProperty(member)?.GetValue(target);
        }
        private static bool LibraryIsDefault(LibraryTypeVersion version)
        {
#if LIBRARY_V17
            return version.IsDefault;
#else
            return false;
#endif
        }
        private static bool LibraryWriteProtected(GlobalLibrary library)
        {
            if (library.IsReadOnly) return true;
#if LIBRARY_V20
            return library.IsWriteProtected;
#else
            return library.GetType().GetProperty("IsWriteProtected")?.GetValue(library) is bool protectedValue && protectedValue;
#endif
        }
        private object? LibraryValue(object? value) => value == null ? null : RenderEngineeringValue != null ? RenderEngineeringValue(value)
            : DefaultLibraryValue(value);
        private static object? DefaultLibraryValue(object value)
            => HardwareScalarEvidence.Scalar(value.GetType()) ? HardwareAddressingPolicy.ScalarValue(value) : HardwareOfficialService.Result(value)["nativeResult"];
        private static List<object?> LibraryList(IEnumerable<string> values) => values.Cast<object?>().ToList();
        private static object? FindExistingByName(object collection, string name)
        {
            try
            {
                if (collection is IEnumerable sequence)
                    foreach (var item in sequence)
                    {
                        var found = TryGetName(item);
                        if (!string.IsNullOrWhiteSpace(found) && string.Equals(found!.Trim(), name, StringComparison.OrdinalIgnoreCase)) return item;
                    }
            }
            catch /* swallow(enumerate-optional): unavailable collection enumeration preserves the missing-item fallback */ { }
            return null;
        }
        private static void LibraryPage(object[] rows, int offset, int limit, Dictionary<string, object?> meta)
            => meta["records"] = EngineeringEvidence.Page(rows.Cast<object?>().ToList(), offset, limit, meta);
        private static void LibraryRequireConfirmation(bool confirmed, string parameter, bool dryRun)
        {
            if (!dryRun && !confirmed) throw new ArgumentException(parameter + "=true required for execution.");
        }
        private List<(PropertyInfo Property, object? Value)> LibraryPrepare(Type type, Dictionary<string, HardwareScalar> changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            LibraryPolicy.GuardKnownTypeWrites(int.Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey, System.Globalization.CultureInfo.InvariantCulture), type, changes.Keys);
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = HardwareScalarEvidence.DistinctProperties(type).FirstOrDefault(p => p.Name == change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, HardwareAddressingPolicy.ConvertValue(change.Value, property.PropertyType)));
            }
            return result;
        }
        private void LibraryApply(object target, List<(PropertyInfo Property, object? Value)> changes, Dictionary<string, object?> meta)
        {
            LibraryPolicy.GuardKnownTypeWrites(int.Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey, System.Globalization.CultureInfo.InvariantCulture), target.GetType(), changes.Select(p => p.Property.Name));
            HardwareScalarEvidence.Apply(target, changes, meta);
        }
        private static Dictionary<string, object?> LibraryObjectAddress(object target)
        {
            var result = HardwareScalarEvidence.Read(target);
            var properties = new List<object?>();
            foreach (var p in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
                properties.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["type"] = p.PropertyType.FullName,
                    ["readable"] = p.GetMethod?.IsPublic == true, ["writable"] = p.SetMethod?.IsPublic == true,
                    ["backlink"] = new[] { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" }.Contains(p.Name, StringComparer.OrdinalIgnoreCase) });
            result["propertySchema"] = properties;
            return result;
        }
        private static object? MultilingualJson(MultilingualText? text)
        {
            if (text == null) return null;
            var items = new Dictionary<string, object?>();
            foreach (var item in PlcGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"] = item.Text;
            return items;
        }
        private static string LibraryPathOf(object node, string rootTypeName)
        {
            // Folder path of a type/master copy/folder relative to the library's system folder, by walking Parent names.
            var names = new List<string>(); object? current = (node as IEngineeringInstance)?.Parent;
            for (int hop = 0; hop < 32 && current != null && current.GetType().Name != rootTypeName && current is not ILibrary; hop++)
            {
                names.Insert(0, PlcGroupOperations.Get(current, "Name").ToString()!);
                current = (current as IEngineeringInstance)?.Parent;
            }
            return string.Join("/", names);
        }
        private static LibraryType ExactLibraryType(object library, string typePath)
        {
            var parts = PlcGroupOperations.Parts(typePath);
            var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
            return (LibraryType)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Types"), parts.Last()) ?? throw new PlcSoftwareException("NotFound", "Exact library type not found: " + typePath));
        }
        private static LibraryTypeVersion ExactTypeVersion(LibraryType type, string version)
        {
            var matches = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Where(v => v.VersionNumber?.ToString() == version).Take(2).ToArray();
            if (matches.Length != 1) throw new PlcSoftwareException("NotFound", "Expected exactly one version '" + version + "' on type '" + type.Name + "', found " + matches.Length + ".");
            return matches[0];
        }
        private MasterCopy ExactMasterCopy(string libraryName,string path) {
            var parts=PlcGroupOperations.Parts(path);
            var folder=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
            return (MasterCopy)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder,"MasterCopies"),parts.Last()) ?? throw new InvalidOperationException("Exact master copy not found."));
        }
        private static object EngineeringLibraryFolder(object library, string folderPath, string rootProperty)
        {
            // SystemGlobalLibrary.TypeFolder is null (observed on TIA V21): say so instead of the generic "unavailable".
            var folder = library.GetType().GetProperty(rootProperty)?.GetValue(library)
                ?? throw new PlcSoftwareException("NotFound", LibraryPolicy.NoTypeFolderMessage(library.GetType().Name, "resolving '" + folderPath + "' under " + rootProperty));
            foreach (var part in PlcGroupOperations.Parts(folderPath, true))
                folder = PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Folders"), part) ?? throw new InvalidOperationException("Library folder not found: " + part);
            return folder;
        }
        private object ExactMasterCopyPlcSource(string softwarePath, string sourcePath, bool block)
        {
            var plc = EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false);
            var parts = PlcGroupOperations.Parts(sourcePath);
            var root = block ? (object)plc.BlockGroup : plc.TypeGroup;
            var group = PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, block ? "Blocks" : "Types"), parts.Last())
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC " + (block ? "block" : "type") + " not found: " + sourcePath + " (path under " + (block ? "Program blocks" : "PLC data types") + ", groups separated by '/', see GetSoftwareTree).");
        }
        private static IEnumerable<string> LibraryInterestingAttributes(object target)
        {
            var result = new List<string>();
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getInfos == null || getAttr == null)
                return result;

            var infos = getInfos.Invoke(target, Array.Empty<object>()) as IEnumerable;
            if (infos == null)
                return result;

            var interesting = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address", "partner", "node"
            };

            foreach (var info in infos)
            {
                var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var lower = name.ToLowerInvariant();
                if (!interesting.Any(k => lower.Contains(k)))
                    continue;

                try
                {
                    var value = getAttr.Invoke(target, new object[] { name });
                    result.Add($"{name}={value ?? ""}");
                }
                catch /* swallow(probe-optional): unreadable attributes are omitted from the diagnostic summary */ { }
            }

            return result;
        }



    }
}
