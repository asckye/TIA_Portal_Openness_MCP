"""Build the embedded, pinned official-source catalog. No native calls or downloads."""
import argparse
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'tools/openness-shared/ToolUsageData.json'


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def generate():
    sources, documents = [], []
    for folder, prefix in [('siemens-openness', 'guides'), ('siemens-code-snippets', 'snippets')]:
        base = ROOT / 'reference' / folder
        upstream = read(base / 'UPSTREAM.json')
        sources.append(dict(upstream, id=prefix))
        files = sorted((base / 'skills').rglob('*')) if prefix == 'guides' else sorted(base.rglob('*'))
        for path in files:
            if not path.is_file() or path.suffix not in ('.md', '.cs', '.csproj', '.props', '.targets'):
                continue
            relative = path.relative_to(base).as_posix()
            upstream_path = (upstream['upstreamSkillsPath'] + '/' + path.relative_to(base / 'skills').as_posix()) if prefix == 'guides' else relative
            text = path.read_text('utf-8-sig').replace('\r\n', '\n')
            blocks = []
            if path.suffix == '.md':
                for match in re.finditer(r'^```([^\n]*)\n(.*?)^```\s*$', text, re.M | re.S):
                    blocks.append({'line': text.count('\n', 0, match.start()) + 1,
                                   'language': match[1].strip(), 'lineCount': len(match[2].splitlines())})
            else:
                blocks = [{'line': n, 'symbol': match.group(1)} for n, line in enumerate(text.splitlines(), 1)
                          if (match := re.search(r'public (?:async )?(?:void|Task) (\w+)\(', line))]
            documents.append({'id': prefix + '/' + relative, 'source': prefix,
                'url': upstream['repository'] + '/blob/' + upstream['commit'] + '/' + upstream_path,
                'sha256': hashlib.sha256(text.encode()).hexdigest(), 'lines': len(text.split('\n')),
                'examples': blocks, 'text': text})

    full = {t['name']: t for t in read(ROOT / 'manifest/tools-list.json')['tools']}
    names = set(full) | {'GetToolUsage'}
    for catalog in (ROOT / 'bin-build/multi-version').glob('tools-*.json'):
        names.update(t['name'] for t in read(catalog)['tools'])
    # Foundation-only contracts; explicit so generation also works in CI without build artifacts.
    names.update(['ReadPlcTags', 'ReadPlcUserConstants', 'ReadPlcSystemConstants', 'CreatePlcTag',
                  'CreatePlcTagTable', 'CreatePlcUserConstant', 'PlanPlcExternalSourceImport', 'DiagnosePortalConnectReadiness'])
    domains = {
        'Portal': ['session-and-project', 'licensing-and-firewall'],
        'Project': ['session-and-project'], 'Library': ['global-library', 'libraries-and-alarms'],
        'Reflection': ['engineering-objects', 'object-tree-walking'],
        'Security': ['security'], 'Safety': ['plc-safety-administration'],
        'PLC-Software': ['blocks', 'software-hierarchy'], 'PLC-Alarms': ['libraries-and-alarms'],
        'PLC-TechnologyObjects': ['technology-objects'], 'PLC-Online': ['online-and-download'],
        'Hardware': ['devices-and-hardware', 'hardware-and-modules'],
    }
    rules = [
        (r'(Dcc|Dcb)', ['drive-control-charts']),
        (r'(StartdriveParameter|DriveParameters)', ['parameters', 'drive-objects']),
        (r'DriveTelegrams', ['telegrams']),
        (r'DriveSafety', ['safety-commissioning']),
        (r'DriveSecurity', ['security', 'drive-objects']),
        (r'(Drive|TechnologyExtensions)', ['drive-objects', 'hardware-and-modules']),
        (r'(Subnet|Profinet|Network|PortInterconnection|IoSystem|TransferArea)', ['networks-and-drivecliq', 'devices-and-hardware']),
        (r'(TagTable|PlcTag|PlcUserConstant|PlcSystemConstant)', ['tags-and-tagtables']),
        (r'(PlcSoftwareUnit|PlcUnitObject|PlcUserGroup)', ['sw-units']),
        (r'(Documents|SimaticSd)', ['simatic-sd']),
        (r'(GetTypes|TypeInfo|ExportType|ImportType|PlcType|PlcUdt)', ['plc-data-types']),
        (r'(Connect|Portal|Project)', ['session-and-project']),
    ]
    snippet_topics = {
        'parameters': ['Plain.Startdrive/ParameterSnippets.cs', 'WithExtensions.Startdrive/ParameterSnippets.cs'],
        'drive-objects': ['Plain.Startdrive/DriveObjectSnippets.cs', 'Plain.Startdrive/StartdriveSnippets.cs'],
        'drive-control-charts': ['Plain.Startdrive.Dcc/DccSnippets.cs'],
        'telegrams': ['Plain.Startdrive/TelegramSnippets.cs'],
        'safety-commissioning': ['Plain.Startdrive/SafetySnippets.cs'],
        'plc-safety-administration': ['Plain.Step7/PLC/SafetySnippets.cs'],
        'blocks': ['Plain.Step7/PLC/ProgramBlocks/ProgramBlockSnippets.cs', 'Plain.Step7/PLC/DataBlockSnippets.cs'],
        'tags-and-tagtables': ['Plain.Step7/PLC/TagTableSnippets.cs'],
        'sw-units': ['Plain.Step7/PLC/SoftwareUnitsSnippets.cs'],
        'global-library': ['Plain.Step7/LibrarySnippets.cs'],
        'libraries-and-alarms': ['Plain.Step7/PLC/PlcAlarmTextListSnippets.cs'],
        'session-and-project': ['Plain.Step7/TiaProcessSnippets.cs'],
        'networks-and-drivecliq': ['Plain.Step7/NetworkSnippets.cs', 'Plain.Startdrive/NetworkSnippets.cs'],
        'devices-and-hardware': ['Plain.Step7/HardwareCatalogSnippets.cs', 'Plain.Step7/TransferAreaSnippets.cs'],
        'hardware-and-modules': ['Plain.Startdrive/HardwareSnippets.cs'],
        'online-and-download': ['Plain.Step7/OnlineSnippets.cs', 'Plain.Startdrive/OnlineDriveSnippets.cs'],
        'security': ['Plain.Step7/SecuritySnippets.cs'],
        'technology-objects': ['Plain.Step7/PLC/TechnologyObjectSnippets.cs'],
    }
    manuals = read(ROOT / 'reference/siemens-openness/manual-examples.json')['references']
    mappings = {}
    for name in sorted(names):
        domain = full.get(name, {}).get('domain', '')
        topics = domains.get(domain, [])
        # HMI/third-party/offline composition contracts must not be presented as PLC native examples.
        project_defined = domain in ('Meta', 'Guide', 'Bootstrap', 'Reports', 'Exports', 'PLC-Builders', 'Validation', 'Diagnostics', 'Simulation', 'Online-Monitoring') or name == 'GetToolUsage'
        unsupported = 'Hmi' in name or 'Unified' in name or 'Sivarc' in name or 'SiVArc' in name or domain in ('PLC-OpcUA', 'VersionControl')
        if not project_defined and not unsupported:
            for pattern, matched in rules:
                if re.search(pattern, name):
                    topics = matched
                    break
        if project_defined or unsupported:
            topics = []
        references = []
        for topic in topics:
            references.append('guides/skills/' + topic + '/SKILL.md')
            for suffix in snippet_topics.get(topic, []):
                references += [d['id'] for d in documents if d['id'].endswith(suffix)]
        mappings[name] = {
            'relationship': 'related-api-patterns' if references else 'no-direct-official-example',
            'explanation': ('Official topic examples, not an official implementation of this MCP wrapper. Read the full source and its setup; wrapper actions/arguments come from inputSchema.' if references else
                            'This project-defined/composed or uncovered API tool has no matching direct example in the pinned official sources. Use the project contract/template; do not invent an official equivalent.'),
            'documents': list(dict.fromkeys(references)),
            'sourceReleaseEvidence': 'Pinned snippet source fixtures use .zap21; upstream README still says V20. Guides are reference patterns with no per-release acceptance. V14 SP1-V19 require the selected foundation contract; no newer-API compatibility claim.',
        }
        manual_topics = []
        if not project_defined:
            if 'Sivarc' in name or 'SiVArc' in name:
                manual_topics = ['sivarc', 'sivarc-rules']
            elif domain == 'VersionControl' and 'Git' not in name:
                manual_topics = ['vci', 'vci-formats']
            elif domain == 'PLC-OpcUA':
                manual_topics = ['opcua']
            elif domain == 'HMI-Unified':
                manual_topics = ['hmi-unified']
                if 'Dynamization' in name: manual_topics += ['hmi-dynamization']
                if 'Event' in name: manual_topics += ['hmi-events']
                if 'Screen' in name: manual_topics += ['hmi-screen']
            elif domain in ('HMI', 'HMI-Classic'):
                manual_topics = ['hmi-classic']
        mappings[name]['manualReferences'] = [m for m in manuals if m['topic'] in manual_topics]
        if name in ('ManageStartdriveParameter', 'ReadDriveParameters', 'ReadOnlineDriveParameters'):
            mappings[name]['manualsByRelease'] = {key: f'https://docs.tia.siemens.cloud/r/en-us/v{key}/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters' for key in ('20', '21')}
            mappings[name]['specificWorkflow'] = 'GetAuthoringGuide(startdrive-bico), GetRecipe(startdrive-bico-read). p2051[0] reported crash: do not automatically replay; dryRun can read Value. Parameters.Find exact name first; BICO references are not scalar values. Online and offline containers differ.'
    ids = {d['id'] for d in documents}
    assert all(set(m['documents']) <= ids for m in mappings.values())
    return {'schemaVersion': 1, 'scope': 'All Markdown guidance/examples in the pinned Siemens AI extension skills, and all C# source files plus dependency/setup text in the pinned Siemens code-snippet repository. Not all examples ever published in Siemens manuals; gaps are explicit per tool. No Siemens SDKs or engineering archives included.',
            'sources': sources, 'documents': documents, 'tools': mappings}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    data = generate()
    encoded = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
    if args.check:
        assert OUTPUT.read_text('utf-8') == encoded, 'ToolUsageData.json is stale; run Generate-ToolUsage.py'
    else:
        OUTPUT.write_text(encoded, encoding='utf-8')
    print(f"Official catalog: {len(data['documents'])} complete documents, {sum(len(d['examples']) for d in data['documents'])} indexed example blocks/methods, {len(data['tools'])} tool mappings")
