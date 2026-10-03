using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Rpc;
using TiaOpenness.Core.Mock;

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
    /// Owns the bridge child process and multiplexes JSON-RPC calls over its stdio.
    /// One instance per TIA Portal session; create a second one to drive a second
    /// Openness version, since a bridge process can only ever bind one.
    /// </summary>
    public sealed class BridgeClient : IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>>();

        private readonly JsonSerializerSettings _json = BridgeJson.Settings;

        private readonly object _writeGate = new object();
        private Process _process;
        private Thread _readerThread;
        private int _nextId;
        private volatile bool _disposed;
        private volatile bool _faulted;
        private RpcDispatcher _mock;
        private readonly string[] _args;
        private bool _nativeMock;
        public bool IsMock => _mock != null || _nativeMock;
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
                if (_mock != null) return true;
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
            if (forceMock && bridgeExePath == null)
            {
                _mock = new RpcDispatcher(() => new MockTiaSessionFactory(), n => DispatchNotification(JObject.FromObject(n)));
                Log?.Invoke(this, new BridgeLogEventArgs { Line = "Explicit in-process mock; no Siemens API loaded." });
                return;
            }

            var exe = bridgeExePath ?? LocateBridge();
            if (exe == null || !File.Exists(exe))
            {
                throw new FileNotFoundException(
                    "TiaOpenness.Bridge.exe was not found. Build the solution, or pass an explicit path.",
                    exe ?? "TiaOpenness.Bridge.exe");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = forceMock ? "--mock" : NativeArguments(opennessVersion),
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

            _nativeMock = forceMock;
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += (s, e) =>
            {
                _faulted = true;
                FailAllPending(new IOException("The native bridge exited; an in-flight operation may have changed the project."));
                Exited?.Invoke(this, EventArgs.Empty);
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Log?.Invoke(this, new BridgeLogEventArgs { Line = e.Data });
            };

            _process.Start();
            _process.BeginErrorReadLine();

            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "tia-bridge-reader" };
            _readerThread.Start();
        }

        /// <summary>Looks for the bridge next to the caller, then in the usual build output folders.</summary>
        public static string LocateBridge()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "TiaOpenness.Bridge.exe"),
                Path.Combine(baseDir, "bridge", "TiaOpenness.Bridge.exe"),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\TiaOpenness.Bridge\bin\Debug\net48\TiaOpenness.Bridge.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\TiaOpenness.Bridge\bin\Release\net48\TiaOpenness.Bridge.exe")),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
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
            if (response.Result == null || response.Result.Type == JTokenType.Null) return default;
            return response.Result.ToObject<T>(JsonSerializer.Create(_json));
        }

        public async Task<RpcResponse> CallRawAsync(string method, object parameters = null,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_faulted) throw new InvalidOperationException("The previous native call has no reliable outcome. Restart Studio and inspect the project before retrying.");
            if (!IsRunning) throw new InvalidOperationException("The bridge is not running. Call Start first.");

            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new RpcRequest
            {
                Id = id,
                Method = method,
                Params = parameters == null ? new JObject() : JObject.FromObject(parameters, JsonSerializer.Create(_json)),
            };

            if (_mock != null) return _mock.Handle(request);

            var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            try
            {
                var payload = JsonConvert.SerializeObject(request, _json);
                lock (_writeGate)
                {
                    _process.StandardInput.Write(payload);
                    _process.StandardInput.Write('\n');
                    _process.StandardInput.Flush();
                }

                using (var timeout = new CancellationTokenSource(DefaultTimeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token))
                {
                    var finished = await Task.WhenAny(
                        completion.Task,
                        Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

                    if (finished != completion.Task)
                    {
                        _faulted = true;
                        throw timeout.IsCancellationRequested
                            ? new TimeoutException("The bridge did not answer '" + method + "' within " + DefaultTimeout + ".")
                            : (Exception)new OperationCanceledException(cancellation);
                    }
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                TaskCompletionSource<RpcResponse> ignored;
                _pending.TryRemove(id, out ignored);
            }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    JObject frame;
                    try { frame = JObject.Parse(line); }
                    catch (JsonException) { throw new IOException("Invalid native bridge response."); }

                    var id = frame.Value<string>("id");
                    if (string.IsNullOrEmpty(id))
                    {
                        DispatchNotification(frame);
                        continue;
                    }

                    TaskCompletionSource<RpcResponse> completion;
                    if (_pending.TryGetValue(id, out completion))
                    {
                        completion.TrySetResult(frame.ToObject<RpcResponse>(JsonSerializer.Create(_json)));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    _faulted = true;
                    FailAllPending(ex);
                }
            }
        }

        private void DispatchNotification(JObject frame)
        {
            if (!string.Equals(frame.Value<string>("method"), "progress", StringComparison.Ordinal)) return;

            var payload = frame["params"];
            if (payload == null) return;

            Progress?.Invoke(this, new ProgressEventArgs
            {
                Progress = payload.ToObject<ProgressPayload>(JsonSerializer.Create(_json)),
            });
        }

        private void FailAllPending(Exception error)
        {
            foreach (var key in _pending.Keys)
            {
                TaskCompletionSource<RpcResponse> completion;
                if (_pending.TryRemove(key, out completion)) completion.TrySetException(error);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _mock?.Dispose();
            _mock = null;

            try
            {
                if (IsRunning)
                {
                    // Closing stdin makes the bridge's read loop end and the session dispose cleanly.
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(5000)) _process.Kill();
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
            Data2 = error.Data?.ToString();
        }

        public string Method { get; }
        public int Code { get; }
        /// <summary>Extra diagnostic payload; named to avoid colliding with <see cref="Exception.Data"/>.</summary>
        public string Data2 { get; }
    }
}
