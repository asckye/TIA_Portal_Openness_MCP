using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpenness.Contracts.Rpc
{
    /// <summary>Well-known JSON-RPC error codes plus the bridge's own range.</summary>
    public static class RpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>No TIA Portal session is open; call session.connect first.</summary>
        public const int NotConnected = -32000;
        /// <summary>A session exists but no project is open.</summary>
        public const int NoProjectOpen = -32001;
        /// <summary>Siemens.Engineering threw; Data carries the original type and message.</summary>
        public const int OpennessFailure = -32002;
        /// <summary>The environment cannot host Openness at all (see doctor.run).</summary>
        public const int EnvironmentUnusable = -32003;
        /// <summary>The open project exposes no Version Control Interface service.</summary>
        public const int VersionControlUnsupported = -32004;
    }

    /// <summary>A JSON-RPC request, newline-delimited on the bridge's stdin.</summary>
    public class RpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("method")] public string Method { get; set; }
        [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    }

    /// <summary>A JSON-RPC error payload.</summary>
    public class RpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("data"), JsonConverter(typeof(BridgeJson.PresentJsonElementConverter))] public JsonElement? Data { get; set; }
    }

    /// <summary>A JSON-RPC response, newline-delimited on the bridge's stdout.</summary>
    public class RpcResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(BridgeJson.PresentJsonElementConverter))] public JsonElement? Result { get; set; }
        [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RpcError Error { get; set; }

        public static RpcResponse Ok(string id, JsonElement? result)
        {
            return new RpcResponse { Id = id, Result = result ?? BridgeJson.ToElement(null) };
        }

        public static RpcResponse Fail(string id, int code, string message, JsonElement? data = null)
        {
            return new RpcResponse { Id = id, Error = new RpcError { Code = code, Message = message, Data = data } };
        }
    }

    /// <summary>
    /// An out-of-band notification pushed from the bridge to the client while a long
    /// call is running. Has no <c>id</c>, so clients must not wait for a reply.
    /// </summary>
    public class RpcNotification
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("method")] public string Method { get; set; }
        [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    }

    /// <summary>Payload of the <c>progress</c> notification.</summary>
    public class ProgressPayload
    {
        [JsonPropertyName("operation")] public string Operation { get; set; }
        [JsonPropertyName("current")] public int Current { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
    }

    /// <summary>Every method name the bridge understands, in one place.</summary>
    public static class RpcMethods
    {
        public const string Ping = "ping";
        public const string DoctorRun = "doctor.run";
        public const string SessionConnect = "session.connect";
        public const string SessionDisconnect = "session.disconnect";
        public const string SessionState = "session.state";

        public const string ProjectOpen = "project.open";
        public const string ProjectClose = "project.close";
        public const string ProjectSave = "project.save";
        public const string ProjectInfo = "project.info";

        public const string DeviceList = "device.list";
        public const string BlockList = "block.list";
        public const string BlockExport = "block.export";
        public const string BlockImport = "block.import";

        public const string TagTableList = "tag.tables";
        public const string TagList = "tag.list";

        public const string CompileDevice = "compile.device";
        public const string InspectProject = "inspect.run";

        // Version Control Interface (project capability).
        public const string VcSupported = "vc.supported";
        public const string VcWorkspaceList = "vc.workspaces";
        public const string VcWorkspaceCreate = "vc.workspace.create";
        public const string VcMapProject = "vc.map";
        public const string VcStatus = "vc.status";
        public const string VcSync = "vc.sync";
        public const string VcDiff = "vc.diff";

        /// <summary>Builds the Openness adapter against the local TIA installation.</summary>
    }
}
