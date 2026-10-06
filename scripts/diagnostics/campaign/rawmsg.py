# -*- coding: utf-8 -*-
"""rawmsg.py <Tool> '<json>': display a V4 tool's data/error and metadata.

Before any write campaign, keep the Workbench open to approve each call, or switch approvals off in the MCP menu. This client does not bypass approval.
"""
import importlib.util
import json
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('camp', Path(__file__).with_name('camp.py'))
camp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(camp)


def main():
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    camp.print_approval_notice()
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    value, _ = camp.call_raw(sys.argv[1], args)
    print('MESSAGE:', json.dumps(value['error'] or value['data'], ensure_ascii=False)[:3000])
    print('META:', json.dumps(value['meta'], ensure_ascii=False)[:3000])


if __name__ == '__main__':
    main()
