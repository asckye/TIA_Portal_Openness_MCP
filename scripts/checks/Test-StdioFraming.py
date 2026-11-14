"""Overlap real host replies and session notifications without connecting to TIA."""
import argparse
import importlib.util
import json
from pathlib import Path

from offline_fixtures import fixture_directory

spec = importlib.util.spec_from_file_location('framing_resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True, type=Path)
    parser.add_argument('--release', default='19', choices=('14sp1', '15.1', '16', '17', '18', '19', '20', '21'))
    parser.add_argument('--iterations', type=int, default=10000)
    parser.add_argument('--batch-size', type=int, default=16)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.iterations < 1 or args.batch_size < 1:
        parser.error('iterations and batch-size must be positive')
    args.output.mkdir(parents=True, exist_ok=True)
    report = dict(release=args.release, requestedIterations=args.iterations, completedIterations=0,
                  replies=0, sessionNotifications=0, nativeTiaExecuted=False, passed=False)
    try:
        with fixture_directory('stdio-framing-') as scratch:
            owned = []
            with resources.server(args.exe, None, args.release, 'stdio', 'full',
                                  env_overrides={'TIA_MCP_DATA_DIRECTORY': scratch, 'TiaPortalLocation': ''},
                                  evidence_directory=args.output, process_observer=owned.append) as (rpc, _, _):
                initialized = rpc('initialize', 0, {'protocolVersion': '2025-03-26', 'capabilities': {},
                                                   'clientInfo': {'name': 'stdio-framing-stress', 'version': '1'}})
                resources.require('result' in initialized, 'Host initialization failed')
                rpc('notifications/initialized', notification=True)
                for start in range(0, args.iterations, args.batch_size):
                    count = min(args.batch_size, args.iterations - start)
                    requests = []
                    for index in range(start, start + count):
                        requests.extend([
                            {'jsonrpc': '2.0', 'id': index * 2 + 1, 'method': 'tools/call',
                             'params': {'name': 'GetToolUsage', 'arguments': {'__invalid': True},
                                        '_meta': {'progressToken': f'framing-{index}'}}},
                            {'jsonrpc': '2.0', 'id': index * 2 + 2, 'method': 'ping', 'params': {}}
                        ])
                    replies, notifications = rpc.batch(requests)
                    for request in requests:
                        reply = replies[request['id']]
                        resources.require('error' not in reply, 'Concurrent request failed: ' + str(reply))
                        if request['method'] == 'tools/call':
                            result = reply['result']
                            body = json.loads(result['content'][0]['text'])
                            resources.require(result['isError'] and body == result['structuredContent'],
                                              'Invalid arguments must retain the V4 rejection envelope')
                            resources.require(body['error']['code'] == 'INVALID_ARGUMENT' and
                                              body['meta']['execution'] == 'not-started',
                                              'Stress request reached an operation')
                        else:
                            resources.require(reply['result'] == {}, 'Ping response changed')
                    report['completedIterations'] += count
                    report['replies'] += len(replies)
                    report['sessionNotifications'] += sum(
                        message.get('method') == 'notifications/tiamcp/sessionState' for message in notifications)
                resources.require(report['sessionNotifications'] > 0, 'Stress must overlap real session notifications and replies')
            resources.require(owned[0].returncode == 0, 'Stress host must exit normally after STDIN closes')
            report['passed'] = True
    finally:
        (args.output / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(report))


if __name__ == '__main__':
    main()
