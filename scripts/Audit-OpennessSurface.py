"""Inventory supplied official XML API identifiers; lexical hints are NOT coverage proof.

Read-only for PublicAPI and product sources. Writes an audit folder, never loads TIA.
No Siemens documentation prose or DLL is redistributed.
"""
import argparse
import collections
import csv
import hashlib
import json
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
BOILER = {'Equals', 'GetHashCode', 'ToString', 'GetEnumerator', 'Any', 'Contains',
          'IndexOf', 'CopyTo', 'GetAttribute', 'GetAttributes', 'GetAttributeInfos',
          'SetAttribute', 'SetAttributes', 'GetComposition', 'GetCompositionInfos',
          'GetInvocationInfos', 'GetCreationInfos', 'GetService', 'GetServiceInfos',
          'Invoke', '#ctor', '#cctor'}


def scan(directory, version):
    rows, sources = [], []
    for file in sorted(directory.glob('*.xml')):
        doc = ET.parse(file)
        assembly = doc.findtext('./assembly/name') or file.stem
        members = doc.findall('./members/member')
        sources.append({'version': version, 'file': file.name, 'assembly': assembly,
                        'sha256': hashlib.sha256(file.read_bytes()).hexdigest(),
                        'counts': dict(collections.Counter(m.attrib['name'][0] for m in members))})
        for m in members:
            signature = m.attrib['name']
            kind, full = signature.split(':', 1)
            stem = full.split('(', 1)[0]
            owner, _, member = stem.rpartition('.') if kind != 'T' else (stem, '', '')
            simple = member.split('#')[-1]
            infrastructure = kind == 'M' and (simple in BOILER or member in BOILER or '#IEngineering' in member)
            rows.append({'version': version, 'assembly': assembly, 'kind': kind,
                         'signature': signature, 'owner': owner, 'member': member,
                         'infrastructureHeuristic': infrastructure})
    return rows, sources


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--v21', type=pathlib.Path, required=True)
    ap.add_argument('--v20', type=pathlib.Path, required=True)
    ap.add_argument('--out', type=pathlib.Path, required=True)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    rows21, sources21 = scan(args.v21, 'V21')
    rows20, sources20 = scan(args.v20, 'V20')
    assert rows21 and rows20
    symbols = collections.defaultdict(set)
    for f in (ROOT / 'tools/tiaportal-mcp/src/TiaMcpServer').rglob('*.cs'):
        if any(p.startswith(('obj', 'bin')) for p in f.relative_to(ROOT).parts):
            continue
        for token in set(re.findall(r'\b[A-Za-z_][A-Za-z_0-9]*\b', f.read_text(encoding='utf-8-sig'))):
            symbols[token].add(f.relative_to(ROOT).as_posix())
    ids20 = {r['signature'] for r in rows20}
    ids21 = {r['signature'] for r in rows21}
    for rows, other, name in [(rows21, ids20, 'V21'), (rows20, ids21, 'V20')]:
        with (args.out / (name + '-members.csv')).open('w', encoding='utf-8-sig', newline='') as f:
            fields = list(rows[0]) + ['sameSignatureInOtherSuppliedVersion', 'ownerTokenSourceHints', 'coverageVerdict']
            writer = csv.DictWriter(f, fieldnames=fields)
            writer.writeheader()
            for row in rows:
                row['sameSignatureInOtherSuppliedVersion'] = row['signature'] in other
                token = row['owner'].rsplit('.', 1)[-1].split('`')[0]
                row['ownerTokenSourceHints'] = ';'.join(sorted(symbols.get(token, [])))
                row['coverageVerdict'] = 'NOT_ASSESSED_PER_MEMBER; source hints are lexical only'
                writer.writerow(row)
    with (args.out / 'V21-domain-methods.txt').open('w', encoding='utf-8') as f:
        for r in rows21:
            if r['kind'] == 'M' and not r['infrastructureHeuristic']:
                f.write(r['assembly'] + '\t' + r['signature'] + '\n')
    roster = json.loads((ROOT / 'manifest/tools-list.json').read_text(encoding='utf-8-sig'))
    assert roster['toolCount'] == len(roster['tools'])
    toolrows = []
    for t in roster['tools']:
        toolrows.append({**t, 'sourceTokenHints': sorted(symbols.get(t['name'], []))})
    (args.out / 'mcp-tools.json').write_text(json.dumps(toolrows, ensure_ascii=False, indent=2), encoding='utf-8')
    result = {
        'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip(),
        'scope': 'All member identifiers in all supplied top-level XML files; not all installed Siemens products, dynamic attributes, or live capabilities.',
        'methodology': 'Exact XML identifier comparison across supplied versions. Lexical owner-token source hints include comments and strings and do NOT establish MCP reachability, complete implementation or semantic coverage.',
        'mcpToolCount': len(toolrows),
        'V21': {'xmlFiles': len(sources21), 'members': len(rows21), 'kinds': dict(collections.Counter(r['kind'] for r in rows21)), 'domainMethodCandidates': sum(r['kind'] == 'M' and not r['infrastructureHeuristic'] for r in rows21)},
        'V20': {'xmlFiles': len(sources20), 'members': len(rows20), 'kinds': dict(collections.Counter(r['kind'] for r in rows20))},
        'exactSignatureIntersection': len(ids20 & ids21),
        'V21NotInSuppliedV20Xml': len(ids21 - ids20),
        'V20NotInSuppliedV21Xml': len(ids20 - ids21),
        'versionComparisonCaveat': 'Not-in-XML is not proof that the API is unavailable; V20 supplied set has only two XML files whereas V21 includes optional products and AddIn assemblies.',
        'sources': sources21 + sources20,
    }
    (args.out / 'inventory-summary.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'sources'}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
