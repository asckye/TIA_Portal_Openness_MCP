"""Run pinned upstream offline unit tests with source precedence and UTF-8.

Usage: TiaMcp_Output/ecosystem-python/Scripts/python.exe -X utf8 scripts/checks/Test-Ecosystem.py
No live endpoint or TIA connection is configured by this runner.
"""
from pathlib import Path
import os
import sys

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tools/third-party/siemens-plc-tools"


def main():
    if not sys.flags.utf8_mode:
        raise SystemExit("Run Python with -X utf8 (required for upstream Windows documentation tests).")
    sys.path[:0] = [str(SOURCE / "src")] + [str(p / "src") for p in sorted((SOURCE / "packages").iterdir()) if (p / "src").is_dir()]
    import pytest
    os.chdir(SOURCE)
    return pytest.main(["packages/plc-code/tests", "packages/plc-iol/tests", "packages/plc-trace/tests", "--import-mode=importlib", "-o", "addopts=", "-o", "cache_dir=" + str(ROOT / "TiaMcp_Output/pytest-cache"), "--disable-warnings", "-q", "-rs", "--basetemp", str(ROOT / "TiaMcp_Output/pytest-ecosystem")] + sys.argv[1:])


if __name__ == "__main__":
    raise SystemExit(main())
