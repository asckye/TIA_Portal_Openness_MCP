"""Per-call latency medians, evidence only (maintainer decision 2026-11-03)."""
import json
import math
from pathlib import Path
import statistics
import time


class Samples:
    def __init__(self, job, profile='full'):
        self.job, self.profile, self.values = job, profile, {}

    def record(self, release, transport, seconds):
        if math.isfinite(seconds) and seconds >= 0:
            self.values.setdefault(f'{self.job}/{release}/{transport}/{self.profile}', []).append(seconds * 1000)

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

    def finish(self, output):
        evidence = dict(schemaVersion=1, producer=self.job, profile=self.profile, gated=False, errors=[], samples={
            key: dict(calls=len(values), medianMilliseconds=statistics.median(values)) for key, values in sorted(self.values.items())})
        output = Path(output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8', newline='\n')
        return evidence
