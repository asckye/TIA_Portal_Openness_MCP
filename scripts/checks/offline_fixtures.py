"""Owned offline fixtures with inherited worktree permissions for child processes."""
from contextlib import contextmanager
from pathlib import Path
import os
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


def verify_owned_process_image(process):
    """Validate the exact executable of a still-live child before forced cleanup."""
    if process.poll() is not None:
        return
    import ctypes
    from ctypes import wintypes
    if os.name != 'nt':
        return
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, process.pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wintypes.DWORD(32768)
        image = ctypes.create_unicode_buffer(size.value)
        if not kernel.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        expected = Path(process.args[0]).resolve()
        if os.path.normcase(str(expected)) != os.path.normcase(str(Path(image.value).resolve())):
            raise RuntimeError('Refusing cleanup of a child whose executable identity changed: ' + str(process.pid))
    finally:
        kernel.CloseHandle(handle)
