#!/usr/bin/env python3
"""Extract real HMI caller/helper into self-authored fake-only checks; never load Siemens."""
from pathlib import Path
import os
import subprocess
import shutil
import uuid
from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]
sources = EngineSources()
# Service bodies reach the kernel through `_session.`; the compiled fixture supplies those members directly.
screen = sources.member('ImportHmiScreen', tool=False).replace('_session.', '')
batch = sources.member('ImportHmiScreensFromDirectory', tool=False).replace('_session.', '')
helper = sources.member('TryImportEngineeringObjectIntoCollection', signature='out string? importedName')
name_reader = sources.member('BestEffortExtractFirstName')
assert screen.count('TryImportEngineeringObjectIntoCollection(') == 1
assert 'GuardClassicScreenSize(sw, importPath);' in screen
assert 'document.Save(' not in screen and 'Regex.Match(' not in screen
assert 'break;' in batch and 'later matching files were not attempted' in batch
assert 'ImportOptions.Override' in helper  # Preserve this family's existing default.
wrapper = sources.member('ImportHmiScreensFromDirectory', tool=True)
assert 'Imported = result.Imported' in wrapper and 'Failed = result.Failed' in wrapper
assert '["success"] = (result.Failed == null || !result.Failed.Any())' in wrapper

program = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
enum ImportOptions { None, Override }
enum PortalErrorCode { InvalidState, NotFound, ImportFailed, InvalidParams }
class PortalException : Exception {
    public PortalException(PortalErrorCode code, string message, object? context = null, Exception? inner = null) : base(message, inner) { }
}
class ImportFailure { public string? Path { get; set; } public string? Error { get; set; } }
class ResponseImportBatch { public List<string>? Imported { get; set; } public List<ImportFailure>? Failed { get; set; } }
class Container { public object? Software { get; set; } }
class Software { public object? Screens { get; set; } }
class Named { public string Name { get; set; } = "NativeName"; }
class FakeCollection {
    public int Calls, FallbackCalls;
    public ImportOptions Seen;
    public Func<FileInfo, object?> Result = file => new[] { new Named() };
    public object? Import(FileInfo file, ImportOptions options) { Calls++; Seen = options; return Result(file); }
    public object Import(FileInfo file) { FallbackCalls++; throw new Exception("must not invoke fallback"); }
}
class Optionless { public int Calls; public object Import(FileInfo file) { Calls++; return new[] { new Named() }; } }
class MissingImporter { }
class Harness {
    public bool NoProject, InvalidInput;
    public Container Container = new Container();
    public string? LastImportNotes { get; private set; }
    bool IsProjectNull() => NoProject;
    Container GetSoftwareContainer(string path) => Container;
    // Dependency double: exercise caller fail-stop when deterministic preflight rejects input.
    void GuardClassicScreenSize(object software, string path) { if (InvalidInput) throw new PortalException(PortalErrorCode.InvalidParams, "invalid preflight input"); }
    static object? TryGetPropertyValue(object obj, string name) => obj.GetType().GetProperty(name)?.GetValue(obj);
    static object? TryResolveChildGroupByPath(object root, string folder) => root;
__SCREEN__
__BATCH__
__HELPER__
__NAME_READER__
}
class Program {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static string Failure(Action action) { try { action(); } catch (PortalException ex) { return ex.Message; } throw new Exception("expected failure"); }
    static Harness New(object collection) => new Harness { Container = new Container { Software = new Software { Screens = collection } } };
    static void Main() {
        var dir = Path.Combine(Path.GetTempPath(), "hmi-fake-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var file = Path.Combine(dir, "screen.xml");
            var xml = "<Document><Hmi.Screen.Button><AttributeList><Visible>true</Visible></AttributeList></Hmi.Screen.Button></Document>";
            File.WriteAllText(file, xml);
            foreach (var original in new[] { "'set_Visible' is not supported by type 'Hmi.Screen.Button'", "unknown native outcome after partial mutation" }) {
                var collection = new FakeCollection { Result = f => throw new Exception(original) };
                var harness = New(collection);
                var error = Failure(() => harness.ImportHmiScreen("hmi", "", file));
                Check(collection.Calls == 1 && collection.FallbackCalls == 0, "one invocation on native throw including old stripping trigger");
                Check(error.Contains(original) && error.Contains("project may have changed") && error.Contains("No automatic retry"), "original error and mutation uncertainty preserved");
                Check(File.ReadAllText(file) == xml && harness.LastImportNotes == null, "input retained and no stripping success note");
            }
            foreach (var result in new Func<FileInfo, object?>[] { f => null, f => Array.Empty<Named>(), f => new[] { new Named() } }) {
                var collection = new FakeCollection { Result = result }; New(collection).ImportHmiScreen("hmi", "", file);
                Check(collection.Calls == 1 && collection.FallbackCalls == 0 && collection.Seen == ImportOptions.Override, "legacy return handling/default retained without replay");
            }
            var optionless = new Optionless(); New(optionless).ImportHmiScreen("hmi", "", file);
            Check(optionless.Calls == 1, "pre-invocation overload discovery retained");
            foreach (var kind in new[] { "preflight", "missing file", "no project", "no software", "no collection" }) {
                var collection = new FakeCollection(); var harness = New(collection); var path = file;
                if (kind == "preflight") harness.InvalidInput = true;
                if (kind == "missing file") path += ".missing";
                if (kind == "no project") harness.NoProject = true;
                if (kind == "no software") harness.Container.Software = null;
                if (kind == "no collection") harness.Container.Software = new Software();
                Failure(() => harness.ImportHmiScreen("hmi", "", path));
                Check(collection.Calls == 0 && collection.FallbackCalls == 0, "zero calls for " + kind);
            }
            Failure(() => New(new MissingImporter()).ImportHmiScreen("hmi", "", file));
            Check(true, "missing overload rejected");
            File.WriteAllText(Path.Combine(dir, "second.xml"), xml);
            File.WriteAllText(Path.Combine(dir, "third.xml"), xml);
            var files = Directory.EnumerateFiles(dir, "*.xml").ToArray();
            var batchCollection = new FakeCollection { Result = f => f.FullName == files[1] ? throw new Exception("batch unknown outcome") : new[] { new Named() } };
            var batch = New(batchCollection).ImportHmiScreensFromDirectory("hmi", "", dir);
            Check(batchCollection.Calls == 2 && batchCollection.FallbackCalls == 0, "batch stops on first failed screen");
            Check(batch.Imported!.SequenceEqual(new[] { Path.GetFileNameWithoutExtension(files[0]) }), "prior successful file retained");
            Check(batch.Failed!.Count == 1 && batch.Failed[0].Path == files[1] && batch.Failed[0].Error!.Contains("batch unknown outcome") && batch.Failed[0].Error!.Contains("later matching files were not attempted"), "failed path and unattempted remainder preserved");
            var invalid = new FakeCollection(); var invalidHarness = New(invalid); invalidHarness.InvalidInput = true;
            var rejected = invalidHarness.ImportHmiScreensFromDirectory("hmi", "", dir);
            Check(invalid.Calls == 0 && rejected.Failed!.Count == 1 && rejected.Imported!.Count == 0, "batch preflight failure also stops without native entry");
            Console.WriteLine($"HMI import fake-only checks passed: {checks}");
        } finally { Directory.Delete(dir, true); }
    }
}
'''.replace('__SCREEN__', screen).replace('__BATCH__', batch).replace('__HELPER__', helper).replace('__NAME_READER__', name_reader)
parent = (ROOT / 'bin-build').resolve()
work = parent / ('hmi-import-' + uuid.uuid4().hex)
# Inherit the worktree ACL, as in the technology-import checker.
work.mkdir(parents=True)
try:
    (work / 'Program.cs').write_text(program, encoding='utf-8', newline='\n')
    (work / 'Checks.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>', encoding='utf-8')
    env = dict(os.environ, DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    subprocess.run([os.environ.get('DOTNET', 'dotnet'), 'run', '--project', str(work / 'Checks.csproj'), '-c', 'Release', '-p:NuGetAudit=false', '-p:RestoreSources=' + str(work)], env=env, check=True)
finally:
    if work.resolve().parent != parent:
        raise ValueError('scratch directory escaped bin-build')
    shutil.rmtree(work)
print('HMI import source wiring guards passed; no HMI/Siemens/TIA runtime or project was loaded.')
