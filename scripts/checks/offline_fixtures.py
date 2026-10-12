"""Owned offline fixtures with inherited worktree permissions for child processes."""
from contextlib import contextmanager
from pathlib import Path
import shutil
import time
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
        # A worker started by a stopped host can still hold fixture files for a moment while it exits; wait a bounded
        # time for the handles to close, then fail loudly if they stay open.
        deadline = time.monotonic() + 30
        while True:
            try:
                shutil.rmtree(directory)
                break
            except PermissionError:
                if time.monotonic() >= deadline: raise
                time.sleep(0.5)
