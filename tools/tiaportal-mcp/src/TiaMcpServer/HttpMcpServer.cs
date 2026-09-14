using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer
{
    /// <summary>
    /// HTTP host for the MCP server. Implements a pragmatic subset of the
    /// MCP Streamable HTTP transport spec aimed at local / internal use:
    /// <list type="bullet">
    ///   <item><description>POST /mcp — JSON-RPC, returns JSON or SSE based on Accept header.</description></item>
    ///   <item><description>GET /mcp/health — liveness/info probe.</description></item>
    ///   <item><description>DELETE /mcp — terminate session (best-effort).</description></item>
    ///   <item><description>GET / — server identity (kept for backward compat).</description></item>
    /// </list>
    /// Auth: a single shared secret can be supplied via either
    /// <c>Authorization: Bearer &lt;secret&gt;</c> or <c>X-API-Key: &lt;secret&gt;</c>.
    /// Mcp-Session-Id is generated on first request and correlated on subsequent requests;
    /// state isolation between sessions is intentionally not implemented because the
    /// underlying TIA Portal handle is process-wide.
    /// </summary>
    internal static class HttpMcpServer
    {
        private const string ServerName = "TIA Portal MCP";
        private const string SessionHeader = "Mcp-Session-Id";
        private const string ProtocolHeader = "MCP-Protocol-Version";

        // Upper bound on how long a POST waits for the MCP host to produce a matching
        // response before returning 504, so a stalled pipe can't hang the request forever.
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(240);

        private sealed class Session
        {
            public string Id = "";
            public DateTime LastSeenUtc;
            public string? ProtocolVersion;
        }

        private static readonly ConcurrentDictionary<string, Session> _sessions
            = new ConcurrentDictionary<string, Session>(StringComparer.OrdinalIgnoreCase);

        public static async Task Run(
            CliOptions? options,
            McpBlockingStream httpToMcp,
            McpBlockingStream mcpToHttp,
            Action<string> log,
            CancellationToken cancellationToken = default)
        {
            string prefix = options?.HttpPrefix ?? "http://127.0.0.1:8765/";
            if (!prefix.EndsWith("/")) prefix += "/";
            string? secret = options?.HttpApiKey;

            using var listener = new HttpListener();
            using var router = new McpHttpResponseRouter(httpToMcp, mcpToHttp);
            var handlers = new HashSet<Task>();
            Action stop = () =>
            {
                try { listener.Abort(); } catch (ObjectDisposedException) { }
                router.Dispose();
            };
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                using var stopping = cancellationToken.Register(stop);
                _ = router.Completion.ContinueWith(_ => stop(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                Console.Error.WriteLine($"TIA Portal MCP Server (HTTP) listening at {prefix}");
                log($"HTTP transport started at {prefix}");
                if (secret == null)
                    Console.Error.WriteLine("WARNING: --http-api-key not set; endpoint is unauthenticated.");

                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }

                    var handler = Task.Run(async () =>
                    {
                        try
                        {
                            await Dispatch(ctx, secret, router, log).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                                log("HTTP handler error: " + ex);
                            try { ctx.Response.Abort(); } catch { }
                        }
                    });
                    lock (handlers) handlers.Add(handler);
                    _ = handler.ContinueWith(done => { lock (handlers) handlers.Remove(done); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            finally
            {
                stop();
                await router.Completion.ConfigureAwait(false);
                Task[] outstanding;
                lock (handlers) { outstanding = new Task[handlers.Count]; handlers.CopyTo(outstanding); }
                await Task.WhenAll(outstanding).ConfigureAwait(false);
            }
        }

        private static async Task Dispatch(
            HttpListenerContext ctx,
            string? secret,
            McpHttpResponseRouter router,
            Action<string> log)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var method = req.HttpMethod ?? "";
            var rawUrl = req.RawUrl ?? "";

            // Health probe — unauthenticated by design so monitoring can hit it cheaply.
            if (HttpMethod("GET", method) && rawUrl.StartsWith("/mcp/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(res, 200, BuildHealthJson()).ConfigureAwait(false);
                return;
            }

            // Server identity (kept for backward compatibility with existing tooling).
            if (HttpMethod("GET", method) && rawUrl == "/")
            {
                await WriteJson(res, 200, $"{{\"server\":\"{ServerName}\",\"transport\":\"http\"}}").ConfigureAwait(false);
                return;
            }

            // All /mcp paths require auth (when a secret is configured).
            if (!rawUrl.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            if (secret != null && !AuthOk(req, secret))
            {
                res.StatusCode = 401;
                res.AddHeader("WWW-Authenticate", "Bearer");
                res.Close();
                return;
            }

            if (HttpMethod("GET", method) && req.Url?.AbsolutePath == "/mcp/ready")
            {
                var readiness = McpHostReadiness.Snapshot();
                await WriteJson(res, readiness["mcpHostReady"]!.GetValue<bool>() ? 200 : 503, readiness.ToJsonString()).ConfigureAwait(false);
                return;
            }

            // DELETE /mcp — session termination (best-effort; idempotent).
            if (HttpMethod("DELETE", method))
            {
                var sid = req.Headers[SessionHeader];
                if (!string.IsNullOrEmpty(sid)) _sessions.TryRemove(sid!, out _);
                res.StatusCode = 204;
                res.Close();
                return;
            }

            if (!HttpMethod("POST", method))
            {
                res.StatusCode = 405;
                res.Close();
                return;
            }

            // Body length guard: real MCP requests are tens of KB; cap defensively.
            const long MaxBodyBytes = 10L * 1024 * 1024;
            if (req.ContentLength64 > MaxBodyBytes)
            {
                res.StatusCode = 413;
                res.Close();
                return;
            }

            // Read the raw body, bounded by Content-Length. Async reads against the
            // HttpListener input stream can hang, so read synchronously (this handler
            // already runs on a dedicated task thread) and stop at the declared length.
            string body;
            var requestTrace = Guid.NewGuid().ToString("N");
            res.AddHeader("X-Mcp-Request-Id", requestTrace);
            var requestWatch = System.Diagnostics.Stopwatch.StartNew();
            byte[] bodyBytes;
            {
                long declared = req.ContentLength64;
                using var ms = new MemoryStream();
                var buf = new byte[8192];
                var input = req.InputStream;
                int read;
                while ((read = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    if (ms.Length > MaxBodyBytes) { res.StatusCode = 413; res.Close(); return; }
                    if (declared >= 0 && ms.Length >= declared) break;
                }
                bodyBytes = ms.ToArray();
            }

            string bodyHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                bodyHash = BitConverter.ToString(sha.ComputeHash(bodyBytes)).Replace("-", "").ToLowerInvariant();
            // JSON over HTTP uses UTF-8. HttpListener's default ContentEncoding can be
            // the system codepage when charset is omitted; never use that default for JSON.
            try { body = new UTF8Encoding(false, true).GetString(bodyBytes); }
            catch (DecoderFallbackException)
            {
                log($"HTTP invalid UTF-8 requestId={requestTrace} bytes={bodyBytes.Length} sha256={bodyHash}");
                await WriteJson(res, 400, new JsonObject { ["error"]="InvalidUtf8", ["requestId"]=requestTrace }.ToJsonString()).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(body)) { res.StatusCode = 400; res.Close(); return; }

            bool hasId;
            string? rpcMethod;
            try
            {
                var parsed = JsonNode.Parse(body) as JsonObject
                    ?? throw new ArgumentException("Expected a JSON-RPC object.");
                hasId = parsed.ContainsKey("id");
                rpcMethod = parsed["method"]?.GetValue<string>();
            }
            catch (Exception ex)
            {
                var error = ex as System.Text.Json.JsonException;
                log($"HTTP invalid JSON requestId={requestTrace} bytes={bodyBytes.Length} sha256={bodyHash} charset={req.ContentEncoding?.WebName} line={error?.LineNumber} bytePosition={error?.BytePositionInLine}");
                await WriteJson(res,400,new JsonObject { ["error"]="InvalidJsonRpc", ["requestId"]=requestTrace,
                    ["line"]=error?.LineNumber, ["bytePosition"]=error?.BytePositionInLine }.ToJsonString()).ConfigureAwait(false);
                return;
            }

            // Session bookkeeping. We assign on initialize and accept any subsequent header.
            var session = TouchSession(req, rpcMethod);
            res.Headers[SessionHeader] = session.Id;
            var protoVersion = req.Headers[ProtocolHeader];
            if (!string.IsNullOrEmpty(protoVersion)) session.ProtocolVersion = protoVersion;

            bool wantsSse = WantsEventStream(req);

            // Allow TIA startup/compilation longer than the former 30-second limit.
            string? responseLine = null;
            try
            {
                responseLine = await router.SendAsync(body, ResponseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log($"HTTP MCP timeout requestId={requestTrace} elapsedMs={requestWatch.ElapsedMilliseconds} bytes={bodyBytes.Length} sha256={bodyHash}; reader remains available.");
                res.StatusCode = 504;
                res.Close();
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                res.StatusCode = 503;
                res.Close();
                return;
            }
            if (!hasId) { res.StatusCode = 202; res.Close(); return; }
            if (responseLine == null) { res.StatusCode = 500; res.Close(); return; }

            if (wantsSse)
            {
                await WriteSse(res, responseLine).ConfigureAwait(false);
            }
            else
            {
                await WriteJson(res, 200, responseLine).ConfigureAwait(false);
            }
        }

        // ---------- helpers ----------

        private static bool HttpMethod(string expected, string actual)
            => string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        private static bool AuthOk(HttpListenerRequest req, string secret)
        {
            var apiKey = req.Headers["X-API-Key"];
            if (apiKey == secret) return true;

            var authz = req.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authz)
                && authz!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && authz.Substring(7).Trim() == secret)
            {
                return true;
            }
            return false;
        }

        private static bool WantsEventStream(HttpListenerRequest req)
        {
            var accept = req.Headers["Accept"];
            return !string.IsNullOrEmpty(accept)
                && accept!.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Session TouchSession(HttpListenerRequest req, string? rpcMethod)
        {
            var sid = req.Headers[SessionHeader];
            if (!string.IsNullOrEmpty(sid) && _sessions.TryGetValue(sid!, out var existing))
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                return existing;
            }
            // Allocate a new session on initialize, or whenever a client omits the header.
            var s = new Session
            {
                Id = Guid.NewGuid().ToString("N"),
                LastSeenUtc = DateTime.UtcNow,
            };
            _sessions[s.Id] = s;
            return s;
        }

        private static string BuildHealthJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"server\":\"").Append(ServerName).Append("\"");
            sb.Append(",\"transport\":\"http\"");
            sb.Append(",\"sessions\":").Append(_sessions.Count);
            sb.Append(",\"build\":\"").Append(typeof(HttpMcpServer).Assembly.GetName().Version).Append("\"");
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(HttpMcpServer).Assembly.Location);
            sb.Append(",\"fileVersion\":\"").Append(version.FileVersion).Append("\"");
            sb.Append(",\"responseTimeoutSeconds\":").Append((int)ResponseTimeout.TotalSeconds);
            sb.Append("}");
            return sb.ToString();
        }

        private static async Task WriteJson(HttpListenerResponse res, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            res.Close();
        }

        private static async Task WriteSse(HttpListenerResponse res, string jsonRpcLine)
        {
            // Single-shot SSE: one "message" event carrying the JSON-RPC response body,
            // then end of stream. This keeps spec-compliant clients happy without
            // adding a real long-lived stream that local use does not need.
            var sb = new StringBuilder();
            sb.Append("event: message\n");
            // Split payload on newlines per SSE framing rules.
            foreach (var line in jsonRpcLine.Split('\n'))
            {
                sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
            }
            sb.Append('\n');

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache";
            res.SendChunked = true;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            res.OutputStream.Close();
            res.Close();
        }
    }
}
