"""Phase 6 migration groups for the snapshot proofs, derived from the plan generator.

A group is the set of 3.x tool names whose source file appendix G assigns to a task; the Foundation releases belong to P6-08 as a whole.
"""
import json
import re
import runpy
from collections import defaultdict
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FOUNDATION_RELEASES = ('14sp1', '15.1', '16', '17', '18', '19')
TASKS = tuple(f'P6-{i:02}' for i in range(7, 25)) + ('P6-67', 'P6-68', 'P6-69')
P6_67_ADDITIONS = {'StageImportFiles', 'ListStagedImportFiles', 'CleanupStagedImportFiles'}
P6_68_NAMES = {'StageImportFiles', 'ListStagedImportFiles', 'CleanupStagedImportFiles', 'ImportPlcBlock', 'ImportPlcType', 'ImportPlcTagTable', 'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory', 'CompilePlcSoftware', 'CompilePlcDiagnostics', 'CompileDevice', 'CompileHmiDiagnostics', 'ExportTechnologyObject', 'ExportPlcWatchTable'}
P6_67_NAMES = P6_67_ADDITIONS | {'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory', 'ImportPlcBlock', 'ImportPlcType', 'ImportPlcTagTable'}
P6_69_NAMES = P6_67_ADDITIONS | {'OpenProject', 'CloseProject', 'ImportPlcBlocksFromDirectory', 'ImportPlcProgramFromDirectory'}

def additions(task):
    return P6_67_ADDITIONS if task == 'P6-67' else set()


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
    if task == 'P6-69':
        return P6_69_NAMES & set(baseline_names)
    if task == 'P6-68':
        return P6_68_NAMES & set(baseline_names)
    if task == 'P6-67':
        return P6_67_NAMES & set(baseline_names)
    if release in FOUNDATION_RELEASES:
        return set(baseline_names) if task == 'P6-08' else set()
    if task == 'P6-08':
        return set()
    g = _generator()
    members = defaultdict(set)
    for name, (path, _method) in g['source_tools'].items():
        members[g['owners'][path]].add(name)
    return members[task] & set(baseline_names)


def names(task):
    """Every 3.x and V4 name `task` migrates in any release; usage-catalog responses about them belong to the group."""
    if task not in TASKS:
        raise ValueError('Unknown migration task: ' + task)
    if task == 'P6-69':
        return P6_69_NAMES
    if task == 'P6-68':
        return P6_68_NAMES
    if task == 'P6-67':
        return P6_67_NAMES
    if task == 'P6-08':
        old = set()
        for release in FOUNDATION_RELEASES:
            path = ROOT / 'manifest/history/contracts-v3/baseline' / (release + '.json')
            old |= {tool['name'] for tool in json.loads(path.read_text('utf-8-sig'))['tools']}
    else:
        g = _generator()
        old = {name for name, (path, _method) in g['source_tools'].items() if g['owners'][path] == task}
    return old | {renames().get(name, name) for name in old}


def rename_map(task):
    """3.x -> V4 for the names `task` renames (unchanged names omitted)."""
    table = renames()
    return {old: table[old] for old in names(task) if old in table and table[old] != old}


def renamed(value, subs):
    """`value` with every whole-word 3.x name of `subs` replaced, in every string (Check-DeadToolReferences --fix semantics)."""
    if not subs:
        return value
    pattern = re.compile(r'(?<![.\w])(?:' + '|'.join(re.escape(n) for n in sorted(subs, key=len, reverse=True)) + r')\b')

    def walk(item):
        if isinstance(item, str):
            return pattern.sub(lambda m: subs[m[0]], item)
        if isinstance(item, list):
            return [walk(x) for x in item]
        if isinstance(item, dict):
            return {walk(k) if isinstance(k, str) else k: walk(v) for k, v in item.items()}
        return item
    return walk(value)


def mapped(name, members):
    return renames().get(name, name) if name in members else name
