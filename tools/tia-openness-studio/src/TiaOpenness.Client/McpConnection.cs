using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaOpenness.Client
{
    /// <summary>Existing local MCP service only. No process launch, Siemens load, reconnect or replay.</summary>
    public sealed class McpConnection : IDisposable
    {
        private readonly HttpClient http;
        private readonly Uri endpoint;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private string sessionId;
        private int nextId;
        private bool initialized;
        private bool poisoned;
        public McpConnection(Uri endpoint, string token = null, HttpMessageHandler handler = null)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || !endpoint.IsLoopback ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                endpoint.AbsolutePath != "/mcp")
                throw new ArgumentException("Use an explicit loopback http(s)://127.0.0.1:PORT/mcp endpoint.");
            this.endpoint = endpoint;
            http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        public async Task<JObject> ToolAsync(string name, JObject arguments, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (poisoned) throw new InvalidOperationException("The previous MCP outcome is unknown. Restart Studio and inspect the engine state; never replay automatically.");
                if (!initialized)
                {
                    var hello = await RpcAsync("initialize", new JObject {
                        ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JObject(),
                        ["clientInfo"] = new JObject { ["name"] = "tia-openness-studio", ["version"] = "2.4.0-integrated" }
                    }, ct).ConfigureAwait(false);
                    var version = hello.Value<string>("protocolVersion");
                    if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("MCP initialization omitted protocolVersion.");
                    http.DefaultRequestHeaders.Add("MCP-Protocol-Version", version);
                    await RpcAsync("notifications/initialized", new JObject(), ct, notification: true).ConfigureAwait(false);
                    initialized = true;
                }
                // CallTool is in the repository's lite profile and retains version and policy checks.
                var result = await RpcAsync("tools/call", new JObject {
                    ["name"] = "CallTool", ["arguments"] = new JObject { ["name"] = name, ["argumentsJson"] = arguments ?? new JObject() }
                }, ct).ConfigureAwait(false);
                return DecodeToolResult(result);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException)
            {
                poisoned = true;
                throw new InvalidOperationException("MCP response is unavailable or malformed; outcome unknown. Restart Studio and inspect the engine. No retry was sent.", ex);
            }
            finally { gate.Release(); }
        }
        private async Task<JObject> RpcAsync(string method, JObject parameters, CancellationToken ct, bool notification = false)
        {
            var id = Interlocked.Increment(ref nextId);
            var frame = new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
            if (!notification) frame["id"] = id;
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                if (sessionId != null) request.Headers.Add("Mcp-Session-Id", sessionId);
                request.Content = new StringContent(frame.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                    {
                        var received = ids.Single();
                        if (sessionId != null && sessionId != received) throw new JsonException("MCP session identity changed.");
                        sessionId = received;
                    }
                    if (notification) return new JObject();
                    var payload = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (payload.Value<string>("jsonrpc") != "2.0" || payload["id"]?.ToString() != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        throw new JsonException("MCP response ID or protocol mismatch.");
                    if (payload["error"] is JObject error) throw new InvalidOperationException(error.Value<string>("message") ?? "MCP error.");
                    return payload["result"] as JObject ?? throw new JsonException("MCP result is missing.");
                }
            }
        }
        public static JObject DecodeToolResult(JObject result)
        {
            if (result.Value<bool?>("isError") == true) throw new InvalidOperationException(result["content"]?.ToString() ?? "MCP tool error.");
            var body = result["structuredContent"] as JObject;
            if (body == null)
            {
                var texts = (result["content"] as JArray)?.OfType<JObject>().Where(x => x.Value<string>("type") == "text").ToList();
                if (texts == null || texts.Count != 1) throw new JsonException("Expected one complete MCP JSON text result.");
                body = JObject.Parse(texts[0].Value<string>("text") ?? "");
            }
            RequireSuccess(body);
            var meta = Get(body, "meta");
            if (Get(meta, "bridgeSuccess") != null)
            {
                if (Get(meta, "bridgeSuccess")?.Value<bool>() != true) throw new InvalidOperationException(Get(body, "message")?.ToString());
                body = JObject.Parse(Get(body, "message")?.Value<string>() ?? "");
                RequireSuccess(body);
            }
            if (Get(Get(body, "meta"), "truncated")?.Value<bool>() == true || Get(Get(body, "meta"), "exportId") != null)
                throw new InvalidOperationException("Response was paginated. Use the MCP export retrieval tools; Studio will not display an incomplete result as complete.");
            return body;
        }
        public static JToken Get(JToken token, string name) => (token as JObject)?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        public static void RequireSuccess(JObject body)
        {
            var meta = Get(body, "meta");
            if (Get(meta, "success")?.Value<bool?>() == false || Get(meta, "operationSuccess")?.Value<bool?>() == false ||
                Get(meta, "bridgeSuccess")?.Value<bool?>() == false)
                throw new InvalidOperationException(Get(body, "message")?.ToString() ?? "The engine rejected this operation.");
        }
        public void Dispose() { http.Dispose(); }
    }
}
