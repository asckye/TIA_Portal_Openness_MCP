"""Compatibility entry point for the CFC domain dispatch/response checks."""
import runpy
import sys
from pathlib import Path


if __name__ == '__main__':
    sys.argv[1:1] = ['--domain', 'Cfc']
    runpy.run_path(str(Path(__file__).with_name('Test-DomainTools.py')), run_name='__main__')
