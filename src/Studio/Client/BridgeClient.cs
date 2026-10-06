using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Rpc;
using TiaMcp.WorkerChannel;
using TiaOpenness.Core.Environment;

namespace TiaOpenness.Client
{
    /// <summary>Raised for every <c>progress</c> notification the bridge pushes mid-call.</summary>
    public class ProgressEventArgs : EventArgs
    {
        public ProgressPayload Progress { get; set; }
    }

    /// <summary>Raised for every line the bridge writes to stderr.</summary>
    public class BridgeLogEventArgs : EventArgs
    {
        public string Line { get; set; }
    }

    /// <summary>
    /// Owns the bridge child process and sends one verified channel request at a time.
    /// One instance per TIA Portal session; create a second one to drive a second
    /// Openness version, since a bridge process can only ever bind one.
    /// </summary>
    public sealed class BridgeClient : IDisposable
    {
        private Process _process;
        private ChannelClient _channel;
        private volatile bool _disposed;
        private volatile bool _faulted;
        private readonly string[] _args;
        private bool _nativeMock;
        private bool _attachAttempted;
        public bool IsMock => _nativeMock;
        public BridgeClient() : this(System.Environment.GetCommandLineArgs()) { }
        public BridgeClient(string[] args) { _args = args ?? Array.Empty<string>(); }

        public event EventHandler<ProgressEventArgs> Progress;
        public event EventHandler<BridgeLogEventArgs> Log;
        public event EventHandler Exited;

        /// <summary>Default per-call timeout. Openness calls on a large project are slow; be generous.</summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public bool IsRunning
        {
            get
            {
                if (_disposed) return false;
                try { return _process != null && !_process.HasExited; }
                catch (InvalidOperationException) /* swallow(env-probe): a process object without an associated process means the bridge is not running */ { return false; }
            }
        }

        /// <summary>Launches the bridge executable.</summary>
        /// <param name="bridgeExePath">Path to TiaOpenness.Bridge.exe. Null uses <see cref="LocateBridge"/>.</param>
        /// <param name="forceMock">Pass --mock so the bridge never touches Siemens.Engineering.</param>
        public void Start(string bridgeExePath = null, bool forceMock = false, string opennessVersion = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BridgeClient));
            if (_faulted) throw new InvalidOperationException("The native bridge session failed. Restart Studio before making another native call.");
            if (IsRunning)
            {
                if (IsMock != forceMock) throw new InvalidOperationException("Restart Studio to change backend mode.");
                return;
            }
            string[] remaining;
            var explicitRoot = TiaOpenness.Shared.BundleLayout.ExtractRootOption(_args, out remaining);
            var root = TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, explicitRoot);
            var exe = bridgeExePath ?? TiaOpenness.Shared.BundleLayout.RequireWorkbenchBridge(AppContext.BaseDirectory, root);
            if (exe == null || !File.Exists(exe))
            {
                throw new FileNotFoundException(
                    "TiaOpenness.Bridge.exe was not found. Build the solution, or pass an explicit path.",
                    exe ?? "TiaOpenness.Bridge.exe");
            }

            var arguments = NativeArguments(opennessVersion);
            var version = arguments.Length == 0 ? (OpennessLocator.Resolve(null)?.Version ?? "21") : arguments.Substring("--openness-version ".Length);
            var release = TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(version);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var bridgeHash = BridgeChannel.Hash(exe);
            var adapterHash = BridgeChannel.Hash(BridgeChannel.AdapterPath(Path.GetDirectoryName(exe), release.Key, forceMock));
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(exe),
            };

            startInfo.ArgumentList.Add("--bundle-root");
            startInfo.ArgumentList.Add(root);
            if (forceMock) startInfo.ArgumentList.Add("--mock");
            startInfo.ArgumentList.Add("--openness-version");
            startInfo.ArgumentList.Add(release.ApiVersion);
            startInfo.ArgumentList.Add("--nonce");
            startInfo.ArgumentList.Add(nonce);
            int publicApi = Array.IndexOf(_args, "--public-api");
            if (publicApi >= 0)
            {
                if (publicApi + 1 >= _args.Length) throw new ArgumentException("--public-api requires a local SDK directory.");
                startInfo.ArgumentList.Add("--public-api");
                startInfo.ArgumentList.Add(Path.GetFullPath(_args[publicApi + 1]));
            }

            _nativeMock = forceMock;
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += (s, e) =>
            {
                _faulted = true;
                Exited?.Invoke(this, EventArgs.Empty);
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Log?.Invoke(this, new BridgeLogEventArgs { Line = e.Data });
            };

            try
            {
                _process.Start();
                _attachAttempted = false;
                _process.BeginErrorReadLine();
                _channel = new ChannelClient(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream,
                    new ChannelIdentity(release.Key, bridgeHash, adapterHash, _process.Id, nonce), ChannelProfile.Studio,
                    payload => Progress?.Invoke(this, new ProgressEventArgs
                    {
                        Progress = BridgeJson.DeserializeClient<ProgressPayload>(BridgeJson.Deserialize<JsonElement>(payload)),
                    }));
                _channel.ConnectAsync(DefaultTimeout).GetAwaiter().GetResult();
            }
            catch
            {
                _faulted = true;
                Dispose();
                throw;
            }
        }

        /// <summary>Locates the bridge at the selected bundle's formal installation or development output.</summary>
        public static string LocateBridge()
        {
            return LocateBridge(AppDomain.CurrentDomain.BaseDirectory);
        }

        internal static string LocateBridge(string baseDir)
        {
            return TiaOpenness.Shared.BundleLayout.RequireWorkbenchBridge(baseDir);
        }

        private string NativeArguments(string selectedVersion)
        {
            int index = Array.IndexOf(_args, "--openness-version");
            if (string.IsNullOrEmpty(selectedVersion) && index >= 0)
            {
                if (index + 1 >= _args.Length) throw new ArgumentException("--openness-version requires an exact release key.");
                selectedVersion = _args[index + 1];
            }
            if (string.IsNullOrEmpty(selectedVersion)) return string.Empty;
            var release = TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(selectedVersion);
            return "--openness-version " + release.ApiVersion;
        }

        /// <summary>Sends one request and waits for its response.</summary>
        /// <exception cref="BridgeRpcException">The bridge answered with an error object.</exception>
        public async Task<T> CallAsync<T>(string method, object parameters = null, CancellationToken cancellation = default)
        {
            var response = await CallRawAsync(method, parameters, cancellation).ConfigureAwait(false);
            if (response.Error != null) throw new BridgeRpcException(method, response.Error);
            if (response.Result == null || response.Result.Value.ValueKind == JsonValueKind.Null) return default;
            return BridgeJson.DeserializeClient<T>(response.Result.Value);
        }

        public async Task<RpcResponse> CallRawAsync(string method, object parameters = null,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_faulted) throw new InvalidOperationException("The previous native call has no reliable outcome. Restart Studio and inspect the project before retrying.");
            if (!IsRunning) throw new InvalidOperationException("The bridge is not running. Call Start first.");

            var payload = BridgeJson.ToElement(parameters ?? new { });
            var id = checked(_channel.LastRequestId + 1).ToString(CultureInfo.InvariantCulture);
            bool firstAttach = method == "session.connect" && !_attachAttempted;
            if (method == "session.connect") _attachAttempted = true;
            try
            {
                var json = await _channel.CallAsync(method, payload.GetRawText(),
                    BridgeChannel.BindingChangeFor(method), BridgeChannel.IsReadOnly(method), DefaultTimeout, cancellation,
                    firstAttach: firstAttach).ConfigureAwait(false);
                return RpcResponse.Ok(id,
                    BridgeJson.Deserialize<JsonElement>(json));
            }
            catch (ChannelFailure ex)
            {
                return new RpcResponse
                {
                    Id = id,
                    Error = BridgeJson.DeserializeClient<RpcError>(BridgeJson.Deserialize<JsonElement>(ex.RpcErrorJson)),
                };
            }
            catch (ChannelFault ex)
            {
                _faulted = true;
                if (ex.InnerException is TimeoutException)
                {
                    string message = "The bridge did not answer '" + method + "' within " + DefaultTimeout + ".";
                    if (firstAttach)
                        message += "\n首次附着超时：TIA 可能正在等待确认 Openness 访问。请在博途机器上核对应用程序，并选择“是”或“全部是”。" +
                            "\nFirst attach timed out: TIA may be waiting for Openness access confirmation. Check the application on the TIA machine and choose ‘Yes’ or ‘Yes to all’.";
                    throw new TimeoutException(message, ex);
                }
                if (ex.InnerException is OperationCanceledException && cancellation.IsCancellationRequested)
                    throw new OperationCanceledException(cancellation);
                if (_process?.HasExited == true)
                    throw new IOException("The native bridge exited; an in-flight operation may have changed the project.", ex);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                try { _channel?.Dispose(); }
                finally
                {
                    if (IsRunning)
                    {
                        // Closing stdin lets the STA loop dispose the session; retain
                        // the existing five-second grace period before killing it.
                        if (!_process.WaitForExit(5000)) _process.Kill();
                    }
                }
            }
            catch (Exception) /* swallow(teardown): bridge exit can race stdin close or kill; the finally block still releases the process handle */
            {
                // Nothing useful to do while tearing down.
            }
            finally
            {
                _disposed = true;
                _process?.Dispose();
                _process = null;
            }
        }
    }

    /// <summary>The bridge answered a call with a JSON-RPC error object.</summary>
    public class BridgeRpcException : Exception
    {
        public BridgeRpcException(string method, RpcError error)
            : base(method + " failed (" + error.Code + "): " + error.Message)
        {
            Method = method;
            Code = error.Code;
            Data2 = BridgeJson.DiagnosticText(error.Data);
        }

        public string Method { get; }
        public int Code { get; }
        /// <summary>Extra diagnostic payload; named to avoid colliding with <see cref="Exception.Data"/>.</summary>
        public string Data2 { get; }
    }
}
