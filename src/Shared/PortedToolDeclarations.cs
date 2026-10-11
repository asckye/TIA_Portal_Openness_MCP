using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class PortedToolDeclarations
    {
        private static readonly IReadOnlyDictionary<string, Type[]> Types = new Dictionary<string, Type[]>(StringComparer.Ordinal) {
#if !TIA_ENGINE_PORTED
            ["F01"] = new[] { typeof(McpServer), typeof(HostMetaTools), typeof(ExportTools), typeof(OfflineSuiteTools), typeof(EngineeringDiagnosticsTools), typeof(EcosystemTools), typeof(V21EcosystemTools) },
            ["F02"] = new[] { typeof(PlcOfflineTools), typeof(PlcDocumentationTools), typeof(OfflineAnalysisTools), typeof(TemplateTools), typeof(QualityAuditTools), typeof(EngineeringDiagnosticsTools), typeof(EcosystemTools), typeof(V21EcosystemTools) },
            ["F03"] = new[] { typeof(HmiOfflineTools), typeof(OfflineSuiteTools), typeof(XmlBuilderTools), typeof(V21EcosystemTools) },
#endif
            ["F05"] = new[] { typeof(ProjectLifecycleSessionTools), typeof(ProjectLifecycleProjectTools), typeof(ProjectLifecycleInspectionTools), typeof(ProjectLifecycleDeviceTools), typeof(ProjectLifecycleExchangeTools), typeof(ProjectSecurityTools) },
            ["F07"] = new[] { typeof(ProjectProtectionTools), typeof(ProjectUserManagementTools), typeof(CertificateManagementTools), typeof(SecurityDeepTools), typeof(HardwareSecurityTools), typeof(HardwareServicesTools) },
#if TIA_ENGINE_PORTED
            ["F33"] = new[] { typeof(VersionControlTools), typeof(TeamcenterTools) },
#else
            ["F33"] = new[] { typeof(VersionControlTools), typeof(TeamcenterTools), typeof(GitWorkflowTools) },
#endif
            ["F06"] = new[] { typeof(EngineeringServicesTools) },
            ["F23"] = new[] { typeof(LibraryTools) },
            ["F24"] = new[] { typeof(HmiDescribeTools), typeof(HmiExchangeTools), typeof(HmiInspectionPortTools), typeof(HmiTagDeletionTools) },
            ["F25"] = new[] { typeof(ClassicHmiFoldersTools), typeof(MotionProDiagClassicHmiTools) },
            ["F35"] = new[] { typeof(ReflectionTools) },
            ["F09"] = new[] { typeof(PlcReadModifyTools), typeof(PlcBuildTools) },
            ["F11"] = new[] { typeof(PlcReadModifyTools) },
            ["F12"] = new[] { typeof(PlcWatchTools), typeof(PlcCausalTraceTools) },
            ["F13"] = new[] { typeof(PlcTechnologyTools), typeof(PlcMotionTools) },
            ["F10"] = new[] { typeof(DocumentsTools), typeof(EngineeringAuditTools), typeof(SoftwareUnitDeepTools) },
            ["F14"] = new[] { typeof(AlarmsTools), typeof(PlcProDiagTools), typeof(SpecializedExchangeTools) },
            ["F15"] = new[] { typeof(OpcUaTools) },
            ["F16"] = new[] { typeof(PlcIntegrityTools) },
            ["F17"] = new[] { typeof(PlcCompilePortTools) },
            ["F08"] = new[] { typeof(PlcOrganisationTools) },
            ["F18"] = new[] { typeof(HardwareDevicesTools), typeof(ModulesTools), typeof(HardwareManagementTools), typeof(HardwareNetworkTools), typeof(HardwareServicesPortTools) },
            ["F20"] = new[] { typeof(HardwareNetworkTools), typeof(HardwareServicesPortTools) },
            ["F19"] = new[] { typeof(AddressesTools) },
            ["F21"] = new[] { typeof(HardwareAmlTools) }
        };

        internal static IEnumerable<MethodInfo> Methods(string release, bool includeUnavailable = false)
        {
            foreach (var family in PortedFamilies.All.Where(f => f.Available(release)))
            {
                if (!Types.TryGetValue(family.Name, out var types)) continue;
                var methods = types.SelectMany(t => t.GetMethods()).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } attribute && family.Tools.Contains(attribute.Name, StringComparer.Ordinal)).ToArray();
                var names = methods.Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!).ToArray();
                var expected = family.Tools;
#if TIA_ENGINE_PORTED
                expected = expected.Where(name => ToolExecution.Table[name] != "host").ToArray();
#endif
                if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                    || names.Except(expected, StringComparer.Ordinal).Any())
                    throw new InvalidOperationException("Ported declaration roster mismatch: " + family.Name);
                foreach (var method in methods) if (includeUnavailable || PortedFamilies.Available(release, method.GetCustomAttribute<McpServerToolAttribute>()!.Name!)) yield return method;
            }
        }
    }
}
