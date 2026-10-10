using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.DiagnosticClients;

public interface IMcpClient
{
    JsonNode? Rpc(string method, JsonNode? parameters = null, bool notify = false, int timeoutSeconds = 0);
}

public sealed class Connection
{
    public string Url { get; }
    private readonly string authorization;
    public Connection(string url, string authorization) { Url = url; this.authorization = authorization; }
    public void Authorize(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("Authorization", authorization);
    public string Redact(string text)
    {
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
        foreach (var secret in new[] { authorization, token, Uri.EscapeDataString(token), PythonJson.Dumps(JsonValue.Create(token))[1..^1] }.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }
    public override string ToString() => "MCP connection (credentials redacted)";
    public static Connection Load(string? url = null, string? token = null, string? claudeFile = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        url ??= environment("TIA_MCP_URL"); token ??= environment("TIA_MCP_TOKEN");
        var auth = string.IsNullOrEmpty(token) ? null : "Bearer " + token;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(auth))
        {
            claudeFile ??= Path.Combine(environment("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            JsonObject? Walk(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    if (obj["tia-portal-vm"] is JsonObject entry && entry["url"] is not null) return entry;
                    foreach (var field in obj) if (Walk(field.Value) is { } found) return found;
                }
                else if (node is JsonArray array) foreach (var item in array) if (Walk(item) is { } found) return found;
                return null;
            }
            var entry = File.Exists(claudeFile) ? Walk(JsonNode.Parse(File.ReadAllText(claudeFile))) : null;
            url = string.IsNullOrEmpty(url) ? (string?)entry?["url"] : url;
            auth ??= (string?)entry?["headers"]?["Authorization"];
        }
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(auth)) throw new ArgumentException("No TIA_MCP_URL/TIA_MCP_TOKEN and no tia-portal-vm connection in ~/.claude.json");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0)
            throw new ArgumentException("MCP URL must be HTTP(S), without credentials or a query");
        return new Connection(url, auth);
    }
}

public sealed class HttpProbe : IMcpClient, IDisposable
{
    private readonly HttpClient http;
    private readonly Connection connection;
    private readonly int defaultTimeout;
    private int sequence;
    public string? Session { get; private set; }
    public HttpProbe(Connection connection, int timeoutSeconds = 600, HttpMessageHandler? handler = null)
    {
        this.connection = connection; defaultTimeout = timeoutSeconds;
        http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public JsonNode? Rpc(string method, JsonNode? parameters = null, bool notify = false, int timeoutSeconds = 0)
    {
        try
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters is not null) body["params"] = parameters.DeepClone();
            if (!notify) body["id"] = ++sequence;
            using var request = new HttpRequestMessage(HttpMethod.Post, connection.Url);
            request.Content = new StringContent(PythonJson.Dumps(body), Encoding.UTF8, "application/json");
            connection.Authorize(request); request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (Session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", Session);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : defaultTimeout));
            using var response = http.SendAsync(request, cancellation.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) Session = sessions.First();
            var raw = connection.Redact(response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult());
            return notify ? null : Decode(raw, response.Content.Headers.ContentType?.MediaType ?? "");
        }
        catch (Exception ex) { throw new InvalidOperationException(connection.Redact(ex.Message)); }
    }
    public static JsonNode? Decode(string raw, string contentType)
    {
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var events = new List<string>(); var current = new List<string>();
            foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) current.Add(line[5..].TrimStart());
                else if (string.IsNullOrWhiteSpace(line) && current.Count > 0) { events.Add(string.Join('\n', current)); current.Clear(); }
            }
            if (current.Count > 0) events.Add(string.Join('\n', current));
            foreach (var ev in events.AsEnumerable().Reverse())
            {
                try { if (JsonNode.Parse(ev) is JsonObject obj && (obj.ContainsKey("result") || obj.ContainsKey("error"))) return obj; }
                catch (System.Text.Json.JsonException) { }
            }
            raw = events.LastOrDefault() ?? raw;
        }
        return string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
    }
    public void Dispose() => http.Dispose();
}

public static class McpSession
{
    public const string ApprovalNotice = "Before any write campaign, keep the Workbench open to approve each call, or switch approvals off in the MCP menu. This client does not bypass approval.";
    public static string Initialize(IMcpClient client, string name, string protocol = "2025-06-18")
    {
        var init = client.Rpc("initialize", new JsonObject { ["protocolVersion"] = protocol, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = name, ["version"] = "1" } })!;
        client.Rpc("notifications/initialized", notify: true);
        var info = init["result"]!["serverInfo"]!;
        return $"{info["name"]} {info["version"]}  protocol={init["result"]!["protocolVersion"]}";
    }
    public static JsonArray Tools(IMcpClient client)
    {
        var tools = new JsonArray(); string? cursor = null;
        do
        {
            var reply = client.Rpc("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor })!["result"]!;
            foreach (var tool in reply["tools"]!.AsArray()) tools.Add(tool!.DeepClone());
            cursor = (string?)reply["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }
    public static JsonObject Call(string tool, IMcpClient client, JsonObject arguments, int timeoutSeconds = 180) =>
        McpResults.Envelope(client.Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, timeoutSeconds: timeoutSeconds));
    public static bool ResetRequired(JsonObject value) => (bool?)value["meta"]?["requiresSessionReset"] == true || (string?)value["meta"]?["outcome"] == "unknown";
}

public sealed record FoundationLaunch(string Executable, string BundleRoot, string ReleaseKey)
{
    public string[] Arguments => ["--bundle-root", BundleRoot, "--release-key", ReleaseKey, "--transport", "stdio", "--profile", "full", "--logging", "0"];
    public static FoundationLaunch Resolve(string bundleRoot, string releaseKey)
    {
        if (!new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Contains(releaseKey)) throw new ArgumentException("Unsupported release key");
        var root = Path.GetFullPath(bundleRoot);
        var exe = Path.Combine(root, "runtime", "v" + releaseKey, "TiaMcp.FoundationHost.exe");
        if (!File.Exists(Path.Combine(root, "manifest/package-manifest.json")) || !File.Exists(exe)) throw new ArgumentException("Selected bundle has no FoundationHost or package manifest");
        var marker = Path.Combine(Path.GetDirectoryName(exe)!, "release-key.txt");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() != releaseKey) throw new ArgumentException("FoundationHost release marker does not match selection");
        return new FoundationLaunch(exe, root, releaseKey);
    }
}

public sealed class StdioClient : IMcpClient, IDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly int shutdownTimeoutSeconds;
    private int sequence;
    public int Pid => process.Id;
    public Process Process => process;
    public StdioClient(FoundationLaunch launch, string clientName, int shutdownTimeoutSeconds = 60)
    {
        this.shutdownTimeoutSeconds = shutdownTimeoutSeconds;
        var start = new ProcessStartInfo(launch.Executable) { WorkingDirectory = launch.BundleRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start FoundationHost");
        process.StandardInput.NewLine = "\n"; stderr = process.StandardError.ReadToEndAsync();
        try { McpSession.Initialize(this, clientName, "2024-11-05"); }
        catch { Dispose(); throw; }
    }
    public JsonNode? Rpc(string method, JsonNode? parameters = null, bool notify = false, int timeoutSeconds = 0)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) body["params"] = parameters.DeepClone();
        if (!notify) body["id"] = ++sequence;
        process.StandardInput.WriteLine(PythonJson.Dumps(body, ensureAscii: false)); process.StandardInput.Flush();
        if (notify) return null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 600));
        while (true)
        {
            string? line;
            try { line = process.StandardOutput.ReadLineAsync(cancellation.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new InvalidOperationException("timeout on " + method); }
            if (line is null) throw new InvalidOperationException("FoundationHost exited: " + (stderr.IsCompleted ? stderr.Result[..Math.Min(stderr.Result.Length, 600)] : ""));
            JsonNode? value;
            try { value = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
            if (value is JsonObject obj && obj["id"] is JsonValue id && id.TryGetValue<int>(out var number) && number == sequence) return obj;
        }
    }
    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(shutdownTimeoutSeconds * 1000)) { process.Kill(entireProcessTree: true); process.WaitForExit(10000); }
        }
        process.Dispose();
    }
}

public sealed class Arguments
{
    public List<string> Rest { get; }
    public Arguments(string[] args) => Rest = args.ToList();
    public string? Take(string name)
    {
        var index = Rest.IndexOf(name); if (index < 0) return null;
        if (index + 1 == Rest.Count) throw new ArgumentException("Missing value for " + name);
        var value = Rest[index + 1]; Rest.RemoveRange(index, 2); return value;
    }
    public bool Flag(string name) => Rest.Remove(name);
    public static JsonObject Load(string? text) => string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text.StartsWith('@') ? File.ReadAllText(text[1..]) : text)!.AsObject();
}
