using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.Versioning;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

// Every family operation that a release's PlcWorker dispatches (WorkerOperationModule over PlcFoundationEngine, or the
// PLCSIM Advanced adapter for F04) gets its handler's return type from the built worker binaries (bytes only, no load),
// and the host's reply check must accept that type's documented shape and refuse a bare number. A family operation whose
// reply type has no accepted shape fails with its name. Like ApiMetadataChecks it needs the built workers
// (src/PlcWorker/bin/<key>/Release/net48), so it runs in the foundation-api suite.
public sealed class FamilyReplyApiMetadataChecks
{
    public static IEnumerable<object[]> Operations() =>
        from release in TiaVersionCatalog.Runnable.Select(r => r.Key)
        from family in PortedFamilies.All.Where(f => f.Available(release))
        from name in WorkerOperations.FamilyNames(family.Name)
        select new object[] { release, family.OperationPrefix + "." + name };

    [Theory, MemberData(nameof(Operations))]
    public void Host_accepts_the_reply_type_of_every_family_operation(string release, string operation)
    {
        Assert.True(WorkerOperations.IsFamilyOperation(operation), operation);
        string returnType = Handlers.ReturnType(release, operation);
        if (FamilyReplyShapes.Reviewed.TryGetValue(operation, out var reason))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            return;
        }
        var samples = FamilyReplyShapes.For(operation, returnType);
        Assert.True(samples.Count > 0, operation + " (" + returnType + ") has no documented reply shape.");
        foreach (var (arguments, reply) in samples)
        {
            var error = Record.Exception(() => WorkerProtocol.ValidateExchangeResult(operation, arguments.DeepClone().AsObject(), reply?.DeepClone()));
            Assert.True(error == null, operation + " (" + returnType + ") rejects its documented reply " + (reply?.ToJsonString() ?? "null")
                + " with " + arguments.ToJsonString() + ": " + error?.Message);
        }
        Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult(operation, samples[0].Arguments.DeepClone().AsObject(), JsonValue.Create(12345)));
    }

    [Fact]
    public void Reviewed_exceptions_are_family_operations_of_some_release()
    {
        var all = Operations().Select(row => (string)row[1]).ToHashSet(StringComparer.Ordinal);
        Assert.All(FamilyReplyShapes.Reviewed.Keys, operation => Assert.Contains(operation, all));
        Assert.Single(FamilyReplyShapes.Reviewed);
    }

    // Return types of the public instance methods that WorkerOperationModule registers (it reflects DeclaredOnly).
    private static class Handlers
    {
        private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Engines = new();
        private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Simulators = new();

        internal static string ReturnType(string release, string operation)
        {
            string method = operation[(operation.IndexOf('.') + 1)..];
            bool simulator = operation.StartsWith("runtime-channels.", StringComparison.Ordinal);
            var table = simulator
                ? Read(Simulators, release, "TiaMcp.PlcSimAdvanced.dll", "TiaMcp.PlcWorker.Runtime", "PlcSimAdvancedAdapter")
                : Read(Engines, release, "TiaMcp.Adapter." + release + ".dll", "TiaMcp.Adapters", "PlcFoundationEngine");
            Assert.True(table.TryGetValue(method, out var type), release + " worker has no handler for " + operation);
            return type!;
        }

        private static IReadOnlyDictionary<string, string> Read(Dictionary<string, IReadOnlyDictionary<string, string>> cache, string release,
            string file, string ns, string typeName)
        {
            lock (cache)
            {
                if (cache.TryGetValue(release, out var known)) return known;
                string path = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                    "src/PlcWorker/bin", release, "Release/net48", file);
                Assert.True(File.Exists(path), "Build the PlcWorkers first (build-plc-workers): " + path);
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var type = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                    .Single(t => reader.GetString(t.Namespace) == ns && reader.GetString(t.Name) == typeName);
                var methods = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var handle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(handle);
                    if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (method.Attributes & MethodAttributes.Static) != 0)
                        continue;
                    string name = reader.GetString(method.Name);
                    string returns = method.DecodeSignature(new TypeNames(), null).ReturnType;
                    // WorkerOperationModule refuses overloaded operation names; ignore them here only if no family uses the name.
                    methods[name] = methods.ContainsKey(name) ? "<overloaded>" : returns;
                }
                return cache[release] = methods;
            }
        }
    }

    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "<function pointer>";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var declaring = type.GetDeclaringType();
            string name = Strip(reader.GetString(type.Name));
            return declaring.IsNil ? Qualify(reader.GetString(type.Namespace), name) : GetTypeFromDefinition(reader, declaring, rawTypeKind) + "+" + name;
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            string name = Strip(reader.GetString(type.Name));
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + name
                : Qualify(reader.GetString(type.Namespace), name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        private static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
        private static string Strip(string name) => name.IndexOf('`') is var tick and >= 0 ? name[..tick] : name;
    }
}

// The documented reply shape of each family reply type, as the worker writes it (WorkerJson), and the request arguments
// the host sees with it. Contract types are instantiated and serialized; the few untyped replies are written out here.
internal static class FamilyReplyShapes
{
    // Family operations whose reply is checked against the request elsewhere, with the reason.
    internal static readonly IReadOnlyDictionary<string, string> Reviewed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hardware-devices.HardwareDeviceCandidate"] = "Checked by CandidateWire.Device against the request's candidate identity before the family check "
            + "(WorkerProtocol.ValidateExchangeResult); a reply without a matching request cannot be built here. Covered by the device candidate tests.",
    };

    private static readonly JsonObject NoArguments = new();

    internal static IReadOnlyList<(JsonObject Arguments, JsonNode? Reply)> For(string operation, string returnType)
    {
        string name = operation[(operation.IndexOf('.') + 1)..];
        if (Untyped(operation) is { } untyped) return untyped;
        if (name is "ReadHardwareAddressing" or "UpdateHardwareAddress")
            return One(Step(new JsonObject { ["tool"] = name == "ReadHardwareAddressing" ? "GetDeviceAddressing" : "SetDeviceAddress", ["success"] = true }));
        return returnType switch
        {
            "System.Boolean" => new[] { (NoArguments, (JsonNode?)JsonValue.Create(true)), (NoArguments, (JsonNode?)JsonValue.Create(false)) },
            "System.String" => One(JsonValue.Create("fixture")),
            "System.Void" => One(null),
            "System.String[]" or "System.Collections.Generic.List<System.String>" => One(new JsonArray("fixture")),
            _ => Contract(returnType) is { } sample ? One(sample) : Array.Empty<(JsonObject, JsonNode?)>(),
        };
    }

    private static IReadOnlyList<(JsonObject, JsonNode?)> One(JsonNode? reply) => new[] { (NoArguments, reply) };

    private static JsonObject Step(JsonObject meta) => new() { ["Message"] = "fixture", ["Meta"] = meta, ["RequiresSessionReset"] = false, ["MayHaveChanged"] = false };

    // A contract type instantiated with its defaults; a null string member becomes "" and an empty Meta dictionary gets the
    // success flag that every family step sets. Collections of contract types get one such element.
    private static JsonNode? Contract(string typeName)
    {
        string element = typeName;
        bool list = false;
        foreach (string wrapper in new[] { "System.Collections.Generic.List<", "System.Collections.Generic.IReadOnlyList<", "System.Collections.Generic.IEnumerable<" })
            if (typeName.StartsWith(wrapper, StringComparison.Ordinal)) { element = typeName[wrapper.Length..^1]; list = true; }
        if (element.EndsWith("[]", StringComparison.Ordinal)) { element = element[..^2]; list = true; }
        var type = typeof(PortedFamilies).Assembly.GetType(element, false);
        if (type == null || type.GetConstructor(Type.EmptyTypes) == null) return null;
        object instance = Populate(Activator.CreateInstance(type)!);
        var json = JsonNode.Parse(WorkerJson.Serialize(instance));
        return list ? new JsonArray(json) : json;
    }

    private static object Populate(object instance)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            if (property.PropertyType == typeof(string) && property.GetValue(instance) == null) property.SetValue(instance, "");
            if (property.Name == "Meta" && typeof(IDictionary).IsAssignableFrom(property.PropertyType))
            {
                if (property.GetValue(instance) is not IDictionary meta)
                {
                    meta = (IDictionary)Activator.CreateInstance(property.PropertyType)!;
                    property.SetValue(instance, meta);
                }
                if (meta.Count == 0) meta["success"] = true;
            }
        }
        return instance;
    }

    // Untyped replies (object, JsonObject or Dictionary<string, object?> in the handler signature), written out from their producers.
    private static IReadOnlyList<(JsonObject, JsonNode?)>? Untyped(string operation)
    {
        JsonObject Request(string field, string value) => new() { ["request"] = new JsonObject { [field] = value } };
        JsonNode? Parse(string json) => JsonNode.Parse(json);
        switch (operation)
        {
            // OnlineChannels/Simulation/PlcSimAdvancedAdapter.cs: Ok, Message, Data and Meta, never RequiresSessionReset.
            case "runtime-channels.ExecutePlcSimAdvanced":
                return One(Parse("{\"Ok\":true,\"Message\":\"m\",\"Data\":{},\"Meta\":{\"success\":true}}"));
            // HardwareDevices.cs:818-827 and :1023-1027: the created device's name only, the candidates and the attempted identifiers.
            case "hardware-devices.HardwareCreateCatalogDevice" or "hardware-devices.HardwareCreateGsdDevice":
                return new[]
                {
                    (NoArguments, Parse("{\"Device\":{\"Name\":\"PLC_1\"},\"Candidate\":{\"TypeIdentifier\":\"t\"},\"Candidates\":[{\"TypeIdentifier\":\"t\"}],\"Attempts\":[\"t\"],\"Error\":null}")),
                    (NoArguments, Parse("{\"Device\":null,\"Candidate\":null,\"Candidates\":[],\"Attempts\":[],\"Error\":\"e\"}")),
                };
            case "hardware-devices.HardwareCreateDeviceFallback":
                return new[]
                {
                    (NoArguments, Parse("{\"Device\":{\"Name\":\"PLC_1\"},\"MlfbUsed\":\"6ES7\",\"VersionUsed\":\"V3.0\",\"Attempts\":[\"6ES7\"],\"Error\":null}")),
                    (NoArguments, Parse("{\"Device\":null,\"MlfbUsed\":null,\"VersionUsed\":null,\"Attempts\":[],\"Error\":\"e\"}")),
                };
            case "hardware-devices.HardwareLegacyDumpDeviceAttributes": return One(Parse("{\"found\":false}"));
            case "hardware-devices.HardwareReadPlugLocations": return new[] { (NoArguments, Parse("{\"free\":[],\"occupied\":[]}")), (NoArguments, (JsonNode?)null) };
            case "hardware-network.HardwareGetProjectTopology":
                return new[] { (NoArguments, Parse("{\"deviceCount\":0,\"devices\":[]}")), (NoArguments, Parse("{\"failureCode\":\"NO_PROJECT\"}")) };
            // SessionCallbackPrimitives.Snapshot.
            case "project-lifecycle.ReadProjectLifecycleBinding":
                return One(Parse("{\"identity\":null,\"fault\":null,\"instanceReserved\":false,\"verifiedLive\":false,\"note\":\"n\"}"));
            // SecurityWorker.ExecuteHardwareSecurity: PUT/GET replies are dictionaries; the two managed operations return SecurityResult.
            case "project-security.ExecuteHardwareSecurity":
                return new[]
                {
                    (Request("Operation", "GetPlcPutGetAccess"), Parse("{\"found\":false,\"message\":\"No project open.\"}")),
                    (Request("Operation", "GetPlcPutGetAccess"), Parse("{\"found\":false,\"device\":\"PLC_1\",\"message\":\"m\"}")),
                    (Request("Operation", "GetPlcPutGetAccess"), Parse("{\"found\":true,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"enabled\":true,\"rawValue\":\"True\"}")),
                    (Request("Operation", "SetPlcPutGetAccess"), Parse("{\"ok\":false,\"failureCode\":\"PROJECT_NOT_BOUND\",\"message\":\"m\"}")),
                    (Request("Operation", "SetPlcPutGetAccess"), Parse("{\"ok\":false,\"failureCode\":\"NOT_FOUND\",\"device\":\"PLC_1\",\"message\":\"m\"}")),
                    (Request("Operation", "SetPlcPutGetAccess"), Parse("{\"ok\":false,\"mayHaveChanged\":true,\"writeOutcomeKnown\":false,\"device\":\"PLC_1\",\"attributeName\":\"a\",\"message\":\"m\"}")),
                    (Request("Operation", "SetPlcPutGetAccess"), Parse("{\"ok\":true,\"mayHaveChanged\":true,\"writeOutcomeKnown\":true,\"device\":\"PLC_1\",\"deviceItem\":\"CPU\",\"attributeName\":\"a\",\"before\":\"False\",\"after\":\"True\",\"note\":\"n\"}")),
                    (Request("Operation", "ManagePlcProtection"), (JsonNode?)Step(new JsonObject { ["success"] = true })),
                    (Request("Operation", "ManageWatchForceTableWebAccess"), (JsonNode?)Step(new JsonObject { ["success"] = true })),
                };
            // PlcWorker/WorkerProjectScaffold.cs Execute, per primitive kind.
            case "project-lifecycle.ExecuteProjectScaffoldPrimitive":
                var rows = new List<(JsonObject, JsonNode?)>();
                foreach (string kind in new[] { "connect", "createProject", "save", "lad", "sclImport", "sclGenerate" })
                    rows.Add((Request("Kind", kind), Parse("{\"Message\":\"m\",\"Meta\":{\"success\":true}}")));
                rows.Add((Request("Kind", "addDevice"), Parse("{\"Ok\":true,\"DeviceName\":\"PLC_1\",\"Family\":\"S7-1500\",\"MlfbUsed\":\"6ES7\",\"VersionUsed\":\"V3.0\",\"Attempts\":[\"6ES7\"],\"Error\":null,\"Message\":\"m\",\"Meta\":{\"success\":true}}")));
                rows.Add((Request("Kind", "compile"), Parse("{\"State\":\"Success\",\"ErrorCount\":0,\"WarningCount\":0,\"Errors\":[],\"Warnings\":[],\"Info\":[],\"RawMessages\":[],\"Message\":\"m\",\"Meta\":{\"success\":true}}")));
                rows.Add((Request("Kind", "hmiInfo"), Parse("{\"Name\":\"HMI_RT_1\",\"ProgramType\":\"Unified\",\"Screens\":[],\"Message\":\"m\",\"Meta\":{\"success\":true}}")));
                rows.Add((Request("Kind", "hmiConnect"), Parse("{\"Meta\":{\"success\":true},\"Message\":\"m\",\"ObjectKind\":null,\"ObjectPath\":null,\"TypeName\":null,\"Members\":null}")));
                foreach (string kind in new[] { "hmiScreen", "hmiDesign", "hmiTag" })
                    rows.Add((Request("Kind", kind), Parse("{\"Message\":\"m\",\"Meta\":{\"success\":true}}")));
                return rows;
            default: return null;
        }
    }
}
