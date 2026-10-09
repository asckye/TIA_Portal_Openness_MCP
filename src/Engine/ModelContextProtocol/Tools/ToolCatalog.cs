using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class ToolCatalog : IToolCatalogView
    {
        // Reviewed registration metadata avoids loading unrelated embedded SDK/model types.
        // TiaMcp.Engine.Harness compares this roster with the attributed types in the built assembly.
        internal static readonly string[] ToolTypeNames = {
            "AddressesTools",
            "AlarmsTools",
            "CertificateManagementTools",
            "CfcTools",
            "ClassicHmiFoldersTools",
            "CompileCandidateTools",
            "DccTools",
            "DevicesTools",
            "HardwareDevicesTools",
            "DiagnosticsTools",
            "DocumentsTools",
            "EcosystemTools",
            "EngineeringAuditTools",
            "FallbackCandidateTools",
            "GitWorkflowTools",
            "GlobalScriptEditTools",
            "GraphicSelectionTools",
            "HardwareAmlTools",
            "HardwareManagementTools",
            "HardwareNetworkTools",
            "HardwareSecurityTools",
            "HardwareServicesTools",
            "HardwareServicesPortTools",
            "HmiDescribeTools",
            "HmiExchangeTools",
            "HmiInspectionTools",
            "HmiTagDeletionTools",
            "ImportOrderTools",
            "ImportStagingTools",
            "LibraryTools",
            "McpServer",
            "MigrationReadTools",
            "ModulesTools",
            "MotionProDiagClassicHmiTools",
            "NativeExchangeTools",
            "OfflineSuiteTools",
            "OnlineDownloadTools",
            "OpcUaTools",
            "OptionalEngineeringTools",
            "PlcBlocksTools",
            "PlcBuildTools",
            "PlcExportCandidateTools",
            "PlcExternalSourcesTools",
            "PlcImportCandidateTools",
            "PlcSimAdvancedTools",
            "PlcSoftwareTools",
            "PlcSourceCandidateTools",
            "PlcTablesTools",
            "ProjectSecurityTools",
            "ProjectSessionTools",
            "ReflectionTools",
            "RuntimeChannelTools",
            "RuntimeSettingsTools",
            "RuntimeTools",
            "SafetyManagementTools",
            "SafetyValidationTools",
            "SaveCloseCandidateTools",
            "SecurityDeepTools",
            "SessionCandidateTools",
            "SessionTools",
            "SivarcTools",
            "SoftwareUnitDeepTools",
            "SoftwareUnitManagementTools",
            "SpecializedExchangeTools",
            "StartdriveTools",
            "TeamcenterTools",
            "TechnologyObjectsTools",
            "TestSuiteTools",
            "ToolUsageTools",
            "TypesTools",
            "UnifiedEngineeringTools",
            "UnifiedEventsTools",
            "UnifiedExchangeTools",
            "UnifiedHmiGroupsTools",
            "UnifiedHmiTools",
            "UnifiedObjectServicesTools",
            "UnifiedScreenItemsTools",
            "UnifiedUiModelTools",
            "V20OptionsTools",
            "VersionControlTools",
            "XmlBuilderTools",
        };
        internal static readonly string[] ServiceTypeNames = {
            "AlarmsService",
            "CertificateManagementService",
            "CfcService",
            "ClassicHmiFoldersService",
            "DccService",
            "DevicesService",
            "DocumentsService",
            "EngineeringAuditService",
            "GlobalScriptEditService",
            "GraphicSelectionService",
            "HardwareAmlService",
            "HardwareManagementService",
            "HardwareNetworkService",
            "HardwareServicesService",
            "HardwareDevicesService",
            "HardwareNetworkPortService",
            "HardwareServicesPortService",
            "HmiDescribeService",
            "HmiExchangeService",
            "HmiInspectionService",
            "HmiTagDeletionService",
            "LibraryService",
            "MigrationReadService",
            "HardwareModulesService",
            "MotionProDiagClassicHmiService",
            "NativeExchangeService",
            "OnlineDownloadService",
            "OpcUaService",
            "OptionalEngineeringService",
            "PlcBlocksService",
            "PlcExportCandidateService",
            "PlcExternalSourcesService",
            "PlcImportCandidateService",
            "PlcSoftwareService",
            "PlcSourceCandidateService",
            "PlcTablesService",
            "ProjectSecurityService",
            "ReflectionService",
            "RuntimeSettingsService",
            "SafetyManagementService",
            "SafetyValidationService",
            "SecurityDeepService",
            "SivarcService",
            "SoftwareUnitDeepService",
            "SoftwareUnitManagementService",
            "SpecializedExchangeService",
            "StartdriveService",
            "TeamcenterService",
            "TechnologyObjectsService",
            "TestSuiteService",
            "TypesService",
            "UnifiedEngineeringService",
            "UnifiedEventsService",
            "UnifiedExchangeService",
            "UnifiedHmiGroupsService",
            "UnifiedHmiService",
            "UnifiedObjectServicesService",
            "UnifiedScreenItemsService",
            "UnifiedUiModelService",
            "V20OptionsService",
            "VersionControlService",
        };
        internal static bool UsesRegistrationMetadata => typeof(ToolCatalog).Assembly.GetName().Name is "TiaMcp.Engine.V20" or "TiaMcp.Engine.V21";
        private static readonly Lazy<ToolCatalog> engine = new Lazy<ToolCatalog>(() =>
            new ToolCatalog(UsesRegistrationMetadata
                ? ToolTypeNames.Select(name => typeof(ToolCatalog).Assembly.GetType("TiaMcpServer.ModelContextProtocol." + name, throwOnError: true)!)
                : LoadableTypes(typeof(ToolCatalog).Assembly, "ToolCatalog").Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() != null)));

        // A type whose optional dependency is absent must not take every tool down: tool types only
        // reference the engine and its shipped libraries, so they are among the types that do load.
        internal static IEnumerable<Type> LoadableTypes(Assembly assembly, string context)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                var types = ex.Types.OfType<Type>().ToArray();
                int missing = ex.Types.Length - types.Length;
                Console.Error.WriteLine(context + ": continuing with " + types.Length
                    + " loadable engine types; " + missing + " types have unresolved dependencies.");
                return types;
            }
        }

        internal static ToolCatalog Engine => engine.Value;
        internal IReadOnlyList<KeyValuePair<string, MethodInfo>> Methods { get; }
        /// <summary>Write and online-write tools by their typed classification (name inference only for unregistered names).</summary>
        internal static bool IsWrite(string name)
        {
            string operation = ToolTaxonomy.OperationOf(name, null).Operation;
            return operation == "WRITE" || operation == "ONLINE-WRITE" || name == "StageImportFiles" || name == "CleanupStagedImportFiles";
        }

        internal ToolCatalog(IEnumerable<Type> types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            var allTypes = types.ToArray();
            var candidates = allTypes.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttribute<BehaviorCandidateAttribute>() != null).ToArray();
            foreach (var type in allTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (attribute == null) continue;
                    var name = attribute.Name ?? method.Name;
#if TIA_ENGINE_PORTED
                    if (TiaMcp.Adapters.Contracts.PortedFamilies.All.Any(f => (f.Name == "F01" || f.Name == "F02" || f.Name == "F03") && f.Tools.Contains(name, StringComparer.Ordinal)))
                        throw new InvalidOperationException("A host-owned tool is still registered in the engine: " + name);
#endif
                    if (methods.TryGetValue(name, out var previous))
                        throw new InvalidOperationException("Duplicate MCP tool name '" + name + "': "
                            + previous.DeclaringType!.FullName + "." + previous.Name + " and "
                            + method.DeclaringType!.FullName + "." + method.Name + ". Tool names must be unique (OrdinalIgnoreCase).");
                    methods.Add(name, TiaMcp.Logic.V4.BehaviorCapabilities.SelectMethod(name, method, candidates, McpServer.ReleaseKey));
                }
            }
            Methods = methods.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList().AsReadOnly();
            all = new Lazy<IReadOnlyDictionary<string, ToolDescriptor>>(() => View(false));
            includingUnavailable = new Lazy<IReadOnlyDictionary<string, ToolDescriptor>>(() => View(true));
            lite = new Lazy<IReadOnlyList<ToolDescriptor>>(() =>
            {
                var names = new HashSet<string>(McpServer.RuntimeProfileEntries()
                    .Where(row => row!["profiles"]!.AsArray().Any(p => (string?)p == "lite"))
                    .Select(row => (string)row!["currentName"]!), StringComparer.Ordinal);
                return All.Values.Where(tool => names.Contains(tool.Name)).ToArray();
            });
            behaviorCapabilities = new Lazy<JsonArray>(() => TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(ToolCatalog).Assembly, McpServer.ReleaseKey));
        }

        private readonly ConcurrentDictionary<string, Lazy<(ToolDescriptor Descriptor, McpServerTool Tool)>> descriptors =
            new ConcurrentDictionary<string, Lazy<(ToolDescriptor, McpServerTool)>>(StringComparer.OrdinalIgnoreCase);
        private (ToolDescriptor Descriptor, McpServerTool Tool) DescriptorEntry(string name)
            => descriptors.GetOrAdd(name, key => new Lazy<(ToolDescriptor, McpServerTool)>(() =>
            {
                var method = Methods.First(pair => pair.Key == key).Value;
                var tool = McpServer.CreateInProcessTool(key, method, out var descriptor);
                return (descriptor, tool);
            })).Value;
        internal McpServerTool RuntimeTool(string name) => DescriptorEntry(name).Tool;
        private readonly Lazy<IReadOnlyDictionary<string, ToolDescriptor>> all;
        private readonly Lazy<IReadOnlyDictionary<string, ToolDescriptor>> includingUnavailable;
        private readonly Lazy<IReadOnlyList<ToolDescriptor>> lite;
        private readonly Lazy<JsonArray> behaviorCapabilities;
        public IReadOnlyDictionary<string, ToolDescriptor> All => all.Value;
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => includingUnavailable.Value;
        private IReadOnlyDictionary<string, ToolDescriptor> View(bool includeUnavailable)
            => Methods.Where(pair => includeUnavailable || McpServer.VersionToolProblem(pair.Key).Length == 0)
                .ToDictionary(pair => pair.Key, pair => DescriptorEntry(pair.Key).Descriptor, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ToolDescriptor> Lite => lite.Value;
        public ToolDescriptor? Find(string name, bool includeUnavailable = false)
        {
            var entry = Methods.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            return entry.Key == null || !includeUnavailable && McpServer.VersionToolProblem(entry.Key).Length != 0 ? null : DescriptorEntry(entry.Key).Descriptor;
        }
        public JsonArray BehaviorCapabilities => (JsonArray)behaviorCapabilities.Value.DeepClone();

        private static readonly McpServerToolCreateOptions DefaultOptions = new McpServerToolCreateOptions();
        private static readonly ConcurrentDictionary<(MethodInfo Method, string? Name, string? Description), Lazy<McpServerTool>> MetadataTools =
            new ConcurrentDictionary<(MethodInfo, string?, string?), Lazy<McpServerTool>>();
        internal static McpServerTool CreateTool(MethodInfo method, McpServerToolCreateOptions? options = null)
        {
            // Standard discovery calls differ only in name/description. The SDK
            // function resolves its instance from each request; it owns no session.
            // Custom provider, serializer and schema options retain separate tools.
            if (options == null || options.Title == null && options.Services == null && options.SchemaCreateOptions == null
                && options.SerializerOptions == null && options.Destructive == DefaultOptions.Destructive
                && options.Idempotent == DefaultOptions.Idempotent && options.OpenWorld == DefaultOptions.OpenWorld
                && options.ReadOnly == DefaultOptions.ReadOnly && options.UseStructuredContent == DefaultOptions.UseStructuredContent)
                return MetadataTools.GetOrAdd((method, options?.Name, options?.Description), _ => new Lazy<McpServerTool>(() =>
                {
                    string declaredName = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
                    if (options == null || options.Name != null && options.Name != declaredName) return CreateToolCore(method, options);
                    var source = CreateTool(method).ProtocolTool;
                    // Discovery prose does not require constructing the SDK's function
                    // and its parameter schema a second time for the same method.
                    return new SchemaHintedTool(CreateTool(method), new Tool {
                        Name = source.Name, Title = source.Title, Description = options.Description ?? source.Description,
                        InputSchema = source.InputSchema, OutputSchema = source.OutputSchema,
                        Annotations = source.Annotations, Meta = source.Meta,
                    });
                })).Value;
            return CreateToolCore(method, options);
        }

        private static McpServerTool CreateToolCore(MethodInfo method, McpServerToolCreateOptions? options)
            => DeclaredToolMetadata.CreateToolCore(method, options, request =>
                request.Services?.GetService(method.DeclaringType!) ?? EngineServices.Get(method.DeclaringType!));
    }
}
