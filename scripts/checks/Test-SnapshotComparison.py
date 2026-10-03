"""Exercise release filtering through both snapshot comparison command lines."""
import json
from contextlib import contextmanager
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid


CHECKS = Path(__file__).resolve().parent


@contextmanager
def snapshot_directory():
    # Inherit the parent's ACL so restricted Windows child processes can read fixtures.
    parent = Path(tempfile.gettempdir()).resolve()
    directory = parent / ('snapshot-compare-' + uuid.uuid4().hex)
    directory.mkdir()
    try:
        yield directory
    finally:
        assert directory.resolve().parent == parent
        shutil.rmtree(directory)


class SnapshotComparisonTests(unittest.TestCase):
    def test_release_filters(self):
        for kind in ('Contracts', 'Responses'):
            with self.subTest(kind=kind), snapshot_directory() as directory:
                baseline = Path(directory) / 'baseline'
                current = Path(directory) / 'current'
                baseline.mkdir()
                current.mkdir()

                def snapshot(release, changed=False):
                    if kind == 'Contracts':
                        return dict(release=release, profile='full-engine', tools=[dict(
                            name='Example', descriptionSha256='unchanged', inputSchema=dict(
                                type='object', properties=dict(value=dict(type='integer' if changed else 'string'))))])
                    return dict(release=release, calls=[dict(profile='full', tool='Example',
                        arguments={}, response=dict(value='changed' if changed else 'original'))])

                def write(folder, release, changed=False):
                    (folder / (release + '.json')).write_text(
                        json.dumps(snapshot(release, changed)), encoding='utf-8')

                def compare(expected, *releases):
                    command = [sys.executable, str(CHECKS / ('Snapshot-Tool' + kind + '.py')),
                               'compare', '--baseline', str(baseline), '--current', str(current)]
                    if releases:
                        command += ['--releases', *releases]
                    result = subprocess.run(command, capture_output=True, text=True)
                    self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
                    return result.stdout

                for release in ('19', '20', '21'):
                    write(baseline, release)
                for release in ('20', '21'):
                    write(current, release)

                with self.subTest(case='strict partial capture fails'):
                    compare(1)
                with self.subTest(case='filtered partial capture passes'):
                    output = compare(0, '20', '21')
                    self.assertNotIn('V19:', output)
                with self.subTest(case='selected real change still fails'):
                    write(current, '20', changed=True)
                    compare(1, '20', '21')
                with self.subTest(case='unselected change is ignored'):
                    compare(0, '21')
                with self.subTest(case='selected missing current fails'):
                    compare(1, '19', '21')
                with self.subTest(case='selected absent from both fails'):
                    compare(1, '18')
                with self.subTest(case='invalid release rejected'):
                    compare(2, '15')
                with self.subTest(case='strict complete capture passes'):
                    write(current, '19')
                    write(current, '20')
                    compare(0)


if __name__ == '__main__':
    unittest.main()
