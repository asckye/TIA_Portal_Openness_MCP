#!/usr/bin/env python3
"""Source wiring guards + extracted production helper against fake collections; never loads Siemens."""
from pathlib import Path
import os
import shutil
import subprocess
import uuid
from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]
sources = EngineSources()
helper = sources.member('TryImportEngineeringObjectIntoCollection', signature='bool overwrite')
technology = '\n'.join((
    sources.member('ImportTechnologyObject', owner='Portal', signature='public void'),
    sources.member('ImportTechnologyObject', signature='private void'),
    sources.member('ImportTechnologyObject', owner='TechnologyObjectsService', tool=False),
    sources.member('ImportTechnologyObjectsFromDirectory', tool=False)))
assert 'bool overwrite = true' in technology
assert 'importPath, true, new List<string>()' in technology
assert 'file, overwrite, imported' in technology
assert 'imported.Add(name)' not in technology
assert 'Batch stopped; later files were not attempted.' in technology
assert 'break;' in technology
assert helper.count('method.Invoke(') == 1
assert 'typeof(FileInfo) });' not in helper
assert 'overwrite ? ImportOptions.Override : ImportOptions.None' in helper
assert 'GetFileNameWithoutExtension' not in helper

program = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
enum ImportOptions { None, Override }
class Named { public string Name { get; set; } = "NativeName"; }
class Collection {
    public int Calls;
    public int FallbackCalls;
    public ImportOptions Seen;
    public Func<object?> Result = () => new[] { new Named() };
    public object? Import(FileInfo file, ImportOptions options) { Calls++; Seen = options; return Result(); }
    public object Import(FileInfo file) { FallbackCalls++; throw new Exception("must not fall back"); }
}
class Optionless { public int Calls; public object Import(FileInfo file) { Calls++; return new[] { new Named() }; } }
class BrokenName { public string Name => throw new Exception("name read failed"); }
class Program {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static IEnumerable Partial() { yield return new Named { Name = "ActualFirst" }; throw new Exception("enumeration failed"); }
    static void Main() {
        var file = Path.GetTempFileName();
        try {
            foreach (var overwrite in new[] { false, true }) {
                var col = new Collection(); var names = new List<string>();
                Check(TryImportEngineeringObjectIntoCollection(col, file, overwrite, names, out var error), "success");
                Check(error == null && col.Calls == 1 && col.FallbackCalls == 0, "single call");
                Check(col.Seen == (overwrite ? ImportOptions.Override : ImportOptions.None), "overwrite mapping");
                Check(names.SequenceEqual(new[] { "NativeName" }), "native identity");
            }
            var multiple = new Collection { Result = () => new[] { new Named { Name = "A" }, new Named { Name = "B" } } };
            var found = new List<string>();
            Check(TryImportEngineeringObjectIntoCollection(multiple, file, false, found, out _) && found.SequenceEqual(new[] { "A", "B" }), "all identities");
            var only = new Optionless();
            Check(!TryImportEngineeringObjectIntoCollection(only, file, false, new List<string>(), out var missing) && only.Calls == 0 && missing!.Contains("no import was attempted"), "optionless rejected");
            var absent = new Collection();
            Check(!TryImportEngineeringObjectIntoCollection(absent, file + ".missing", false, new List<string>(), out _) && absent.Calls == 0, "missing file preflight");
            foreach (var result in new Func<object?>[] { () => throw new Exception("native failure"), () => null, () => Array.Empty<Named>(), () => "not identities", () => new object[] { new BrokenName() }, () => new[] { new Named { Name = " " } } }) {
                var col = new Collection { Result = result };
                Check(!TryImportEngineeringObjectIntoCollection(col, file, false, new List<string>(), out var error), "uncertain result rejected");
                Check(col.Calls == 1 && col.FallbackCalls == 0 && error!.Contains("project may have changed"), "uncertain no retry");
            }
            var partial = new Collection { Result = () => Partial() }; found = new List<string> { "PreviousFile" };
            Check(!TryImportEngineeringObjectIntoCollection(partial, file, true, found, out var partialError), "partial fails");
            Check(found.SequenceEqual(new[] { "PreviousFile", "ActualFirst" }) && partialError!.Contains("ActualFirst") && !partialError.Contains("PreviousFile"), "partial identities preserved");
            Console.WriteLine($"Technology import fake-collection checks passed: {checks}");
        } finally { File.Delete(file); }
    }
__HELPER__
}
'''.replace('__HELPER__', helper)
parent = (ROOT / 'bin-build').resolve()
work = parent / ('technology-import-' + uuid.uuid4().hex)
# Inherit the worktree ACL; TemporaryDirectory's private Windows ACL blocks sandboxed builds.
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
print('Technology import source wiring guards passed; no Siemens runtime acceptance claimed.')
