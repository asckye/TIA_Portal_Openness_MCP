"""Build the embedded, pinned official-source catalog. No native calls or downloads."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'src/Shared/ToolUsageData.json'


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def appendix_names(root=ROOT):
    text = (root / 'docs/development/phase6-review.md').read_text('utf-8-sig')
    block = text.split('A. ', 1)[-1].split('B. ', 1)[0]
    return dict(re.findall(r'^\| `([^`]+)` \| `([^`]+)` \|', block, re.M))


def resolve_names(baseline, registered, targets, merged=False):
    result = {}
    for old in baseline:
        candidates = {old, targets[old]} & registered
        if merged and old in ('GetAuthoringGuide', 'GetRecipe'):
            assert old not in registered, (old, 'merged alias still registered')
            candidates = {'GetToolUsage'} & registered
        assert len(candidates) == 1, (old, 'missing or double registration', candidates)
        result[old] = next(iter(candidates))
    assert set(result.values()) == registered, ('unmapped registrations', registered - set(result.values()))
    return result


def registered_rosters(root=ROOT):
    """Source registrations, never stale build outputs or generated profile data."""
    sys.path.insert(0, str(root / 'scripts/checks'))
    import engine_sources
    engine = engine_sources.EngineSources(root)
    targets = appendix_names(root)
    baseline = {key: read(root / f'manifest/contracts/baseline/{key}.json')['tools']
                for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
    full = set()
    for source in engine.sources.values():
        full.update(re.findall(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', source))
    mapping = resolve_names([t['name'] for t in baseline['21']], full, targets, merged=True)
    rosters = {key: {mapping[t['name']] for t in baseline[key]} for key in ('20', '21')}
    foundation_sources = {p.name: p.read_text('utf-8-sig') for p in (root / 'src/FoundationHost').glob('*.cs')}
    definitions = {}
    for line in foundation_sources['FoundationTools.cs'].splitlines():
        match = re.match(r'\s*new\("([^"]+)"', line)
        if match:
            response = re.search(r',\s*"([^"]+)"\),?\s*$', line)
            definitions[match[1]] = response[1] if response else ''
    helpers = set()
    for name, source in foundation_sources.items():
        if name == 'FoundationTools.cs': continue
        helpers.update(re.findall(r'new (?:Offline\w+Tool|PassiveDiagnosticTool)\("([^"]+)"', source))
        helpers.update(re.findall(r'\bName\s*=\s*"([^"]+)"', source))
    helpers &= targets.keys() | set(targets.values())
    wrapped = any('new FoundationV4Tool(' in s for s in foundation_sources.values())
    host_map = dict(re.findall(r'\["([^"]+)"\]\s*=\s*"([^"]+)"',
                              foundation_sources.get('FoundationV4Tool.cs', '').split('internal static string Name')[0])) if wrapped else {}
    assert all(targets[old] == new for old, new in host_map.items()), 'Foundation names differ from appendix A'
    for key in tuple(baseline)[:6]:
        major = 14 if key == '14sp1' else int(key.split('.')[0])
        accepted = {n for n, response in definitions.items()
                    if not (response in ('HardwareCatalog', 'DeviceAdd') and major < 19)
                    and not (response == 'SpecialExport' and major < 16)
                    and not (response in ('DocumentExport', 'BatchDocumentExport', 'DocumentImport', 'BatchDocumentImport') and major < 20)
                    and not (n in ('GetPlcWatchTables', 'ListPlcWatchTables') and key == '14sp1')}
        registered = {host_map.get(n, n) for n in accepted | helpers}
        resolve_names([t['name'] for t in baseline[key]], registered, targets)
        rosters[key] = registered
    return rosters, mapping


def validate_coverage(rosters, calls):
    for profile, keys in (('full-engine', ('20', '21')), ('plc-foundation', ('14sp1', '15.1', '16', '17', '18', '19'))):
        expected = set().union(*(rosters[k] for k in keys))
        assert set(calls['profiles'][profile]) == expected, (profile, 'Call example coverage differs from tool roster',
                                                          expected ^ set(calls['profiles'][profile]))


def generate():
    sources, documents = [], []
    for folder, prefix in [('siemens-openness', 'guides'), ('siemens-code-snippets', 'snippets')]:
        base = ROOT / 'reference' / folder
        upstream = read(base / 'UPSTREAM.json')
        sources.append(dict(upstream, id=prefix))
        candidates = (base / 'skills').rglob('*') if prefix == 'guides' else base.rglob('*')
        # pathlib ordering folds case on Windows, but not on Linux. Keep the
        # shipped order explicitly so CI and local regeneration are identical.
        files = sorted(candidates, key=lambda p: tuple(part.lower() for part in p.relative_to(base).parts))
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
    rosters, current = registered_rosters()
    full = {name: dict(full[old], name=name) for old, name in current.items()}
    names = set().union(*rosters.values())
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
    ids = {d['id'] for d in documents}
    assert all(set(m['documents']) <= ids for m in mappings.values())
    base = ROOT / 'reference/tool-examples'
    library = read(base / 'languages/catalog.json')
    assert len({e['id'] for e in library['examples']}) == len(library['examples'])
    assert all(set(e['tools']) <= names for e in library['examples']), 'Example links to an unknown tool'
    for example in library['examples']:
        for asset in example.get('files', []):
            path = ROOT / asset['path']
            asset['content'] = path.read_text('utf-8-sig').replace('\r\n', '\n')
            asset['contentSha256'] = hashlib.sha256(asset['content'].encode()).hexdigest()
    meta = read(base / 'metadata.json')
    calls = read(base / 'calls.json')
    validate_coverage(rosters, calls)
    return {'schemaVersion': 2, 'scope': 'Pinned Siemens source documents and project-authored MCP/programming examples. Per-release contracts are read from the running engine. Templates, complete sources and fragments are distinguished; native acceptance is separate.',
            'sources': sources, 'documents': documents, 'tools': mappings,
            'languages': library['languages'], 'examples': library['examples'],
            'calls': calls, 'sequences': read(base / 'sequences.json'), **{k: v for k, v in meta.items() if k != 'schemaVersion'}}


class RosterTests(unittest.TestCase):
    def test_migrated_and_unmigrated_names(self):
        targets = {'BuildOld': 'BuildNew', 'ReadOld': 'GetNew', 'Typed': 'Typed'}
        self.assertEqual(resolve_names(targets, {'BuildNew', 'ReadOld', 'Typed'}, targets),
                         {'BuildOld': 'BuildNew', 'ReadOld': 'ReadOld', 'Typed': 'Typed'})
        for names in ({'BuildOld', 'BuildNew', 'ReadOld', 'Typed'}, {'BuildNew', 'Typed'}, {'BuildNew', 'ReadOld', 'Typed', 'Extra'}):
            with self.assertRaises(AssertionError): resolve_names(targets, names, targets)

    def test_profile_coverage_stays_strict(self):
        rosters = {k: {'New'} if k in ('20', '21') else {'Old'} for k in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
        calls = {'profiles': {'full-engine': {'New': {}}, 'plc-foundation': {'Old': {}}}}
        validate_coverage(rosters, calls)
        calls['profiles']['full-engine']['Old'] = {}
        with self.assertRaises(AssertionError): validate_coverage(rosters, calls)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(RosterTests))
        sys.exit(0 if result.wasSuccessful() else 1)
    data = generate()
    encoded = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
    if args.check:
        assert OUTPUT.read_text('utf-8') == encoded, 'ToolUsageData.json is stale; run Generate-ToolUsage.py'
    else:
        OUTPUT.write_text(encoded, encoding='utf-8')
    print(f"Official catalog: {len(data['documents'])} complete documents, {sum(len(d['examples']) for d in data['documents'])} indexed example blocks/methods, {len(data['tools'])} tool mappings")
