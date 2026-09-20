# -*- coding: utf-8 -*-
"""rawmsg.py <Tool> '<json>'  -> print the decoded message/error of a CallTool result (unicode-safe)."""
import sys, json, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.argv = [sys.argv[0], "call"] + sys.argv[1:]
import importlib.util
spec = importlib.util.spec_from_file_location("camp", os.path.join(HERE, "camp.py"))
camp = importlib.util.module_from_spec(spec)
# camp.py runs its CLI on import when argv has a verb; avoid that by loading only the helpers
src = io.open(os.path.join(HERE, "camp.py"), encoding="utf-8").read()
cut = src.find("\nif __name__")
if cut < 0: cut = src.find("\ndef main")
ns = {"__file__": os.path.join(HERE, "camp.py"), "__name__": "camp_lib"}
exec(compile(src[:cut] if cut > 0 else src, "camp.py", "exec"), ns)
name = sys.argv[2]; args = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
d, txt = ns["call_raw"](name, args)
msg = d.get("message"); meta = d.get("meta")
print("MESSAGE:", json.dumps(msg, ensure_ascii=False)[:3000])
print("META:", json.dumps(meta, ensure_ascii=False)[:3000])
