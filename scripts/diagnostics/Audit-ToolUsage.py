"""Require every release/tool to have actual MCP usage retrieval and schema checks."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
def read(path):
    return json.loads(path.read_text('utf-8-sig'))


def main():
    matrix = read(ROOT / 'manifest/version-tools.json')['releases']
    release = read(ROOT / 'manifest/release-build.json')['release']
    source = ROOT / 'tools/openness-shared/ToolUsageData.json'
    catalog = read(source)
    records = read(ROOT / 'bin-build/multi-version/transport/tool-usage.json')
    for major in (20, 21):
        # Parallel release pipelines write per-version folders; older runs used the flat release folder.
        path = ROOT / f'bin-build/releases/v{release}/v{major}/tool-usage-v{major}.json'
        full = read(path if path.exists() else ROOT / f'bin-build/releases/v{release}/tool-usage-v{major}.json')
        assert len(full) == 4 and all(r['checkedToolCount'] > 0 for r in full)
        selected = next(r for r in full if r['profile'] == 'full' and r['transport'] == 'stdio')
        assert selected['allSourceTextRetrievedAndHashChecked']
        records.append(selected)
    by_release = {}
    for record in records:
        key = record['releaseKey']
        assert not record['exampleCallsExecuted']
        assert record['registeredToolCount'] == record['checkedToolCount'] == matrix[key]['toolCount'], key
        assert {r['toolName'] for r in record['tools']} == set(matrix[key]['tools']), key
        by_release[key] = {k: v for k, v in record.items() if k not in ('profile', 'transport')}
    assert set(by_release) == set(matrix)
    union = set().union(*(set(r['tools']) for r in matrix.values()))
    assert union == set(catalog['tools']), 'Missing or obsolete usage mapping'
    result = {'basis': 'Actual MCP tools/list and GetToolUsage responses for every tool/operation on all eight releases. Parameterized calls, input origins, result contracts, language files, sequence calls and source hashes checked. Allowlisted in-memory call examples execute on every release; target-bound native example operations NOT executed.',
              'catalogSha256': hashlib.sha256(source.read_text('utf-8').replace('\r\n', '\n').encode()).hexdigest(),
              'uniqueTools': len(union), 'documents': len(catalog['documents']),
              'versionToolPairs': sum(r['toolCount'] for r in matrix.values()),
              'offlineCallExecutions': sum(len(r['offlineCallExamplesExecuted']) for r in by_release.values()),
              'indexedExampleBlocksOrMethods': sum(len(d['examples']) for d in catalog['documents']),
              'programmingExamples': len(catalog['examples']), 'callSequences': len(catalog['sequences']),
              'languages': [l['id'] for l in catalog['languages']],
              'relatedOfficialPatternMappings': sum(m['relationship'] == 'related-api-patterns' for m in catalog['tools'].values()),
              'noDirectVendoredExampleMappings': sum(m['relationship'] == 'no-direct-official-example' for m in catalog['tools'].values()),
              'sources': catalog['sources'], 'nativeTiaExecuted': False, 'releases': by_release}
    (ROOT / 'manifest/tool-usage-coverage.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f"PASS: {len(union)} unique tools across {len(by_release)} releases; all usage retrieved, all pinned reference text hash-checked; native calls NOT RUN")


if __name__ == '__main__':
    main()
