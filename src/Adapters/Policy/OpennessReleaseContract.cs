using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using TiaMcp.Versioning;

namespace TiaMcpServer.Siemens
{
    // Documented foundation only. Does not mark a catalog release runnable.
    // Original-V14 branches are historical reference only; For rejects excluded releases.
    // References and deliberately unknown fields: docs/reference/openness-coverage.md.
    internal sealed class OpennessReleaseContract
    {
        internal string Key { get; }
        internal string CoreAssemblyName => Key == "21" ? "Siemens.Engineering.Base" : "Siemens.Engineering";
        // Recorded from authorized PublicAPI PE metadata, not from a candidate being admitted.
        internal AssemblyName CoreAssemblyIdentity => new AssemblyName(CoreAssemblyName +
            ", Version=" + (Key == "14sp1" ? "14.0.1.0" : Key == "15.1" ? "15.1.0.0" : Key + ".0.0.0") +
            ", Culture=neutral, PublicKeyToken=" + (Key == "21" ? "29bfe5fdf4ba5d3b" : "d29ec89bac048f84"));
        internal string SoftwareContainerType => Key == "14"
            ? "Siemens.Engineering.HW.ISoftwareContainer"
            : "Siemens.Engineering.HW.Features.SoftwareContainer";
        internal string SoftwareBaseType => Key == "14"
            ? "Siemens.Engineering.HW.SoftwareBase" : "Siemens.Engineering.HW.Software";
        internal Type XmlFileArgumentType => Key == "14" ? typeof(string) : typeof(FileInfo);
        internal string[] TagConstantCollections => Key == "14" ? new[] { "Constants" } : new[] { "UserConstants", "SystemConstants" };
        internal string SclXmlBodyEvidence => Key == "14" ? "interface-only" : Key == "14sp1" ? "unverified" : "documented";

        private readonly AssemblyName? expectedIdentity;
        private readonly string? selectedDirectory;
        private OpennessReleaseContract(string key, AssemblyName? expected, string? directory)
        {
            Key = key;
            expectedIdentity = expected == null ? null : new AssemblyName(expected.FullName);
            if (expectedIdentity != null)
                EngineeringAssemblyIdentity.RequireMatch(CoreAssemblyIdentity, expectedIdentity, "reference inventory");
            if (directory != null)
            {
                if (expectedIdentity == null || string.IsNullOrWhiteSpace(directory))
                    throw new ArgumentException("An explicit SDK directory requires a verified reference identity.", nameof(directory));
                selectedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        // expected must come from the separately verified SDK reference inventory,
        // never derive it from the candidate assembly being admitted.
        internal static OpennessReleaseContract For(string key, AssemblyName? expected = null, string? selectedPublicApiDirectory = null)
        {
            TiaVersionCatalog.Get(key); // Exact release key, never a decimal/major conversion.
            return new OpennessReleaseContract(key, expected, selectedPublicApiDirectory);
        }

        internal object XmlFileArgument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An XML file path is required.", nameof(path));
            return Key == "14" ? (object)path : new FileInfo(TiaOpenness.Shared.NativePathSelection.FullPath(path));
        }

        internal string UnifiedCollection(string logicalName)
        {
            if (new[] { "14", "14sp1", "15", "15.1" }.Contains(Key))
                throw new NotSupportedException("No documented Unified API contract for " + Key + ".");
            if (!new[] { "Tags", "TagTables", "SystemTags", "Connections" }.Contains(logicalName))
                throw new ArgumentException("Unknown Unified collection: " + logicalName);
            return Key == "16" ? "Hmi" + logicalName : logicalName;
        }

        // The manual's V16 spelling is not present in every V16-identity DLL:
        // compatibility DLLs shipped in V19 expose the renamed properties.
        // Feed only metadata from an independently identity-validated SDK type.
        internal string ResolveUnifiedCollection(string logicalName, System.Collections.Generic.IEnumerable<string> publicProperties)
        {
            var documented = UnifiedCollection(logicalName);
            var candidates = new[] { documented, logicalName }.Distinct().ToArray();
            var matches = candidates.Where(name => publicProperties.Contains(name, StringComparer.Ordinal)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new AmbiguousMatchException("SDK exposes both Unified collection aliases: " + logicalName);
            throw new MissingMemberException("Selected SDK does not expose the documented Unified collection: " + logicalName);
        }

        internal static string? PublicApiKey(string path)
        {
            // Match a full directory segment immediately after PublicAPI. A host
            // installation can contain older APIs; Portal Vxx is not API identity.
            var match = Regex.Match(path ?? "", @"(?:^|[\\/])PublicAPI[\\/]V(14 SP1|15\.1|16|17|18|19|20|21)(?:[\\/]|$)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            return match.Groups[1].Value.Equals("14 SP1", StringComparison.OrdinalIgnoreCase) ? "14sp1" : match.Groups[1].Value;
        }

        internal void RequireAssembly(Assembly assembly)
        {
            RequireAssemblyIdentity(assembly.GetName(), assembly.Location);
        }

        // Also permits offline admission tests with AssemblyName metadata alone.
        internal void RequireAssemblyIdentity(AssemblyName actual, string path)
        {
            if (expectedIdentity == null)
                throw new NotSupportedException("This documented contract is unbound; a verified SDK reference assembly identity is required before native calls.");
            EngineeringAssemblyIdentity.RequireMatch(expectedIdentity, actual, path);
            bool directoryMatches = selectedDirectory == null ? PublicApiKey(path) == Key :
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), selectedDirectory,
                    Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (actual.Name != CoreAssemblyName || !directoryMatches)
                throw new FileLoadException("The adapter requires the exact " + Key + " " + CoreAssemblyName +
                    " assembly from its selected PublicAPI directory; no cross-release fallback.", path);
        }

        // A real read-only adapter boundary: exact documented service type and
        // explicit service-provider interface, without synthesized Siemens types.
        // Kept out of engine selection until licensed SDK shape/native gates pass.
        internal object? ReadSoftware(object deviceItem, Assembly engineering)
        {
            RequireAssembly(engineering);
            var itemType = engineering.GetType("Siemens.Engineering.HW.DeviceItem", throwOnError: true)!;
            if (!itemType.IsInstanceOfType(deviceItem)) throw new ArgumentException("Device item belongs to another API assembly.");
            var provider = engineering.GetType("Siemens.Engineering.IEngineeringServiceProvider", throwOnError: true)!;
            var service = engineering.GetType(SoftwareContainerType, throwOnError: true)!;
            var getter = provider.GetMethods().Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
            var container = InvokeExact(getter.MakeGenericMethod(service), deviceItem, Array.Empty<object>());
            if (container == null) return null; // E.g. redundant partner CPU; not an API mismatch.
            var software = service.GetProperty("Software")!.GetValue(container);
            var softwareType = engineering.GetType(SoftwareBaseType, throwOnError: true)!;
            if (software != null && !softwareType.IsInstanceOfType(software)) throw new InvalidOperationException("Software has an unexpected API identity.");
            return software;
        }

        internal object? ReadAttribute(object target, string name, Assembly engineering)
        {
            RequireAssembly(engineering);
            var contract = engineering.GetType("Siemens.Engineering.IEngineeringObject", throwOnError: true)!;
            if (!contract.IsInstanceOfType(target)) throw new ArgumentException("Object belongs to another API assembly.");
            // Pre-V17 implements this explicitly. Never fall back to a public-only
            // lookup or interpret a missing public member as an empty attribute.
            var method = contract.GetMethod("GetAttribute", new[] { typeof(string) })
                ?? throw new MissingMethodException(contract.FullName, "GetAttribute(string)");
            return InvokeExact(method, target, new object[] { name });
        }

        internal object? ExportXml(object target, string path, bool withDefaults, Assembly engineering)
        {
            RequireEngineeringObject(target, engineering);
            var optionType = engineering.GetType("Siemens.Engineering.ExportOptions", throwOnError: true)!;
            var method = RequireExactMethod(target.GetType(), "Export", XmlFileArgumentType, optionType);
            return InvokeExact(method, target, new[] { XmlFileArgument(path), Enum.Parse(optionType, withDefaults ? "WithDefaults" : "None") });
        }

        internal object? ImportXml(object composition, string path, bool overwrite, Assembly engineering)
        {
            RequireAssembly(engineering);
            var compositionContract = engineering.GetType("Siemens.Engineering.IEngineeringCompositionOrObject", throwOnError: true)!;
            if (!compositionContract.IsInstanceOfType(composition)) throw new ArgumentException("Composition belongs to another API assembly.");
            var optionType = engineering.GetType("Siemens.Engineering.ImportOptions", throwOnError: true)!;
            var method = RequireExactMethod(composition.GetType(), "Import", XmlFileArgumentType, optionType);
            // Compositions need not implement IEngineeringObject, and V21 PLC
            // declarations live in Step7. Anchor their common core interface;
            // the runtime resolver must separately verify all module identities.
            return InvokeExact(method, composition, new[] { XmlFileArgument(path), Enum.Parse(optionType, overwrite ? "Override" : "None") });
        }

        private void RequireEngineeringObject(object target, Assembly engineering)
        {
            RequireAssembly(engineering);
            var contract = engineering.GetType("Siemens.Engineering.IEngineeringObject", throwOnError: true)!;
            if (!contract.IsInstanceOfType(target)) throw new ArgumentException("Object belongs to another API assembly.");
        }

        internal static MethodInfo RequireExactMethod(Type owner, string name, params Type[] parameters)
        {
            // DefaultBinder may accept assignable types (e.g. object instead of
            // FileInfo). Exact parameter identity is required for documented APIs.
            var matches = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && !m.IsGenericMethod &&
                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new AmbiguousMatchException("Multiple exact native signatures: " + owner.FullName + "." + name);
            throw new MissingMethodException(owner.FullName,
                name + "(" + string.Join(",", parameters.Select(p => p.FullName)) + ")");
        }

        internal static object? InvokeExact(MethodInfo method, object owner, object[] arguments)
        {
            try { return method.Invoke(owner, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }
}
