// Frozen pre-migration dispatcher and Newtonsoft settings for wire compatibility tests.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;
using TiaOpenness.Core.Inspection;

namespace TiaOpenness.Core.Tests.Legacy
{
    /// <summary>Turns one JSON-RPC request into one response, against the live session.</summary>
    public sealed class RpcDispatcher : IDisposable
    {
        private readonly Func<ITiaSessionFactory> _resolveFactory;
        private ITiaSessionFactory _factory;
        private readonly Action<RpcNotification> _notify;
        private readonly JsonSerializer _serializer;
        private ITiaSession _session;
        internal bool BackendEntered { get; private set; }

        /// <param name="resolveFactory">
        /// Called once when the first operation needs a backend; the bridge keeps that factory.
        /// </param>
        public RpcDispatcher(Func<ITiaSessionFactory> resolveFactory, Action<RpcNotification> notify)
        {
            _resolveFactory = resolveFactory;
            _notify = notify;
            _serializer = JsonSerializer.Create(BridgeJson.Settings);
        }

        private ITiaSessionFactory Factory()
        {
            if (_factory == null) _factory = _resolveFactory();
            return _factory;
        }

        public RpcResponse Handle(RpcRequest request, Action<RpcNotification> notify = null)
        {
            BackendEntered = false;
            try
            {
                var result = Invoke(request.Method, request.Params ?? new JObject(),
                    (operation, current, total, message) => Progress(notify ?? _notify, operation, current, total, message));
                return RpcResponse.Ok(request.Id, result == null ? JValue.CreateNull() : JToken.FromObject(result, _serializer));
            }
            catch (OpennessNotInstalledException ex)
            {
                return RpcResponse.Fail(request.Id, RpcErrorCodes.EnvironmentUnusable, ex.Message);
            }
            catch (OpennessUnavailableException ex)
            {
                return RpcResponse.Fail(request.Id, RpcErrorCodes.EnvironmentUnusable, ex.Message);
            }
            catch (VersionControlUnsupportedException ex)
            {
                return RpcResponse.Fail(request.Id, RpcErrorCodes.VersionControlUnsupported, ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return RpcResponse.Fail(request.Id, RpcErrorCodes.MethodNotFound, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return RpcResponse.Fail(request.Id, RpcErrorCodes.InvalidParams, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                var code = ex.Message.IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0
                    ? RpcErrorCodes.NotConnected
                    : ex.Message.IndexOf("No project", StringComparison.OrdinalIgnoreCase) >= 0
                        ? RpcErrorCodes.NoProjectOpen
                        : RpcErrorCodes.InternalError;
                return RpcResponse.Fail(request.Id, code, ex.Message);
            }
            catch (Exception ex)
            {
                var data = new JObject
                {
                    ["type"] = ex.GetType().FullName,
                    ["stack"] = ex.StackTrace,
                };
                if (ex.InnerException != null) data["inner"] = ex.InnerException.Message;
                return RpcResponse.Fail(request.Id, RpcErrorCodes.OpennessFailure, ex.Message, data);
            }
        }

        private object Invoke(string method, JObject p, ProgressCallback progress)
        {
            switch (method)
            {
                case RpcMethods.Ping:
                    return new { pong = true, mode = Factory().Mode.ToString(), decision = SessionFactoryLoader.LastDecision };

                case RpcMethods.DoctorRun:
                    return OpennessDoctor.Run();

                case RpcMethods.SessionConnect:
                    EnsureSession();
                    return _session.Connect(
                        Bool(p, "withUserInterface", true),
                        Bool(p, "attachToRunning", true),
                        Str(p, "version", null));

                case RpcMethods.SessionDisconnect:
                    if (_session != null) { BackendEntered = true; _session.Dispose(); _session = null; }
                    return new SessionState { Connected = false, Mode = Factory().Mode };

                case RpcMethods.SessionState:
                    return _session == null
                        ? new SessionState { Connected = false, Mode = Factory().Mode }
                        : Session().GetState();

                case RpcMethods.ProjectOpen:
                    return Session().OpenProject(Required(p, "path"));

                case RpcMethods.ProjectInfo:
                    return Session().GetProjectInfo();

                case RpcMethods.ProjectSave:
                    Session().SaveProject();
                    return new { saved = true };

                case RpcMethods.ProjectClose:
                    Session().CloseProject();
                    return new { closed = true };

                case RpcMethods.DeviceList:
                    return Session().ListDevices();

                case RpcMethods.BlockList:
                    return Session().ListBlocks(Required(p, "deviceId"), Bool(p, "includeSystemBlocks", false));

                case RpcMethods.BlockExport:
                    return Session().ExportBlocks(
                        Required(p, "deviceId"),
                        StrList(p, "blocks"),
                        Required(p, "outputDirectory"),
                        Enum(p, "format", ExportFormat.SimaticMl),
                        Bool(p, "preserveFolders", true),
                        progress);

                case RpcMethods.BlockImport:
                    return Session().ImportBlocks(
                        Required(p, "deviceId"),
                        StrList(p, "files"),
                        Bool(p, "overwrite", false),
                        progress);

                case RpcMethods.TagTableList:
                    return Session().ListTagTables(Required(p, "deviceId"));

                case RpcMethods.TagList:
                    return Session().ListTags(Required(p, "deviceId"), Str(p, "tableName", null));

                case RpcMethods.CompileDevice:
                    return Session().CompileDevice(Required(p, "deviceId"), Bool(p, "softwareOnly", true));

                case RpcMethods.VcSupported:
                    return new { supported = Session().VersionControl != null };

                case RpcMethods.VcWorkspaceList:
                    return VersionControl().ListWorkspaces();

                case RpcMethods.VcWorkspaceCreate:
                    return VersionControl().CreateWorkspace(Required(p, "name"), Required(p, "folderPath"));

                case RpcMethods.VcMapProject:
                    return VersionControl().MapProject(
                        Str(p, "workspaceName", null),
                        Str(p, "deviceId", null),
                        Bool(p, "dryRun", true),
                        progress);

                case RpcMethods.VcDiff:
                    return Diff(Str(p, "workspaceName", null), Str(p, "file", null));

                case RpcMethods.VcStatus:
                    return VersionControl().GetStatus(Str(p, "workspaceName", null), Bool(p, "changedOnly", true));

                case RpcMethods.VcSync:
                    return VersionControl().Sync(
                        Str(p, "workspaceName", null),
                        Enum(p, "direction", SyncDirection.ProjectToWorkspace),
                        Bool(p, "dryRun", true),
                        progress);

                case RpcMethods.InspectProject:
                    return Session().Inspect(Required(p, "deviceId"), new InspectionOptions
                    {
                        BlockNamePattern = Str(p, "blockNamePattern", null),
                        RequireBlockComment = Bool(p, "requireBlockComment", true),
                        FindUnusedBlocks = Bool(p, "findUnusedBlocks", true),
                        FlagInconsistentBlocks = Bool(p, "flagInconsistentBlocks", true),
                    });

                default:
                    throw new NotSupportedException("Unknown method '" + method + "'. Known methods: " +
                        string.Join(", ", KnownMethods()));
            }
        }

        private static void Progress(Action<RpcNotification> notify, string operation, int current, int total, string message)
        {
            if (notify == null) return;
            notify(new RpcNotification
            {
                Method = "progress",
                Params = JObject.FromObject(new ProgressPayload
                {
                    Operation = operation,
                    Current = current,
                    Total = total,
                    Message = message,
                }, JsonSerializer.Create(BridgeJson.Settings)),
            });
        }

        private void EnsureSession()
        {
            if (_session == null)
            {
                var factory = Factory();
                BackendEntered = !(factory is UnavailableSessionFactory);
                _session = factory.Create();
            }
            BackendEntered = true;
        }

        private ITiaSession Session()
        {
            if (_session == null) throw new InvalidOperationException("Not connected. Call session.connect first.");
            BackendEntered = true;
            return _session;
        }

        /// <summary>
        /// Reads the workspace's uncommitted changes. The workspace is resolved through version
        /// control rather than taken from the caller, so the diff always belongs to the workspace
        /// the operator is actually looking at.
        /// </summary>
        private WorkspaceDiff Diff(string workspaceName, string file)
        {
            var workspaces = VersionControl().ListWorkspaces();

            var workspace = string.IsNullOrWhiteSpace(workspaceName)
                ? workspaces.FirstOrDefault()
                : workspaces.FirstOrDefault(w =>
                    string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase));

            if (workspace == null)
            {
                throw new InvalidOperationException("This project has no version control workspace yet.");
            }

            return GitWorkspaceDiff.Read(workspace.Name, workspace.RootPath, file);
        }

        /// <summary>
        /// The project's Version Control Interface, if exposed by this project and installation.
        /// </summary>
        private IVersionControl VersionControl()
        {
            var versionControl = Session().VersionControl;
            if (versionControl == null)
            {
                throw new VersionControlUnsupportedException();
            }
            return versionControl;
        }

        private static IEnumerable<string> KnownMethods()
        {
            return typeof(RpcMethods)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue())
                .OrderBy(s => s, StringComparer.Ordinal);
        }

        // ---- parameter helpers ---------------------------------------------

        private static string Required(JObject p, string name)
        {
            var token = p[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new ArgumentException("Missing required parameter '" + name + "'.");
            }
            var value = token.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Parameter '" + name + "' must not be empty.");
            }
            return value;
        }

        private static string Str(JObject p, string name, string fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<string>();
        }

        private static bool Bool(JObject p, string name, bool fallback)
        {
            var token = p[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }

        private static IReadOnlyList<string> StrList(JObject p, string name)
        {
            var token = p[name];
            if (token == null || token.Type == JTokenType.Null) return new string[0];
            if (token.Type == JTokenType.String) return new[] { token.Value<string>() };
            return token.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        private static T Enum<T>(JObject p, string name, T fallback) where T : struct
        {
            var raw = Str(p, name, null);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            T parsed;
            if (!System.Enum.TryParse(raw, true, out parsed))
            {
                throw new ArgumentException("Parameter '" + name + "' must be one of: " +
                    string.Join(", ", System.Enum.GetNames(typeof(T))));
            }
            return parsed;
        }

        public void Dispose()
        {
            if (_session != null) { _session.Dispose(); _session = null; }
        }
    }

    /// <summary>Serializer settings shared by the bridge and its clients.</summary>
    public static class BridgeJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.DateTimeOffset,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
            Formatting = Formatting.None,
        };
    }
}
