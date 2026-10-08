"""Prove P7-04 Siemens IL and call order against an SDK build of its exact HEAD."""
import argparse
from collections import Counter
import importlib.util
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
VERSIONS = {'14sp1': 'V14Sp1', '15.1': 'V15_1', **{str(v): f'V{v}' for v in range(16, 22)}}


def read(path):
    return json.loads(path.read_text('utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-root', type=Path, required=True)
    parser.add_argument('--reader', type=Path, required=True, help='Metadata-only Cecil reader (report includes its source)')
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    evidence.relative_to(ROOT)
    evidence.mkdir(parents=True, exist_ok=True)
    empty = evidence / 'empty-inventory.json'
    empty.write_text('{"sites":[]}\n', encoding='utf-8')
    spec = importlib.util.spec_from_file_location('order', Path(__file__).with_name('Compare-NativeCallOrder.py'))
    order = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(order)
    rows = []

    def il(label, before, after, before_sites, after_sites, scope, allowed):
        captures = []
        for phase, assembly, sites in (('before', before, before_sites), ('after', after, after_sites)):
            target = evidence / f'{label}-{phase}.json'
            subprocess.run([str(args.reader.resolve()), str(assembly), str(sites), str(target)], check=True)
            selected = [m for m in read(target)['methods'] if scope(m['owner'])]
            grouped = {}
            for method in selected:
                grouped.setdefault(method['name'], []).append(method['semantic'])
            captures.append({name: sorted(values) for name, values in grouped.items()})
        before, after = captures
        missing = before.keys() - after.keys()
        added_closures = {name for name in before.keys() & after.keys() if '<>c__DisplayClass#' in name and '::.ctor()' in name
                          and set(before[name]) == set(after[name]) and len(after[name]) > len(before[name])}
        changed = {name for name in before.keys() & after.keys() if before[name] != after[name] and name not in added_closures}
        assert not missing, (label, 'missing methods', sorted(missing))
        assert changed == allowed, (label, 'unexpected IL changes', sorted(changed ^ allowed))
        row = {'assembly': label, 'unchangedMethods': sum(len(before[n]) for n in before.keys() - changed),
               'changedMethods': sorted(changed), 'addedMethods': sorted(after.keys() - before.keys()),
               'addedIdenticalClosureConstructors': sorted(added_closures)}
        rows.append(row)
        print(f'PASS {label}: {row["unchangedMethods"]} unchanged IL bodies; {len(changed)} listed seam/guard changes', flush=True)

    for version in ('20', '21'):
        output, intermediate = ('bin-v20', 'obj-v20') if version == '20' else ('bin', 'obj')
        relative = Path('src/Engine')
        before_sites = args.baseline_root / relative / intermediate / 'Release/net48/native-call-coverage.json'
        after_sites = ROOT / relative / intermediate / 'Release/net48/native-call-coverage.json'
        old, new = read(before_sites)['sites'], read(after_sites)['sites']
        seam = [s for s in new if '<AdoptFoundationSession>' in s['caller']]
        assert len(seam) == 1 and seam[0]['member'] == 'System.String Siemens.Engineering.ProjectBase::get_Name()' and seam[0]['opcode'] == 'callvirt', seam
        kept = [s for s in new if s not in seam]
        errors, _, old_count, new_count = order.compare(old, kept)
        assert not errors and old_count == new_count, errors
        old_sequences, new_sequences = order.sequences(old), order.sequences(kept)
        assert old_sequences == new_sequences, 'Existing native method-family call order changed'
        assert Counter(s['member'] for s in new if s['category'] == 'direct') == Counter(s['member'] for s in old if s['category'] == 'direct') + Counter([seam[0]['member']])
        filtered = evidence / f'engine-{version}-without-adoption-getter.json'
        filtered.write_text(json.dumps({'sites': kept}) + '\n', encoding='utf-8')
        print(f'PASS V{version} native order: {old_count} existing Siemens sites unchanged; one project-name getter added', flush=True)
        il(f'engine-{version}', args.baseline_root / relative / output / f'Release/net48/TiaMcp.Engine.V{version}.exe',
           ROOT / relative / output / f'Release/net48/TiaMcp.Engine.V{version}.exe', before_sites, after_sites,
           lambda owner: owner.startswith('TiaMcpServer.Siemens.'), {'System.Void TiaMcpServer.Siemens.Portal::Dispose()'})
    for version, directory in VERSIONS.items():
        relative = Path('src/Adapters') / directory / 'bin' / version / f'Release/net48/TiaMcp.Adapter.{version}.dll'
        inventory = Path('src/Adapters') / directory / 'obj' / version / 'Release/net48/native-call-coverage.json'
        def adapter_sequences(sites):
            sequences = {}
            for site in sites:
                if '::BorrowSharedSession(' in site['caller']:
                    continue
                caller = re.sub(r'<>c__DisplayClass\d+_', '<>c__DisplayClass#_', site['caller'])
                caller = re.sub(r'b__\d+_(\d+)', r'b__#_\1', caller)
                caller = re.sub(r'\|\d+_(\d+)', r'|#_\1', caller)
                caller = re.sub(r'd__\d+', 'd__#', caller)
                sequences.setdefault(caller, []).append((site['offset'], site['opcode'], site['member'], site['category']))
            return {caller: [s[1:] for s in sorted(sites)] for caller, sites in sequences.items()}
        assert adapter_sequences(read(args.baseline_root / inventory)['sites']) == adapter_sequences(read(ROOT / inventory)['sites']), (version, 'adapter native call order')
        il(f'adapter-{version}', args.baseline_root / relative, ROOT / relative, args.baseline_root / inventory, ROOT / inventory,
           lambda owner: owner.startswith('TiaMcp.Adapters.Native.') or owner.startswith('TiaMcp.Adapters.'),
           {'System.Void TiaMcp.Adapters.PlcFoundationEngine::.ctor(System.String,System.String)',
            'System.Void TiaMcp.Adapters.PlcFoundationEngine::Check(System.Boolean)'})
    (evidence / 'proof.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
