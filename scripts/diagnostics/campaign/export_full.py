# -*- coding: utf-8 -*-
"""export_full.py <exportId> <outfile>: assemble V4 GetExportContent pages."""
import importlib.util
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('camp', Path(__file__).with_name('camp.py'))
camp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(camp)


def assemble(export_id, call):
    offset, parts = 0, []
    while True:
        value, _ = call('GetExportContent', {'exportId': export_id, 'offset': offset, 'length': 20000})
        camp.successful(value)
        parts.append(value['data']['text'])
        following = value['meta']['paging']['nextOffset']
        if following is None: return ''.join(parts)
        if following <= offset: raise ValueError('Export paging did not advance')
        offset = following


def main():
    text = assemble(sys.argv[1], camp.call_raw)
    Path(sys.argv[2]).write_text(text, encoding='utf-8')
    print(sys.argv[2], len(text))


if __name__ == '__main__':
    main()
