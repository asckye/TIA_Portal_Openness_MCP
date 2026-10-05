"""Phase 6 migration groups for the snapshot proofs, derived from the plan generator.

A group is the set of 3.x tool names whose source file appendix G assigns to a task; the Foundation releases belong to P6-08 as a whole.
"""
import runpy
from collections import defaultdict
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FOUNDATION_RELEASES = ('14sp1', '15.1', '16', '17', '18', '19')
TASKS = tuple(f'P6-{i:02}' for i in range(7, 25))


@lru_cache(maxsize=1)
def _generator():
    return runpy.run_path(str(ROOT / 'scripts/generate/Generate-Phase6Plan.py'), run_name='phase6_groups')


def renames():
    """3.x name -> V4 name for every current name (unchanged names map to themselves)."""
    return dict(_generator()['renames'])


def group(task, release, baseline_names):
    """3.x names of `release` that `task` migrates."""
    if task not in TASKS:
        raise ValueError('Unknown migration task: ' + task)
    if release in FOUNDATION_RELEASES:
        return set(baseline_names) if task == 'P6-08' else set()
    if task == 'P6-08':
        return set()
    g = _generator()
    members = defaultdict(set)
    for name, (path, _method) in g['source_tools'].items():
        members[g['owners'][path]].add(name)
    return members[task] & set(baseline_names)


def mapped(name, members):
    return renames().get(name, name) if name in members else name
