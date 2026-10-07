"""Regenerate complete renderer goldens and reviewer samples from tracked XML inputs."""
import json
import os
from pathlib import Path
import subprocess
import uuid
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[5]
FIXTURES = Path(__file__).resolve().parent
ARTIFACTS = ROOT / 'bin-build/P6-62'


def main():
    runner = ARTIFACTS / 'generator'
    runner.mkdir(parents=True, exist_ok=True)
    local_source = ARTIFACTS / 'nuget-source'
    local_source.mkdir(exist_ok=True)
    project = runner / 'Generator.csproj'
    project.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<NuGetAudit>false</NuGetAudit><RestoreSources>' + str(local_source) + '</RestoreSources>'
        '</PropertyGroup><ItemGroup><ProjectReference Include="'
        + str(ROOT / 'src/Logic/TiaMcp.Logic.csproj')
        + '" /></ItemGroup></Project>\n', encoding='utf-8', newline='\n')
    (runner / 'Program.cs').write_text('''using System.Text.Json;
using TiaMcpServer.ModelContextProtocol;
var jobs = JsonSerializer.Deserialize<string[][]>(File.ReadAllText(args[0]))!;
foreach (var job in jobs) {
    var result = PlcProgramRenderer.Write(job[0], job[1], job[2] == "atlas", "21");
    if (!result.Ok) throw new Exception(result.Error!.Message);
    Console.WriteLine(Path.GetFileName(job[1]));
}
''', encoding='utf-8', newline='\n')
    samples = ARTIFACTS / 'samples'
    samples.mkdir(exist_ok=True)
    inputs = sorted((ROOT / 'plugin/skill/lad-cookbook').glob('*.xml'))
    inputs += sorted((ROOT / 'templates/mcp-full-e2e-verify/plc/blocks').glob('*.xml'))
    inputs += [ROOT / 'templates/plc/block-xml/DB_HMI_Interface.xml']
    inputs += sorted((FIXTURES / 'Parts').glob('*.xml'))
    assert len(inputs) == 14, f'Expected 14 inputs, got {len(inputs)}'
    inputs += [FIXTURES / 'Catalog/Primer.xml']
    stage = ARTIFACTS / ('goldens-' + uuid.uuid4().hex)
    stage.mkdir()
    jobs, targets = [], []
    for index, source in enumerate(inputs):
        relative = source.relative_to(ROOT).as_posix()
        target = (Path(str(source) + '.html') if source.parent in (FIXTURES / 'Parts', FIXTURES / 'Catalog')
                  else FIXTURES / (relative.replace('/', '_') + '.html'))
        output = stage / f'{index}.html'
        jobs.append([str(source), str(output), 'block'])
        targets.append((output, target, [(str(source), relative)]))
    exports = stage / 'exports'
    exports.mkdir()
    mappings = []
    for source in inputs[:4]:
        copy = exports / source.name
        copy.write_bytes(source.read_bytes())
        mappings.append((str(copy), source.relative_to(ROOT).as_posix()))
    output = stage / 'atlas.html'
    jobs.append([str(exports), str(output), 'atlas'])
    targets.append((output, FIXTURES / 'Catalog/cookbook.html', mappings))
    roster = stage / 'jobs.json'
    roster.write_text(json.dumps(jobs), encoding='utf-8')
    environment = dict(os.environ, MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0',
                       UseSharedCompilation='false', BuildInParallel='false', NuGetAudit='false',
                       TEMP=str(ARTIFACTS), TMP=str(ARTIFACTS))
    subprocess.run(['dotnet', 'run', '--project', str(project), '-c', 'Release',
                    '-p:RestoreSources=' + str(local_source), '--', str(roster)],
                   cwd=ROOT, env=environment, check=True)
    for output, golden, mappings in targets:
        html = output.read_text(encoding='utf-8')
        for absolute, relative in mappings:
            # Match WebUtility.HtmlEncode for local filenames (including quotes).
            html = html.replace(escape(absolute, {'"': '&quot;', "'": '&#39;'}), relative)
        golden.parent.mkdir(exist_ok=True)
        golden.write_text(html, encoding='utf-8', newline='\n')
        (samples / ('atlas.html' if golden.name == 'cookbook.html' else golden.name)).write_text(
            html, encoding='utf-8', newline='\n')
    print('Generated 15 block goldens, 1 catalog golden and 16 reviewer samples.')


if __name__ == '__main__':
    main()
