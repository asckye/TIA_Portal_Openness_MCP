#!/usr/bin/env dotnet
// Verify Studio adapter hello and unbound read-only RPCs with local SDKs.
// Usage: dotnet run tests/Studio/Test-BridgeSmoke.cs -- --bridge <Bridge.exe> --public-api-root <SDK-root> [--shared-adapter-paths]
#:property PublishAot=false
#:property NuGetAudit=false
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property IsTestProject=false
#:property GenerateProgramFile=false
#:project ../../scripts/diagnostics/TiaMcp.DiagnosticClients/TiaMcp.DiagnosticClients.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.DiagnosticClients;

Console.OutputEncoding = new UTF8Encoding(false);
var options = new Arguments(args);
if (options.Flag("--help")) { Console.WriteLine("--bridge <Bridge.exe> --public-api-root <SDK-root> [--shared-adapter-paths]; no native session is created"); return 0; }
var bridge = Path.GetFullPath(options.Take("--bridge") ?? throw new ArgumentException("--bridge required"));
var sdk = Path.GetFullPath(options.Take("--public-api-root") ?? throw new ArgumentException("--public-api-root required"));
var shared = options.Flag("--shared-adapter-paths");
if (options.Rest.Count != 0) throw new ArgumentException("Unexpected argument");
string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var count = 0;
foreach (var (key, relative) in new[] { ("14sp1", "TIA_V14SP1_PublicAPI/V14 SP1"), ("16", "TIA_V16_PublicAPI/V16"), ("21", "TIA_V21_PublicAPI/V21/net48") })
{
    var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    var adapter = Path.Combine(Path.GetDirectoryName(bridge)!, "adapters", "v" + key, shared ? "TiaMcp.Adapter." + key + ".dll" : "TiaOpenness.Openness.dll");
    var expected = new JsonObject { ["protocol"] = 2, ["releaseKey"] = key, ["workerSha256"] = Digest(bridge), ["adapterSha256"] = Digest(adapter), ["nonce"] = nonce, ["bindingEpoch"] = 0, ["bound"] = false };
    var start = new ProcessStartInfo(bridge) { WorkingDirectory = Path.GetDirectoryName(bridge), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
    foreach (var argument in new[] { "--openness-version", key, "--nonce", nonce, "--public-api", Path.Combine(sdk, relative) }) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start bridge");
    var diagnostics = child.StandardError.ReadToEndAsync(); expected["pid"] = child.Id; child.StandardInput.NewLine = "\n";
    async Task<JsonNode> Receive()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = new List<byte>(); var single = new byte[1];
        while (true)
        {
            var read = await child.StandardOutput.BaseStream.ReadAsync(single, cancellation.Token);
            Require(read == 1, "Bridge exited before a frame"); bytes.Add(single[0]);
            if (single[0] == 10) break;
            Require(bytes.Count < 2_000_000, "Bridge frame too large");
        }
        Require(!bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Bridge frame has BOM");
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray()))!;
    }
    try
    {
        Require(JsonNode.DeepEquals(await Receive(), new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "hello", ["params"] = expected }), "Bridge hello identity mismatch"); count++;
        var id = 0;
        foreach (var method in new[] { "session.state", "ping", "doctor.run" })
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = new JsonObject(), ["bindingEpoch"] = 0 };
            child.StandardInput.WriteLine(request.ToJsonString()); child.StandardInput.Flush(); var reply = await Receive();
            Require((string?)reply["jsonrpc"] == "2.0" && (int?)reply["id"] == id && (int?)reply["bindingEpochBefore"] == 0 && (int?)reply["bindingEpochAfter"] == 0 && !reply.AsObject().ContainsKey("error"), "Invalid unbound RPC: " + reply);
            var result = reply["result"]!;
            if (method == "session.state") Require((bool?)result["Connected"] == false && (string?)result["Mode"] == "Openness" && result["OpenProject"] is null, "Unexpected bound state");
            else if (method == "ping") Require(JsonNode.DeepEquals(result, new JsonObject { ["pong"] = true, ["mode"] = "Openness", ["decision"] = "direct Openness V" + key }), "Ping mismatch");
            else Require(!string.IsNullOrEmpty((string?)result["MachineName"]) && result["Checks"]!.AsArray().Any(c => (string?)c!["Id"] == "ENV-NETFX"), "Doctor mismatch");
            count++;
        }
        child.StandardInput.Close(); Require(child.WaitForExit(5000) && child.ExitCode == 0, "Bridge exit failure"); count++;
        Console.WriteLine($"PASS Studio {key}: 5 checks; native session never created");
    }
    finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); } }
}
Console.WriteLine($"Passed: {count}; Failed: 0"); return 0;
