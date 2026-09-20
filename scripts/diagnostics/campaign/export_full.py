# -*- coding: utf-8 -*-
"""export_full.py <exportId> <outfile>: page through GetExport and write the assembled payload text."""
import sys, json, io, os
sys.argv, saved = [sys.argv[0]], sys.argv
import importlib.util
spec = importlib.util.spec_from_file_location("camp", os.path.join(os.path.dirname(os.path.abspath(__file__)), "camp.py"))
camp = importlib.util.module_from_spec(spec); spec.loader.exec_module(camp)
sys.argv = saved
export_id, out = sys.argv[1], sys.argv[2]
offset, parts = 0, []
while True:
    d, txt = camp.call_raw("GetExport", {"exportId": export_id, "offset": offset, "length": 20000})
    j = json.loads(txt)
    parts.append(j["message"]); meta = j.get("meta", {})
    if meta.get("eof") or meta.get("nextOffset") is None: break
    offset = meta["nextOffset"]
io.open(out, "w", encoding="utf-8").write("".join(parts)); print(out, sum(len(p) for p in parts))
