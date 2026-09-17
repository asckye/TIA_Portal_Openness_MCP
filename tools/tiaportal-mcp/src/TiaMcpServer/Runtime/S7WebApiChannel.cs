using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Siemens.Simatic.S7.Webserver.API.Enums;
using Siemens.Simatic.S7.Webserver.API.Exceptions;
using Siemens.Simatic.S7.Webserver.API.Services.IdGenerator;
using Siemens.Simatic.S7.Webserver.API.Services.RequestHandling;

namespace TiaMcpServer.Runtime
{
    // RUNTIME channel to the SIMATIC S7 Web server API (S7-1500 FW >= 2.9, S7-1200 G2,
    // ET 200SP CPUs, Software Controller, PLCSIM Advanced) through the official MIT client
    // Siemens.Simatic.S7.Webserver.API. This is HTTPS JSON-RPC on the CPU itself and has
    // nothing to do with TIA Openness.
    //
    // Session policy: one authenticated HttpClient per host+user is cached and reused (the
    // PLC limits concurrent sessions and login is comparatively slow). The password is
    // used only to (re)login and is never stored in the cache, meta or logs. A stale token
    // (HTTP 401 after the CPU's session timeout) triggers exactly one silent re-login.
    //
    // Certificate policy: the CPU certificate is validated by default. Only when the caller
    // passes ignoreCertificateErrors=true is a handler-local callback installed that accepts
    // any certificate; the process-wide ServicePointManager callback is never touched.

    public sealed class S7WebVarItem
    {
        public string Name = "";
        public JsonNode? Value;
        public string? Error;
    }

    public sealed class S7WebReadResult
    {
        public bool Ok;
        public string? Error;
        public bool ReusedSession;
        public long ElapsedMs;
        public List<S7WebVarItem> Items = new List<S7WebVarItem>();
    }

    public sealed class S7WebWriteItem
    {
        public string Name = "";
        public JsonNode? Requested;
        public JsonNode? Before;
        public string? BeforeError;
        public bool WriteAttempted;
        public bool WriteAccepted;
        public JsonNode? After;
        public bool Verified;
        public string? Error;
    }

    public sealed class S7WebWriteResult
    {
        public bool Ok;
        public string? Error;
        public bool ReusedSession;
        public long ElapsedMs;
        public List<S7WebWriteItem> Items = new List<S7WebWriteItem>();
    }

    public sealed class S7WebDiagnosticsResult
    {
        public bool Ok;
        public string? Error;
        public bool ReusedSession;
        public long ElapsedMs;
        public string? Ping;
        public double? ApiVersion;
        public string? OperatingMode;
        public string? ModeSelector;
        public JsonNode? CpuType;
        public string? SystemTimeUtc;
        public JsonNode? RuntimeInformation;
        public JsonNode? MemoryInformation;
        public List<string> Unavailable = new List<string>();   // "<call>: <reason>" for calls this CPU/user does not support
    }

    public sealed class S7WebModeChangeResult
    {
        public bool Ok;
        public string? Error;
        public bool ReusedSession;
        public long ElapsedMs;
        public string? Before;
        public bool RequestAttempted;
        public bool RequestAccepted;
        public string? After;
        public bool Verified;
        public int PollCount;
    }

    public static class S7WebApiChannel
    {
        private sealed class Session
        {
            public HttpClient Http = null!;
            public ApiHttpClientRequestHandler Handler = null!;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Session> Sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private static readonly Dictionary<string, SemaphoreSlim> Locks = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private static SemaphoreSlim LockFor(string key)
        {
            lock (Gate)
            {
                if (!Locks.TryGetValue(key, out var s)) { s = new SemaphoreSlim(1, 1); Locks[key] = s; }
                return s;
            }
        }

        // --- public entry points (synchronous; the MCP tool layer is synchronous) -----

        public static S7WebReadResult ReadVars(string host, string username, string password, List<string> vars, bool ignoreCertificateErrors, int timeoutMs)
        {
            var result = new S7WebReadResult();
            Run(host, username, password, ignoreCertificateErrors, timeoutMs, result, async (h, ct) =>
            {
                foreach (var name in vars)
                    result.Items.Add(await ReadOneAsync(h, name, ct));
                result.Ok = true;
            }, (r, reused) => r.ReusedSession = reused, (r, err) => r.Error = err, (r, ms) => r.ElapsedMs = ms);
            return result;
        }

        // preview=true: only reads the current values (no PlcProgram.Write is ever sent).
        public static S7WebWriteResult WriteVars(string host, string username, string password, List<RuntimeWriteItem> writes, bool ignoreCertificateErrors, int timeoutMs, bool preview)
        {
            var result = new S7WebWriteResult();
            Run(host, username, password, ignoreCertificateErrors, timeoutMs, result, async (h, ct) =>
            {
                foreach (var w in writes)
                {
                    var item = new S7WebWriteItem { Name = w.Name, Requested = w.Value?.DeepClone() };
                    var before = await ReadOneAsync(h, w.Name, ct);
                    item.Before = before.Value; item.BeforeError = before.Error;
                    result.Items.Add(item);
                }
                if (preview) { result.Ok = true; return; }

                foreach (var item in result.Items)
                {
                    var w = writes.First(x => x.Name == item.Name);
                    if (item.BeforeError != null)
                    {
                        item.Error = "Not written: the variable could not be read before writing (" + item.BeforeError + ").";
                        continue;
                    }
                    item.WriteAttempted = true;
                    try
                    {
                        var resp = await h.PlcProgramWriteAsync(w.Name, w.ClrValue, null, ct);
                        item.WriteAccepted = resp?.Result == true;
                        if (!item.WriteAccepted) { item.Error = "PlcProgram.Write did not return true."; continue; }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        item.Error = "PlcProgram.Write failed: " + Describe(ex);
                        continue;
                    }
                    var after = await ReadOneAsync(h, w.Name, ct);
                    item.After = after.Value;
                    if (after.Error != null) { item.Error = "Readback failed after write: " + after.Error; continue; }
                    item.Verified = RuntimeChannelsLogic.ValuesMatch(item.Requested, item.After);
                    if (!item.Verified)
                        item.Error = $"Readback differs from the requested value (requested {item.Requested?.ToJsonString()}, read {item.After?.ToJsonString()}); the PLC program may overwrite this variable cyclically.";
                }
                result.Ok = result.Items.All(i => i.Error == null);
            }, (r, reused) => r.ReusedSession = reused, (r, err) => r.Error = err, (r, ms) => r.ElapsedMs = ms);
            return result;
        }

        public static S7WebDiagnosticsResult ReadDiagnostics(string host, string username, string password, bool ignoreCertificateErrors, int timeoutMs)
        {
            var result = new S7WebDiagnosticsResult();
            Run(host, username, password, ignoreCertificateErrors, timeoutMs, result, async (h, ct) =>
            {
                // Operating mode is the one call that must succeed; everything else is
                // best-effort and reported in Unavailable when the CPU/user lacks it.
                result.OperatingMode = (await h.PlcReadOperatingModeAsync(ApiPlcRedundancyId.StandardPLC, ct)).Result.ToString();
                result.Ok = true;

                await Try(result, "Api.Ping", async () => result.Ping = (await h.ApiPingAsync(ct)).Result);
                await Try(result, "Api.Version", async () => result.ApiVersion = (await h.ApiVersionAsync(ct)).Result);
                await Try(result, "Plc.ReadModeSelectorState", async () =>
                    result.ModeSelector = (await h.PlcReadModeSelectorStateAsync(ApiPlcRedundancyId.StandardPLC, ct)).Result?.Mode_Selector.ToString());
                await Try(result, "Api.GetPlcCpuType", async () =>
                    result.CpuType = RuntimeChannelsLogic.ToJsonNode((await h.ApiGetPlcCpuTypeAsync(ct)).Result, Serialize));
                await Try(result, "Plc.ReadSystemTime", async () =>
                    result.SystemTimeUtc = (await h.PlcReadSystemTimeAsync(ct)).Result?.Timestamp.ToString("o"));
                await Try(result, "Plc.ReadRuntimeInformation", async () =>
                    result.RuntimeInformation = RuntimeChannelsLogic.ToJsonNode((await h.PlcReadRuntimeInformationAsync(ct)).Result, Serialize));
                await Try(result, "Plc.ReadMemoryInformation", async () =>
                    result.MemoryInformation = RuntimeChannelsLogic.ToJsonNode((await h.PlcReadMemoryInformationAsync(ct)).Result, Serialize));
            }, (r, reused) => r.ReusedSession = reused, (r, err) => r.Error = err, (r, ms) => r.ElapsedMs = ms);
            return result;
        }

        // preview=true: reads the current mode only. Otherwise sends Plc.RequestChangeOperatingMode
        // and polls Plc.ReadOperatingMode until the requested mode is observed or the budget ends.
        public static S7WebModeChangeResult ChangeOperatingMode(string host, string username, string password, string mode, bool ignoreCertificateErrors, int timeoutMs, bool preview)
        {
            var result = new S7WebModeChangeResult();
            var target = mode == "run" ? ApiPlcOperatingMode.Run : ApiPlcOperatingMode.Stop;
            Run(host, username, password, ignoreCertificateErrors, timeoutMs, result, async (h, ct) =>
            {
                result.Before = (await h.PlcReadOperatingModeAsync(ApiPlcRedundancyId.StandardPLC, ct)).Result.ToString();
                if (preview) { result.Ok = true; return; }
                if (RuntimeChannelsLogic.OperatingModeSatisfies(mode, result.Before))
                {
                    result.After = result.Before; result.Verified = true; result.Ok = true;
                    return;
                }
                result.RequestAttempted = true;
                var resp = await h.PlcRequestChangeOperatingModeAsync(target, ApiPlcRedundancyId.StandardPLC, ct);
                result.RequestAccepted = resp?.Result == true;
                if (!result.RequestAccepted) { result.Error = "Plc.RequestChangeOperatingMode did not return true."; return; }
                // The CPU changes mode asynchronously; give it up to ~10 s (bounded by the call timeout).
                var deadline = DateTime.UtcNow.AddSeconds(10);
                do
                {
                    await Task.Delay(500, ct);
                    result.PollCount++;
                    result.After = (await h.PlcReadOperatingModeAsync(ApiPlcRedundancyId.StandardPLC, ct)).Result.ToString();
                    if (RuntimeChannelsLogic.OperatingModeSatisfies(mode, result.After)) { result.Verified = true; break; }
                } while (DateTime.UtcNow < deadline);
                if (!result.Verified)
                    result.Error = $"Mode change was accepted but readback still reports '{result.After}' after {result.PollCount} polls; check the CPU (mode selector, startup errors).";
                result.Ok = result.Verified;
            }, (r, reused) => r.ReusedSession = reused, (r, err) => r.Error = err, (r, ms) => r.ElapsedMs = ms);
            return result;
        }

        public static void CloseAll()
        {
            List<Session> all;
            lock (Gate) { all = Sessions.Values.ToList(); Sessions.Clear(); }
            foreach (var s in all)
            {
                try { s.Handler.ApiLogoutAsync().Wait(2000); } catch { }
                try { s.Http.Dispose(); } catch { }
            }
        }

        // --- internals -----------------------------------------------------------

        private static string Serialize(object o) => JsonConvert.SerializeObject(o);

        private static async Task Try(S7WebDiagnosticsResult r, string call, Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r.Unavailable.Add(call + ": " + Describe(ex)); }
        }

        private static async Task<S7WebVarItem> ReadOneAsync(ApiHttpClientRequestHandler h, string name, CancellationToken ct)
        {
            var item = new S7WebVarItem { Name = name };
            try
            {
                var resp = await h.PlcProgramReadAsync<object>(name, null, ct);
                item.Value = RuntimeChannelsLogic.ToJsonNode(resp?.Result, Serialize);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Error = Describe(ex);
            }
            return item;
        }

        private static string Describe(Exception ex)
        {
            while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
            // HttpClient reports its own timeout as a cancelled task; say what it means.
            if (ex is OperationCanceledException)
                return "Timeout: no answer from the CPU web server within the request timeout (web server disabled, wrong host/port, https blocked, or CPU unreachable).";
            string text = ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                text += " <- " + inner.Message;
            // The client echoes the request into some messages; never let a password through.
            int p = text.IndexOf("\"password\"", StringComparison.OrdinalIgnoreCase);
            if (p >= 0) text = text.Substring(0, p) + "[request redacted]";
            return ex.GetType().Name + ": " + RuntimeChannelsLogic.Truncate(text, 400);
        }

        private static bool IsStaleToken(Exception ex)
        {
            if (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
            return ex is InvalidHttpRequestException && ex.Message.Contains("401");
        }

        private static void Run<T>(string host, string username, string password, bool ignoreCert, int timeoutMs, T result,
            Func<ApiHttpClientRequestHandler, CancellationToken, Task> body,
            Action<T, bool> setReused, Action<T, string> setError, Action<T, long> setElapsed)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int budget = RuntimeChannelsLogic.ClampTimeout(timeoutMs);
            try
            {
                var task = RunAsync(host, username, password, ignoreCert, budget, result, body, setReused);
                // Overall wall-clock guard: mode-change polling can legitimately take longer
                // than one request budget, so allow the poll window on top.
                if (!task.Wait(budget * 2 + 12000))
                    setError(result, $"S7 Web API call timed out ({budget} ms per request) against '{host}'.");
            }
            catch (AggregateException ae) { setError(result, "S7 Web API error: " + Describe(ae)); }
            catch (Exception ex) { setError(result, "S7 Web API error: " + Describe(ex)); }
            finally { sw.Stop(); setElapsed(result, sw.ElapsedMilliseconds); }
        }

        private static async Task RunAsync<T>(string host, string username, string password, bool ignoreCert, int budget, T result,
            Func<ApiHttpClientRequestHandler, CancellationToken, Task> body, Action<T, bool> setReused)
        {
            string key = RuntimeChannelsLogic.S7WebSessionKey(host, username, ignoreCert);
            var gate = LockFor(key);
            if (!await gate.WaitAsync(budget))
                throw new TimeoutException($"S7 Web API session for '{host}' (user '{username}') is busy with a previous call.");
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var (session, reused) = await AcquireAsync(key, host, username, password, ignoreCert, budget, forceNew: attempt > 0);
                    setReused(result, reused);
                    using var cts = new CancellationTokenSource(budget);
                    try
                    {
                        await body(session.Handler, cts.Token);
                        return;
                    }
                    catch (Exception ex) when (attempt == 0 && reused && IsStaleToken(ex))
                    {
                        Forget(key, session);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"S7 Web API request to '{host}' exceeded {budget} ms.");
                    }
                }
            }
            finally { gate.Release(); }
        }

        private static async Task<(Session session, bool reused)> AcquireAsync(string key, string host, string username, string password, bool ignoreCert, int budget, bool forceNew)
        {
            Session? existing;
            lock (Gate) { Sessions.TryGetValue(key, out existing); }
            if (!forceNew && existing != null) return (existing, true);
            if (existing != null) Forget(key, existing);

            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (ignoreCert)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://" + host + "/"),
                Timeout = TimeSpan.FromMilliseconds(budget)
            };
            var requestHandler = new ApiHttpClientRequestHandler(
                http,
                new ApiRequestFactory(new GUIDGenerator(), new ApiRequestParameterChecker(), null),
                new ApiResponseChecker(),
                new ApiRequestSplitterByBytes(null),
                null);
            try
            {
                using var cts = new CancellationTokenSource(budget);
                var login = await requestHandler.ApiLoginAsync(username, password, null, cts.Token);
                if (string.IsNullOrEmpty(login?.Result?.Token))
                    throw new InvalidOperationException("Api.Login returned no token.");
            }
            catch
            {
                http.Dispose();
                throw;
            }
            var session = new Session { Http = http, Handler = requestHandler };
            lock (Gate) { Sessions[key] = session; }
            return (session, false);
        }

        private static void Forget(string key, Session s)
        {
            lock (Gate)
            {
                if (Sessions.TryGetValue(key, out var cur) && ReferenceEquals(cur, s)) Sessions.Remove(key);
            }
            try { s.Http.Dispose(); } catch { }
        }
    }
}
