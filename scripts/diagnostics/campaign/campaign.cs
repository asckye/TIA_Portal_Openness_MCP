#!/usr/bin/env dotnet
// Real-machine campaigns and historical ledger rendering; connection is lazy.
// Usage: dotnet run scripts/diagnostics/campaign/campaign.cs -- call|run|raw|export|ledger [arguments] [--config config.json]
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../TiaMcp.DiagnosticClients/TiaMcp.DiagnosticClients.csproj

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.DiagnosticClients;

Console.OutputEncoding = new UTF8Encoding(false);
Connection? connection = null; HttpProbe? probe = null;
try
{
    var options = new Arguments(args); var root = Repository.FindRoot(Environment.CurrentDirectory);
    var configPath = options.Take("--config"); var config = configPath is null ? new JsonObject() : JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
    var url = options.Take("--url"); var token = options.Take("--token"); var output = options.Take("--output"); var run = options.Take("--run"); var check = options.Flag("--check");
    if (options.Rest.Count == 0 || options.Flag("--help")) { Console.WriteLine("call <Tool> [json|@file] [keys] | run <plan.json> [start] | raw <Tool> [json|@file] | export <id> <file> | ledger --output <file> [--check]; --config contains bundleRoot and run"); return 0; }
    var mode = options.Rest[0]; options.Rest.RemoveAt(0);
    if (mode == "ledger")
    {
        output ??= Path.Combine(root, "docs/reference/real-machine-ledger.md"); Ledger.Write(root, output, check); Console.WriteLine(output); return 0;
    }
    if (mode is not ("call" or "run" or "raw" or "export")) throw new ArgumentException("Use call, run, raw, export or ledger");
    if (options.Rest.Count == 0) throw new ArgumentException("Missing command argument");
    var campaign = new Campaign(() => { connection = Connection.Load(url, token); return probe = new HttpProbe(connection, 900); });
    string Redact(string text) => connection?.Redact(text) ?? text;
    if (mode == "run")
    {
        Console.Error.WriteLine(McpSession.ApprovalNotice);
        var path = options.Rest[0]; var plan = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var start = int.Parse(options.Rest.ElementAtOrDefault(1) ?? "0", CultureInfo.InvariantCulture);
        if (start < 0 || start > plan.Count) throw new ArgumentException("Start index outside plan");
        run ??= (string?)config["run"] ?? Path.GetFileNameWithoutExtension(path);
        var exportRoot = Campaign.ExportRoot(config, run);
        // Validate the entire plan before any connection or native call.
        Campaign.Expand(plan, exportRoot, "");
        var directory = Path.Combine(root, "scripts/diagnostics/campaign/ledger"); Directory.CreateDirectory(directory);
        using var ledger = new StreamWriter(Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".jsonl"), append: true, new UTF8Encoding(false)) { NewLine = "\n" };
        campaign.Run(plan, start, exportRoot, ledger, Console.Out, Redact); return 0;
    }
    if (mode == "export")
    {
        if (options.Rest.Count != 2) throw new ArgumentException("export requires id and output file");
        var text = Campaign.Assemble(options.Rest[0], campaign.CallRaw);
        File.WriteAllText(options.Rest[1], Redact(text), new UTF8Encoding(false)); Console.WriteLine(Redact(options.Rest[1]) + " " + text.EnumerateRunes().Count()); return 0;
    }
    Console.Error.WriteLine(McpSession.ApprovalNotice);
    var tool = options.Rest[0]; var arguments = Arguments.Load(options.Rest.ElementAtOrDefault(1));
    if (PythonJson.Dumps(arguments).Contains("${EXPORT_ROOT}", StringComparison.Ordinal)) arguments = Campaign.Expand(arguments, Campaign.ExportRoot(config, run ?? (string?)config["run"] ?? "manual"), "")!.AsObject();
    var value = campaign.CallRaw(tool, arguments);
    if (mode == "raw")
    {
        Console.WriteLine(Redact("MESSAGE: " + Campaign.Trim(PythonJson.Dumps(value["error"] ?? value["data"], ensureAscii: false), 3000)));
        Console.WriteLine(Redact("META: " + Campaign.Trim(PythonJson.Dumps(value["meta"], ensureAscii: false), 3000))); return 0;
    }
    var (ok, summary) = Campaign.Classify(value); Console.WriteLine(Redact("== " + tool + " " + (ok ? "ok" : "FAIL") + " :: " + Campaign.Trim(summary, 400)));
    var fields = Campaign.ResultFields(value);
    foreach (var key in (options.Rest.ElementAtOrDefault(2) ?? "").Split(','))
    {
        if (key == "*") Console.WriteLine(Redact("    meta = " + Campaign.Trim(PythonJson.Dumps(fields, ensureAscii: false), 4000)));
        else if (fields.ContainsKey(key)) Console.WriteLine(Redact("    " + key + " = " + Campaign.Trim(PythonJson.Dumps(fields[key], ensureAscii: false), 1500)));
    }
    Console.WriteLine(Redact("    " + PythonJson.Dumps(campaign.Alive(), ensureAscii: false))); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(connection?.Redact(ex.Message) ?? ex.Message); return 1; }
finally { probe?.Dispose(); }
