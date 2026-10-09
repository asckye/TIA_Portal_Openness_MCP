#!/usr/bin/env dotnet
// Transitional Python/C# byte and fingerprint parity; remove with batch 8.
// Usage: dotnet run scripts/checks/Test-StaticCheckParity.cs -- -Python python -Root .
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../build-tools/common/TiaMcp.BuildCommon/TiaMcp.BuildCommon.csproj

using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

var arguments = args.ToList();
string? Take(string name)
{
    var index = arguments.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= arguments.Count) throw new ArgumentException("Missing value for " + name);
    var value = arguments[index + 1]; arguments.RemoveRange(index, 2); return value;
}
var toolRoot = Repository.FindRoot(Environment.CurrentDirectory);
var root = Path.GetFullPath(Take("-Root") ?? toolRoot);
var python = Take("-Python") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
var output = Path.GetFullPath(Take("-Output") ?? Path.Combine(toolRoot, "bin-build", "static-parity-" + Guid.NewGuid().ToString("N")));
if (arguments.Count != 0) throw new ArgumentException("Unexpected argument: " + arguments[0]);
if (!output.StartsWith(toolRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Parity output must stay within the worktree");
Directory.CreateDirectory(output);
void Run(string executable, params string[] command)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = toolRoot, RedirectStandardOutput = true, RedirectStandardError = true };
    start.Environment["PYTHONIOENCODING"] = "utf-8"; start.Environment["UseSharedCompilation"] = "false";
    foreach (var argument in command) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start " + executable);
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); Console.Write(stdout.Result); Console.Error.Write(stderr.Result);
    if (process.ExitCode != 0) throw new InvalidOperationException(executable + " failed: " + process.ExitCode);
}
var oracle = """"
import importlib.util, json, sys, shutil
from pathlib import Path
root, tool, output = map(Path, sys.argv[1:])
def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), tool / 'scripts/checks' / (name + '.py'))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module
modules = dict(swallowed=load('Check-SwallowedExceptions'), comments=load('Check-CommentHygiene'),
               **{'mcp-text': load('Check-McpText'), 'envelopes': load('Inventory-ResponseEnvelopes')})
baselines = dict(swallowed='swallowed-exceptions-baseline.json', comments='comment-hygiene-baseline.json',
                 **{'mcp-text':'mcp-text-baseline.json','envelopes':'response-envelope-baseline.json'})
for kind, module in modules.items():
    result = module.scan(root); rows, errors = result[:2]
    assert not errors, errors
    source = root / 'scripts/checks' / baselines[kind]
    path = output / ('py-' + kind + '-baseline.json'); shutil.copyfile(source, path)
    shutil.copyfile(source, output / ('cs-' + kind + '-baseline.json'))
    if kind == 'swallowed':
        module.write_baseline(path, module.baseline_rows(rows), module.read_baseline(path))
        inventory = dict(rows=rows, errors=errors)
    elif kind == 'comments':
        module.write_baseline(path, module.baseline_rows(rows), module.read_baseline(path, module.KINDS))
        inventory = dict(rows=rows, errors=errors)
    elif kind == 'mcp-text':
        previous, allowed = module.read_baseline(path, updating=True)
        module.write_baseline(path, rows, previous, allowed)
        inventory = dict(rows=rows, errors=errors)
    else:
        handwritten = module.totals(rows, 'handwritten', module.RATCHET)
        module.write_baseline(path, handwritten, module.read_baseline(path))
        inventory = dict(files=rows, totals=module.totals(rows, 'counts', module.METRICS), handwritten=handwritten, errors=errors)
    (output / ('py-' + kind + '.json')).write_text(json.dumps(inventory, ensure_ascii=False), encoding='utf-8')
dead = load('Check-DeadToolReferences')
sources = {}
for folder in ('src/Engine','src/Logic','src/Shared'):
    sources.update(dead.load(root / folder))
registered, _ = dead.scan(sources)
old, new = next((a,b) for a,b in dead.migration_names(root).items() if a != b and b in registered)
fixture = '[McpServerTool(Name="' + new + '")] void Tool() {}\n[Description("Use ' + old + '.")] void M() { var data = "' + old + '"; }\n'
expected, events = dead.rewrite_guidance(fixture, dead.migration_names(root), registered)
assert any(event[3] for event in events), events
(output / 'py-fixed.cs').write_bytes(expected.encode('utf-8'))
mutation = output / 'dead-copy'
for name in ('docs/development/phase6-review.md',):
    target = mutation / name; target.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(root/name, target)
for folder in ('manifest/history/contracts-v3/baseline','manifest/contracts/v4/baseline'):
    shutil.copytree(root/folder, mutation/folder, dirs_exist_ok=True)
target = mutation / 'src/Engine/Fixture.cs'; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(fixture.encode('utf-8'))
"""";
Run("dotnet", "build", Path.Combine(toolRoot, "build-tools/release"), "--nologo", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false");
Run(python, "-X", "utf8", "-c", oracle, root, toolRoot, output);
foreach (var kind in new[] { "swallowed", "comments", "mcp-text", "envelopes" })
{
    Run("dotnet", "run", "--project", Path.Combine(toolRoot, "build-tools/release"), "--no-restore", "--", "check-ratchet", "-Kind", kind, "-Root", root, "-Baseline", Path.Combine(output, "cs-" + kind + "-baseline.json"), "-UpdateBaseline", "-Output", Path.Combine(output, "cs-" + kind + ".json"));
    var before = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "py-" + kind + ".json")))!;
    var after = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "cs-" + kind + ".json")))!;
    if (kind != "envelopes")
    {
        string[] Rows(JsonNode data) => data["rows"]!.AsArray().Select(r => PythonJson.Dumps(r, sortKeys: true, ensureAscii: false)).Order(PythonStringComparer.Instance).ToArray();
        if (!Rows(before).SequenceEqual(Rows(after))) throw new InvalidOperationException(kind + " full inventory/fingerprint multiset differs");
    }
    else if (!JsonNode.DeepEquals(before, after)) throw new InvalidOperationException("Envelope full inventory differs");
    if (!File.ReadAllBytes(Path.Combine(output, "py-" + kind + "-baseline.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "cs-" + kind + "-baseline.json")))) throw new InvalidOperationException(kind + " regenerated baseline bytes differ");
    var committed = File.ReadAllBytes(Path.Combine(root, "scripts/checks", kind switch { "swallowed" => "swallowed-exceptions-baseline.json", "comments" => "comment-hygiene-baseline.json", "mcp-text" => "mcp-text-baseline.json", _ => "response-envelope-baseline.json" }));
    Console.WriteLine("PARITY " + kind + ": full inventory and regenerated bytes equal; input baseline drift=" + !committed.SequenceEqual(File.ReadAllBytes(Path.Combine(output, "py-" + kind + "-baseline.json"))));
}
Run("dotnet", "run", "--project", Path.Combine(toolRoot, "build-tools/release"), "--no-restore", "--", "check-dead-tool-references", "-Root", Path.Combine(output, "dead-copy"), "-Fix");
if (!File.ReadAllBytes(Path.Combine(output, "py-fixed.cs")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "dead-copy/src/Engine/Fixture.cs")))) throw new InvalidOperationException("Dead-reference fix bytes differ");
Run(python, "-X", "utf8", Path.Combine(toolRoot, "scripts/checks/Check-TiaFeatures.py"), "--root", root, "--capture", Path.Combine(output, "py-tia.json"));
Run("dotnet", "run", "--project", Path.Combine(toolRoot, "build-tools/release"), "--no-restore", "--", "check-tia-features", "-Root", root, "-Capture", Path.Combine(output, "cs-tia.json"));
if (!File.ReadAllBytes(Path.Combine(output, "py-tia.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "cs-tia.json")))) throw new InvalidOperationException("TIA capture bytes differ");
Console.WriteLine("PARITY dead-reference fix and TIA capture: identical bytes. Evidence: " + output);
