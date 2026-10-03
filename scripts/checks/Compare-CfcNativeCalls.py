"""Compatibility entry point for the generic native-call migration check."""
import runpy
from pathlib import Path


if __name__ == '__main__':
    runpy.run_path(str(Path(__file__).with_name('Compare-NativeCallOrder.py')), run_name='__main__')
