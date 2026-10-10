"""Validate all campaign inputs offline against a built engine's actual V4 schemas."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    for key in ('exe', 'public_api', 'host_harness', 'output'):
        setattr(args, key, getattr(args, key).resolve())
    args.output.mkdir(parents=True, exist_ok=False)
    steps = []
    for source in sorted((ROOT / 'scripts/diagnostics/campaign/plans').glob('plan_*.json')):
        copy = args.output / source.name
        copy.write_bytes(source.read_bytes())
        plan = json.loads(copy.read_text(encoding='utf-8'))
        for index, step in enumerate(plan):
            # These exact fixtures retain the unsupported-action and wrong-type
            # rejection coverage; native failure expectations are not exemptions.
            negative = step.get('expect') == 'error' and (
                'V4 rejects this unadvertised action' in step.get('note', '') or
                step.get('note', '').startswith('V4 schema rejection:'))
            steps.append(dict(step, label=f'{source.name}:{index + 1}', negativeInput=negative))
    with resources.server(args.exe, args.public_api, args.major, 'stdio', 'full',
                          args.host_harness, args.public_api,
                          env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '2000000'}) as (rpc, _, _):
        tools = rpc('tools/list', 'catalog')['result']['tools']
    catalog, inputs = args.output / 'tools.json', args.output / 'inputs.json'
    catalog.write_text(json.dumps(tools), encoding='utf-8')
    inputs.write_text(json.dumps(steps), encoding='utf-8')
    subprocess.run([str(args.host_harness), str(args.exe), 'script-inputs-only', str(args.public_api),
                    str(catalog), str(inputs)], check=True)


if __name__ == '__main__':
    main()
