using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ReflectionPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<string> identity;
        internal ReflectionPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
        { this.call = call; this.identity = identity; }
        public ReflectionPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }

        private T Invoke<T>(OpennessReflectionRequest request)
        {
            // Preserve the original pure safety refusal before acquiring a native target.
            if (request.Operation is "InvokeObject" or "InvokeService" or "DescribeService")
            {
                var refusal = OpennessReflectionPolicy.ReflectionRefusal(request.Operation == "InvokeObject" ? null : request.ServiceTypeSuffix,
                    request.Operation == "DescribeService" ? null : request.MethodName);
                if (refusal != null) throw new PortalException(PortalErrorCode.InvalidState, refusal);
            }
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = !request.AllowWrite };
            if (request.AllowWrite) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
            var reply = call("openness-reflection.ExecuteOpennessReflection", arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing reflection worker reply.");
            if (reply["Failure"] is JsonObject failure)
            {
                string code = (string?)failure["Code"] ?? "";
                string message = (string?)failure["Message"] ?? "Reflection failed.";
                if (Enum.TryParse<PortalErrorCode>(code, out var nativeCode)) throw new PortalException(nativeCode, message,
                    failure["Candidates"] == null ? null : JsonSerializer.Deserialize<string[]>(failure["Candidates"]!.ToJsonString()));
                if (code == nameof(ArgumentException)) throw new ArgumentException(message);
                if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
                if (code == nameof(FormatException)) throw new FormatException(message);
                if (code == nameof(OverflowException)) throw new OverflowException(message);
                throw new InvalidOperationException(message);
            }
            return JsonSerializer.Deserialize<T>(reply["Observation"]!.ToJsonString())!;
        }
        private static HardwareScalar[]? Arguments(JsonArray? args) => args?.Select(a => {
            if (a == null) return new HardwareScalar();
            if (a is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text)) return new HardwareScalar { Kind = "string", Text = text };
                if (value.TryGetValue<int>(out var integer)) return new HardwareScalar { Kind = "int", Text = integer.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (value.TryGetValue<long>(out var wide)) return new HardwareScalar { Kind = "long", Text = wide.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (value.TryGetValue<double>(out var number)) return new HardwareScalar { Kind = "double", Text = number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) };
                if (value.TryGetValue<bool>(out var boolean)) return new HardwareScalar { Kind = "bool", Text = boolean.ToString() };
            }
            return new HardwareScalar { Kind = "string", Text = a.ToString() };
        }).ToArray();
        public ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
            => Invoke<ResponseObjectDescribe>(new OpennessReflectionRequest { Operation = "DescribeObject", ObjectKind = objectKind, ObjectPath = objectPath, SoftwarePath = softwarePath, Limit = maxMembers });

        public ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
            => Invoke<ResponseObjectDescribe>(new OpennessReflectionRequest { Operation = "DescribeObjectProperty", ObjectKind = objectKind, ObjectPath = objectPath, PropertyPath = propertyPath, SoftwarePath = softwarePath, Limit = maxMembers });

        public ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
            => Invoke<ResponseObjectValue>(new OpennessReflectionRequest { Operation = "GetObjectProperty", ObjectKind = objectKind, ObjectPath = objectPath, PropertyPath = propertyPath, SoftwarePath = softwarePath });

        public ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
            => Invoke<ResponseObjectChildren>(new OpennessReflectionRequest { Operation = "ListObjectChildren", ObjectKind = objectKind, ObjectPath = objectPath, PropertyPath = collectionProperty, SoftwarePath = softwarePath, Limit = limit });

        public ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
            => Invoke<ResponseObjectValue>(new OpennessReflectionRequest { Operation = "InvokeObject", ObjectKind = objectKind, ObjectPath = objectPath, MethodName = methodName, Arguments = Arguments(args), SoftwarePath = softwarePath, AllowWrite = allowWrite });

        public ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
            => Invoke<ResponseObjectDescribe>(new OpennessReflectionRequest { Operation = "DescribeService", ObjectKind = objectKind, ObjectPath = objectPath, ServiceTypeSuffix = serviceTypeSuffix, SoftwarePath = softwarePath, Limit = maxMembers });

        public ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
            => Invoke<ResponseObjectValue>(new OpennessReflectionRequest { Operation = "InvokeService", ObjectKind = objectKind, ObjectPath = objectPath, ServiceTypeSuffix = serviceTypeSuffix, MethodName = methodName, Arguments = Arguments(args), SoftwarePath = softwarePath, AllowWrite = allowWrite });

    }
}
