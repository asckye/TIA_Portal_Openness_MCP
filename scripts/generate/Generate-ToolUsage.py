"""Build the embedded, pinned official-source catalog. No native calls or downloads."""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import re
import runpy
import sys
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'src/Shared/ToolUsageData.json'
PROFILES = ROOT / 'src/Logic/ModelContextProtocol/ToolProfiles.resx'


def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def new_v4_tools(root=ROOT):
    tree = ast.parse((root / 'scripts/generate/Generate-Phase6Plan.py').read_text('utf-8-sig'))
    entries = [ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
               and any(isinstance(t, ast.Name) and t.id == 'NEW_V4_TOOLS' for t in node.targets)]
    assert len(entries) == 1, 'Exactly one reviewed new-in-4.0 list is required'
    result = entries[0]
    expected = {'RenderPlcBlock': ('P6-48', 'FILE'), 'RenderPlcProgramAtlas': ('P6-48', 'FILE'),
                'StageImportFiles': ('P6-67', 'FILE'), 'ListStagedImportFiles': ('P6-67', 'READ'), 'CleanupStagedImportFiles': ('P6-67', 'FILE')}
    assert set(result) == set(expected), 'Reviewed new V4 list differs'
    for name, entry in result.items():
        assert (entry['owner'], entry['operation']) == expected[name], ('Unreviewed owner/category', name)
        assert entry['releases'] == ['14sp1', '15.1', '16', '17', '18', '19', '20', '21'], name
    return result


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


def workbench_tools(root=ROOT):
    text = (root / 'src/FoundationHost/WorkbenchControlTools.cs').read_text('utf-8-sig')
    block = text.split('string[] Names = {', 1)[1].split('};', 1)[0]
    names = re.findall(r'"([A-Za-z0-9]+)"', block)
    assert len(names) == len(set(names)) == 8, 'Closed Workbench tool roster differs'
    metadata = (root / 'src/Logic/ModelContextProtocol/ToolMetadata.cs').read_text('utf-8-sig')
    rows = dict(re.findall(r'\["([^"\n]+)"\] = new Classification\("L1", "Workbench", "(UI|READ)",', metadata))
    assert set(rows) == set(names), 'Workbench classification coverage differs'
    return rows


def registered_rosters(root=ROOT):
    """Source registrations, never stale build outputs or generated profile data."""
    sys.path.insert(0, str(root / 'scripts/checks'))
    import engine_sources
    import ported_families
    engine = engine_sources.EngineSources(root)
    targets = appendix_names(root)
    additions = new_v4_tools(root)
    targets.update({n: n for n in additions})
    baseline = {key: read(root / f'manifest/history/contracts-v3/baseline/{key}.json')['tools']
                for key in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
    for name, entry in additions.items():
        for key in entry['releases']:
            assert name not in {t['name'] for t in baseline[key]}, ('New tool exists in 3.x', name)
            baseline[key].append({'name': name})
    ported = {key: ported_families.additions(root, key) for key in baseline}
    targets.update({name: name for names in ported.values() for name in names})
    for key in tuple(baseline)[:6]:
        baseline[key].extend({'name': name} for name in sorted(ported[key]))
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
        registered = {host_map.get(n, n) for n in accepted | helpers} | ported[key]
        resolve_names([t['name'] for t in baseline[key]], registered, targets)
        rosters[key] = registered
    policy = (root / 'src/EngineHost/SharedToolCatalog.cs').read_text('utf-8')
    removed = set(re.findall(r'"(\w+)"', policy.split('Removed =', 1)[1].split('};', 1)[0]))
    extras = {host_map.get(n, n) for n, response in definitions.items() if response in ('DocumentExport', 'BatchDocumentExport', 'DocumentImport', 'BatchDocumentImport')}
    for key in ('20', '21'):
        rosters[key] = (rosters[key] - removed) | rosters['19'] | extras
    for key in rosters:
        rosters[key].update(workbench_tools(root))
    return rosters, mapping


def validate_coverage(rosters, calls):
    for profile, keys in (('full-engine', ('20', '21')), ('plc-foundation', ('14sp1', '15.1', '16', '17', '18', '19'))):
        expected = set().union(*(rosters[k] for k in keys))
        if profile == 'plc-foundation': expected |= {'ExportPlcBlockDocuments', 'ExportPlcBlocksDocuments', 'ImportPlcBlockDocuments', 'ImportPlcBlocksDocuments'}
        assert set(calls['profiles'][profile]) == expected, (profile, 'Call example coverage differs from tool roster',
                                                          expected ^ set(calls['profiles'][profile]))


def validate_foundation_examples(calls, sequences):
    """Keep the VM argument lessons in every maintained call and reusable flow."""
    batch = {'ExportPlcBlocks', 'ExportPlcTypes', 'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory'}
    imports = {'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory'}
    builders = {'BuildPlcTagTable', 'BuildStructuredText', 'BuildFlgNetCall',
                'BuildPlcFcBlock', 'BuildPlcFbBlock', 'BuildPlcLadFcBlock', 'BuildPlcGlobalDb', 'BuildPlcUdt'}

    def check(name, arguments, location):
        if name in batch:
            path = arguments.get('softwarePath', '')
            assert path.startswith('devices/') or ('exact softwarePath' in path and 'GetProjectTree' in path), (location, 'batch requires exact software path')
        if (arguments.get('dryRun') is False or arguments.get('confirm') is True) and name not in ('StageImportFiles', 'CleanupStagedImportFiles'):
            assert arguments.get('expectedProjectFile'), (location, 'real call requires expectedProjectFile')
        if name in imports:
            pattern = arguments.get('regexName', '')
            assert pattern and re.fullmatch(pattern, 'Main.xml') and not re.fullmatch(pattern, 'Main'), (location, 'regex must select the file name including extension')
        if name in builders:
            assert arguments.get('outputReleaseKey') == '21', (location, 'Foundation VM builder example must select V21')

    for name, entry in calls['profiles']['plc-foundation'].items():
        check(name, entry['arguments'], name)
        execution = entry.get('execution', {}).get('arguments')
        if execution:
            check(name, dict(entry['arguments'], **execution), name + '/execution')
        for operation, example in entry.get('operations', {}).items():
            check(name, example['arguments'], name + '/' + operation)
    for sequence in sequences:
        if sequence['profile'] == 'plc-foundation':
            for index, step in enumerate(sequence['steps']):
                check(step['tool'], step['arguments'], sequence['id'] + '/' + str(index))


def validate_retest_examples(calls, sequences, metadata):
    """Guard retest lessons without advertising Foundation parameters on full engines."""
    imports = ('ImportPlcBlock', 'ImportPlcType', 'ImportPlcTagTable')
    for name in imports:
        entry = calls['profiles']['plc-foundation'][name]
        assert entry['arguments']['overwrite'] is False, (name, 'explicit no-replacement preview')
        execution = entry['execution']['arguments']
        assert execution['overwrite'] is True and execution['expectedProjectFile'], (name, 'explicit bound replacement')
        text = entry['note']
        for required in ('overwrite=false refuses replacement.', 'INVALID_ARGUMENT', 'parameter=overwrite',
                         'rejected-before-operation', 'not-started', 'no Workbench request',
                         'C:/Examples/exports/Tags.xml', 'C:\\Examples\\exports\\Tags.xml',
                         'normalized full path', 'An existing Openness XML file is required.', 'parameter=importPath'):
            assert required in text, (name, required)
        assert 'Native imports do not overwrite an existing logical object.' not in text, name
        assert entry['parameters']['importPath']['requiresBinding'] is True, name
        full = calls['profiles']['full-engine'][name]
        assert full == entry, (name, 'Shared V20/V21 examples must be the Foundation examples')

    indexed = {entry['id']: entry for entry in sequences}
    for topic in ('foundation-approval-precheck', 'foundation-tag-table-round-trip', 'foundation-block-round-trip', 'plc-xml-round-trip'):
        sequence = indexed['sequence/' + topic]
        full = topic == 'plc-xml-round-trip'
        keys = ['20', '21'] if full else ['14sp1', '15.1', '16', '17', '18', '19', '20', '21']
        assert sequence['releaseKeys'] == keys, topic
        assert sequence['profile'] == ('full-engine' if full else 'plc-foundation'), topic
        assert 'native' in sequence['validation'] and 'NOT RUN' in sequence['validation'], topic
        for key in keys:
            tools = {tool['name']: tool['inputSchema'] for tool in read(ROOT / f'manifest/contracts/v4/baseline/{key}.json')['tools']}
            for step in sequence['steps']:
                schema = tools[step['tool']]
                arguments = step['arguments']
                assert set(schema.get('required', [])) <= arguments.keys() <= schema['properties'].keys(), (key, topic, step)
                if not full and arguments.get('dryRun') is False:
                    assert arguments.get('confirm') is True and arguments.get('expectedProjectFile'), (key, topic, step)

    table = indexed['sequence/foundation-tag-table-round-trip']
    tools = [step['tool'] for step in table['steps']]
    assert tools.index('ExportPlcTagTable') < tools.index('CreatePlcTag') < tools.index('ImportPlcTagTable'), tools
    assert tools[-1] == 'ListPlcTags' and 'tag added after export is gone' in table['steps'][-1]['expect'], table['id']
    assert 'Edit this exported file' in next(step['expect'] for step in table['steps']
                                           if step['tool'] == 'ExportPlcTagTable' and step['arguments']['dryRun'] is False), table['id']
    block = indexed['sequence/foundation-block-round-trip']
    tools = [step['tool'] for step in block['steps']]
    assert tools.index('ExportPlcBlock') < tools.index('ImportPlcBlock') < tools.index('CompilePlcSoftware'), tools
    assert tools[-1] == 'CompilePlcSoftware' and block['steps'][-1]['arguments']['dryRun'] is False, block['id']
    assert 'remove or move an old Main.xml' in block['preconditions'], block['id']
    assert 'Exports need no Workbench approval' in block['notes'], block['id']
    for sequence in (table, block):
        for step in sequence['steps']:
            if step['tool'] in imports:
                assert step['arguments']['overwrite'] is True, (sequence['id'], step)
    refusal = indexed['sequence/foundation-approval-precheck']
    assert {step['tool'] for step in refusal['steps']} >= {*imports, 'CreatePlcTag', 'CreatePlcTagTable', 'CloseProject'}
    for step in refusal['steps'][1:]:
        assert all(token in step['expect'] for token in ('no Workbench request', 'rejected-before-operation', 'not-started')), step
    approval = ' '.join(metadata['resultReading']['ResponseMessage'])
    for required in ('0.1-6.5 seconds', 'need no approval click', 'PRECONDITION_FAILED', 'parameter=softwarePath', 'parameter=name', 'parameter=overwrite'):
        assert required in approval, required


def profiles_resource():
    # Reuse the owning generator without writing its unrelated plans/manifests.
    return runpy.run_path(str(ROOT / 'scripts/generate/Generate-Phase6Plan.py'))['resource_text']()


def source_metadata(sources):
    """Usage topics follow registrations; tools-list staleness belongs to Check-ToolsList."""
    full = {}
    for source in sources:
        for match in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', source):
            description = re.match(r'\)\s*,\s*Description\(\s*"([^"\n]*)', source[match.end():])
            assert description, ('Missing adjacent Description', match[1])
            tags = re.match(r'\[L\d\]\[([^\]]+)\]', description[1])
            assert tags, ('Missing domain', match[1])
            assert match[1] not in full, ('Duplicate registration', match[1])
            full[match[1]] = {'name': match[1], 'domain': tags[1].removeprefix('Category:')}
    return full


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

    rosters, _ = registered_rosters()
    from engine_sources import EngineSources
    full = source_metadata(EngineSources(ROOT).sources.values())
    assert rosters['21'] - set(full) <= rosters['19'], 'New registrations must come from Foundation'
    additions = new_v4_tools()
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
        project_defined = domain in ('Meta', 'Guide', 'Bootstrap', 'Reports', 'Exports', 'PLC-Builders', 'Validation', 'Diagnostics', 'Simulation', 'Online-Monitoring') or name == 'GetToolUsage' or name in additions or name in workbench_tools()
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
        if name in ('ManageStartdriveParameter', 'GetDriveParameters', 'GetOnlineDriveParameters'):
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
    sequences = read(base / 'sequences.json')
    validate_foundation_examples(calls, sequences)
    validate_retest_examples(calls, sequences, meta)
    return {'schemaVersion': 2, 'scope': 'Pinned Siemens source documents and project-authored MCP/programming examples. Per-release contracts are read from the running engine. Templates, complete sources and fragments are distinguished; native acceptance is separate.',
            'sources': sources, 'documents': documents, 'tools': mappings,
            'languages': library['languages'], 'examples': library['examples'],
            'engineSourceCalls': read(ROOT / 'tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json'),
            'engineSourceSequences': read(ROOT / 'tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceSequences.json'),
            'calls': calls, 'sequences': sequences, **{k: v for k, v in meta.items() if k != 'schemaVersion'}}


class RosterTests(unittest.TestCase):
    def test_ported_roster_keeps_optional_action_switches(self):
        sys.path.insert(0, str(ROOT / 'scripts/checks'))
        import ported_families
        rows = ported_families.parse('''new Family("test", "test", new[] { "20", "21" },
            new[] { "Tool" }, new Dictionary<string, string[]> { ["write"] = new[] { "21" } })''')
        self.assertEqual(["20", "21"], rows["test"]["releases"])
        self.assertEqual(["Tool"], rows["test"]["tools"])

    def test_ported_roster_refuses_unparsed_and_duplicate_rows(self):
        sys.path.insert(0, str(ROOT / 'scripts/checks'))
        import ported_families
        row = 'new Family("test", "test", new[] { "20" }, new[] { "Tool" })'
        for source in (row + row, row + 'new Family("other", "other", releases, tools)'):
            with self.subTest(source=source), self.assertRaises(AssertionError):
                ported_families.parse(source)

    def test_retest_lessons(self):
        validate_retest_examples(read(ROOT / 'reference/tool-examples/calls.json'),
                                 read(ROOT / 'reference/tool-examples/sequences.json'),
                                 read(ROOT / 'reference/tool-examples/metadata.json'))

    def test_retest_lessons_cannot_disappear(self):
        for mutation in ('overwrite', 'path', 'full-parameters', 'sequence', 'readback', 'compile', 'approval', 'identity', 'release'):
            calls = read(ROOT / 'reference/tool-examples/calls.json')
            sequences = read(ROOT / 'reference/tool-examples/sequences.json')
            metadata = read(ROOT / 'reference/tool-examples/metadata.json')
            table = next(s for s in sequences if s['id'] == 'sequence/foundation-tag-table-round-trip')
            block = next(s for s in sequences if s['id'] == 'sequence/foundation-block-round-trip')
            entry = calls['profiles']['plc-foundation']['ImportPlcTagTable']
            if mutation == 'overwrite': entry['execution']['arguments'].pop('overwrite')
            elif mutation == 'path': entry['note'] = entry['note'].replace('normalized full path', '')
            elif mutation == 'full-parameters': calls['profiles']['full-engine']['ImportPlcBlock']['arguments']['overwrite'] = True
            elif mutation == 'sequence': sequences.remove(table)
            elif mutation == 'readback': table['steps'].pop()
            elif mutation == 'compile': block['steps'].pop()
            elif mutation == 'approval': metadata['resultReading']['ResponseMessage'].pop()
            elif mutation == 'identity': table['steps'][-2]['arguments'].pop('expectedProjectFile')
            elif mutation == 'release': table['releaseKeys'].remove('19')
            with self.subTest(mutation=mutation), self.assertRaises((AssertionError, KeyError)):
                validate_retest_examples(calls, sequences, metadata)

    def test_profiles_resource_uses_current_call_examples(self):
        resource = ET.fromstring(profiles_resource())
        catalog = json.loads(resource.find(".//data[@name='Catalog']/value").text)
        calls = read(ROOT / 'reference/tool-examples/calls.json')
        for key, release in catalog['releases'].items():
            profile = 'full-engine' if key in ('20', '21') else 'plc-foundation'
            for tool in release:
                if tool['currentName'] in ('ImportPlcBlock', 'ImportPlcType', 'ImportPlcTagTable'):
                    self.assertEqual(tool['arguments'], calls['profiles'][profile][tool['currentName']]['arguments'])

    def test_foundation_vm_lessons(self):
        validate_foundation_examples(read(ROOT / 'reference/tool-examples/calls.json'),
                                     read(ROOT / 'reference/tool-examples/sequences.json'))

    def test_foundation_batch_aliases_are_rejected(self):
        for name in ('ExportPlcBlocks', 'ExportPlcTypes', 'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory'):
            calls = read(ROOT / 'reference/tool-examples/calls.json')
            calls['profiles']['plc-foundation'][name]['arguments']['softwarePath'] = 'PLC_1'
            with self.subTest(name=name), self.assertRaises(AssertionError):
                validate_foundation_examples(calls, [])

    def test_foundation_real_calls_require_project_identity(self):
        calls = read(ROOT / 'reference/tool-examples/calls.json')
        calls['profiles']['plc-foundation']['SaveProject']['execution']['arguments']['expectedProjectFile'] = ''
        with self.assertRaises(AssertionError):
            validate_foundation_examples(calls, [])
        sequences = read(ROOT / 'reference/tool-examples/sequences.json')
        sequence = next(s for s in sequences if s['id'] == 'sequence/foundation-write-tags')
        next(s for s in sequence['steps'] if s['arguments'].get('dryRun') is False)['arguments'].pop('expectedProjectFile')
        with self.assertRaises(AssertionError):
            validate_foundation_examples(read(ROOT / 'reference/tool-examples/calls.json'), sequences)

    def test_foundation_directory_regex_includes_extension(self):
        for name in ('ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory'):
            calls = read(ROOT / 'reference/tool-examples/calls.json')
            calls['profiles']['plc-foundation'][name]['arguments']['regexName'] = '^Main$'
            with self.subTest(name=name), self.assertRaises(AssertionError):
                validate_foundation_examples(calls, [])

    def test_foundation_builders_do_not_inherit_the_host_release(self):
        calls = read(ROOT / 'reference/tool-examples/calls.json')
        for name, entry in calls['profiles']['plc-foundation'].items():
            if 'outputReleaseKey' not in entry['arguments']:
                continue
            previous = entry['arguments']['outputReleaseKey']
            entry['arguments']['outputReleaseKey'] = '{release}'
            with self.subTest(name=name), self.assertRaises(AssertionError):
                validate_foundation_examples(calls, [])
            entry['arguments']['outputReleaseKey'] = previous

    def test_generation_does_not_read_the_guard_manifest(self):
        original_read = read
        def without_manifest(path):
            self.assertNotEqual(path, ROOT / 'manifest/tools-list.json')
            return original_read(path)
        with patch(__name__ + '.read', side_effect=without_manifest):
            catalog = generate()
        rosters, _ = registered_rosters()
        self.assertTrue(rosters['21'] <= catalog['tools'].keys())

    def test_source_metadata_uses_current_names_and_normalizes_domains(self):
        sources = ['[McpServerTool(Name = "ConnectPortal"), Description("[L1][Portal][SESSION] Connect.")]',
                   '[McpServerTool(Name="GetHardware"), Description(\n "[L2][Category:Hardware]" + " Read.")]']
        self.assertEqual(source_metadata(sources), {
            'ConnectPortal': {'name': 'ConnectPortal', 'domain': 'Portal'},
            'GetHardware': {'name': 'GetHardware', 'domain': 'Hardware'}})
        for invalid in ([sources[0], sources[0]], ['[McpServerTool(Name="Missing")]'],
                        ['[McpServerTool(Name="Missing"), Description("no domain")]']):
            with self.subTest(invalid=invalid), self.assertRaises(AssertionError): source_metadata(invalid)

    def test_reviewed_additions_are_exact(self):
        additions = new_v4_tools()
        self.assertEqual(set(additions), {'RenderPlcBlock', 'RenderPlcProgramAtlas', 'StageImportFiles', 'ListStagedImportFiles', 'CleanupStagedImportFiles'})
        targets = {'Old': 'New', **{n: n for n in additions}}
        resolve_names(targets, {'New', *additions}, targets)
        for names in ({'New', 'RenderPlcBlock'}, {'New', *additions, 'RenderPlcOther'}):
            with self.assertRaises(AssertionError): resolve_names(targets, names, targets)

    def test_migrated_and_unmigrated_names(self):
        targets = {'BuildOld': 'BuildNew', 'ReadOld': 'GetNew', 'Typed': 'Typed'}
        self.assertEqual(resolve_names(targets, {'BuildNew', 'ReadOld', 'Typed'}, targets),
                         {'BuildOld': 'BuildNew', 'ReadOld': 'ReadOld', 'Typed': 'Typed'})
        for names in ({'BuildOld', 'BuildNew', 'ReadOld', 'Typed'}, {'BuildNew', 'Typed'}, {'BuildNew', 'ReadOld', 'Typed', 'Extra'}):
            with self.assertRaises(AssertionError): resolve_names(targets, names, targets)

    def test_profile_coverage_stays_strict(self):
        documents = {'ExportPlcBlockDocuments', 'ExportPlcBlocksDocuments', 'ImportPlcBlockDocuments', 'ImportPlcBlocksDocuments'}
        rosters = {k: {'New', 'Old'} | documents if k in ('20', '21') else {'Old'} for k in ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')}
        calls = {'profiles': {'full-engine': {name: {} for name in {'New', 'Old'} | documents},
                              'plc-foundation': {name: {} for name in {'Old'} | documents}}}
        validate_coverage(rosters, calls)
        calls['profiles']['full-engine']['Extra'] = {}
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
    profiles = profiles_resource()
    if args.check:
        assert OUTPUT.read_text('utf-8') == encoded, 'ToolUsageData.json is stale; run Generate-ToolUsage.py'
        assert PROFILES.read_text('utf-8') == profiles, 'ToolProfiles.resx is stale; run Generate-ToolUsage.py'
    else:
        OUTPUT.write_text(encoded, encoding='utf-8', newline='\n')
        PROFILES.write_text(profiles, encoding='utf-8', newline='\n')
    print(f"Official catalog: {len(data['documents'])} complete documents, {sum(len(d['examples']) for d in data['documents'])} indexed example blocks/methods, {len(data['tools'])} tool mappings")
