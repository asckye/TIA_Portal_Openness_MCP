"""Owned offline fixtures with inherited worktree permissions for child processes."""
from contextlib import contextmanager
from pathlib import Path
import shutil
import uuid


@contextmanager
def fixture_directory(prefix='offline-fixture-'):
    parent = (Path(__file__).resolve().parents[2] / 'bin-build').resolve()
    parent.mkdir(exist_ok=True)
    directory = parent / (prefix + uuid.uuid4().hex)
    directory.mkdir()
    try:
        yield str(directory)
    finally:
        if directory.resolve().parent != parent: raise ValueError('Unsafe offline fixture cleanup path')
        shutil.rmtree(directory)
