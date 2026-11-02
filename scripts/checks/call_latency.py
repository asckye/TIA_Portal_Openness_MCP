"""Per-call latency evidence and the reviewed master-baseline gate (milliseconds)."""
import json
import math
from pathlib import Path
import statistics
import time
import uuid

BASELINE = Path(__file__).with_name('call-latency-baseline.json')
RELEASES = ('14sp1', '15.1', '16', '17', '18', '19', '20', '21')


class Samples:
    def __init__(self, job, profile='full'):
        self.job, self.profile, self.values = job, profile, {}

    def record(self, release, transport, seconds):
        if not math.isfinite(seconds) or seconds < 0:
            raise ValueError('Invalid call latency')
        key = f'{self.job}/{release}/{transport}/{self.profile}'
        self.values.setdefault(key, []).append(seconds * 1000)

    def instrument(self, client, release, transport):
        if getattr(client, '_latency_instrumented', False):
            return client
        original = client.call

        def call(method, params):
            started = time.perf_counter()
            result = original(method, params)
            if method == 'tools/call':
                self.record(release, transport, time.perf_counter() - started)
            return result

        client.call, client._latency_instrumented = call, True
        return client

    def finish(self, output, baseline=BASELINE, record_revision=None):
        rows = {key: {'calls': len(values), 'medianMilliseconds': statistics.median(values)}
                for key, values in sorted(self.values.items())}
        if not rows:
            raise ValueError('Missing per-call latency samples')
        output = Path(output)
        output.parent.mkdir(parents=True, exist_ok=True)
        evidence = {'schemaVersion': 1, 'producer': self.job, 'profile': self.profile, 'samples': rows}
        errors = []
        if record_revision is not None:
            if len(record_revision) != 40 or any(c not in '0123456789abcdef' for c in record_revision):
                raise ValueError('Baseline producer requires the full master revision')
            previous = json.loads(Path(baseline).read_text('utf-8')) if Path(baseline).exists() else {
                'schemaVersion': 1, 'absoluteMarginMilliseconds': 2.0, 'relativeMargin': 0.25, 'maximumMarginMilliseconds': 4.0, 'samples': {}}
            previous.setdefault('maximumMarginMilliseconds', 4.0)
            for key, row in rows.items():
                previous['samples'][key] = dict(row, masterRevision=record_revision, producer=self.job)
            Path(baseline).write_text(json.dumps(previous, indent=2) + '\n', encoding='utf-8', newline='\n')
            evidence['baselineRevision'] = record_revision
        else:
            previous = json.loads(Path(baseline).read_text('utf-8'))
            if previous.get('schemaVersion') != 1:
                raise ValueError('Unsupported call latency baseline')
            absolute, relative = previous['absoluteMarginMilliseconds'], previous['relativeMargin']
            maximum = previous['maximumMarginMilliseconds']
            if not all(math.isfinite(value) and value >= 0 for value in (absolute, relative, maximum)):
                raise ValueError('Invalid call latency margin')
            for key, row in rows.items():
                expected = previous['samples'].get(key)
                if expected is None:
                    errors.append('Missing master call latency baseline: ' + key)
                    continue
                median = expected['medianMilliseconds']
                if not math.isfinite(median) or median < 0 or expected['calls'] <= 0 or len(expected['masterRevision']) != 40:
                    raise ValueError('Invalid master call latency baseline: ' + key)
                limit = median + min(maximum, max(absolute, median * relative))
                row['masterMedianMilliseconds'], row['limitMilliseconds'] = median, limit
                if row['medianMilliseconds'] > limit:
                    errors.append(f'{key}: median {row["medianMilliseconds"]:.3f} ms exceeds master gate {limit:.3f} ms')
        evidence['errors'] = errors
        output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8', newline='\n')
        if errors:
            raise ValueError('\n'.join(errors))
        return evidence


def promote(candidate, revision, destination=BASELINE):
    """Publish a complete formal-producer capture, preserving its measured bytes."""
    content = Path(candidate).read_bytes()
    data = json.loads(content)
    expected = {f'transport/{release}/{transport}/{profile}' for release in RELEASES
                for transport in ('stdio', 'http') for profile in ('merge', 'full')}
    expected |= {f'responses/{release}/stdio/{profile}' for release in RELEASES for profile in ('merge', 'full')}
    expected |= {f'responses/{release}/http/{profile}' for release in ('20', '21') for profile in ('merge', 'full')}
    if data.get('schemaVersion') != 1 or set(data.get('samples', {})) != expected:
        raise ValueError('Latency baseline promotion requires all 52 release/transport/profile samples')
    if len(revision) != 40 or any(c not in '0123456789abcdef' for c in revision):
        raise ValueError('Latency baseline promotion requires the full master revision')
    for key, row in data['samples'].items():
        if row['masterRevision'] != revision or row['producer'] != key.split('/')[0] or row['calls'] <= 0 or \
                not math.isfinite(row['medianMilliseconds']) or row['medianMilliseconds'] < 0:
            raise ValueError('Invalid formal producer sample: ' + key)
    for name in ('absoluteMarginMilliseconds', 'relativeMargin', 'maximumMarginMilliseconds'):
        if not math.isfinite(data[name]) or data[name] < 0:
            raise ValueError('Invalid latency baseline margin: ' + name)
    destination = Path(destination)
    temporary = destination.with_name(destination.name + '.' + uuid.uuid4().hex + '.tmp')
    temporary.write_bytes(content)
    temporary.replace(destination)
    return len(expected)


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--promote', required=True, type=Path)
    parser.add_argument('--master-revision', required=True)
    args = parser.parse_args()
    print('Promoted', promote(args.promote, args.master_revision), 'formal producer latency samples to', BASELINE)
