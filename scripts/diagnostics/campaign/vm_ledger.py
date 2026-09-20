# -*- coding: utf-8 -*-
"""Which of the 447 tools were ever invoked on the VM (from every Claude Code session transcript of this project ON THE ORIGINAL DEV BOX).
Machine-specific by nature; its output vm_ledger.json is checked in and is what make_ledger.py reads (status "早期真机")."""
import json, re, glob, os, io, collections
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
tools = json.load(io.open(os.path.join(ROOT, "manifest/tools-list.json"), encoding="utf-8-sig"))
names = sorted({t["name"] for t in (tools["tools"] if isinstance(tools, dict) and "tools" in tools else tools)})
by_lower = {n.lower(): n for n in names}
hits = collections.defaultdict(set)   # tool -> set(session)
# command forms: python ".../vmrun.py" Tool '{...}'  |  Probe-McpServer.py call Tool  |  $V Tool (variable prefix) -> caught by the output form
pat_cmd = re.compile(r'(?:Probe-McpServer\.py\\?"?\s+(?:call|bridge)|vmrun\.py\\?"?|mcp_probe\.py\\?"?\s+(?:call|bridge)?)\s+[\'"]?([A-Za-z][A-Za-z0-9]+)')
pat_out = re.compile(r'== ([A-Za-z][A-Za-z0-9]+) ::')                       # vmrun.py summary line
pat_tc = re.compile(r'tools/call.{0,60}?\\?"name\\?":\s*\\?"([A-Za-z][A-Za-z0-9]+)\\?"')
pat_mcp = re.compile(r'mcp__tia-portal-vm__([A-Za-z0-9]+)')
pat_bridge = re.compile(r'\\?"name\\?"\s*:\s*\\?"([A-Za-z][A-Za-z0-9]+)\\?"\s*,\s*\\?"argumentsJson\\?"')
pat_calltool = re.compile(r'CallTool\s+\'\{\\?"name\\?":\s*\\?"([A-Za-z0-9]+)')
pat_probe_hdr = re.compile(r'^== ([A-Za-z][A-Za-z0-9]+) (?:::|\[)')
for f in glob.glob(r"C:\Users\asckye\.claude\projects\D--Code-TIA-Portal-Openness-MCP\*.jsonl"):
    sid = os.path.basename(f)[:8]
    with io.open(f, encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            for pat in (pat_cmd, pat_mcp, pat_bridge, pat_calltool, pat_out, pat_tc):
                for m in pat.finditer(line):
                    n = by_lower.get(m.group(1).lower())
                    if n: hits[n].add(sid)
never = [n for n in names if n not in hits]
print("tools", len(names), "invoked on VM at least once", len(hits), "never", len(never))
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vm_ledger.json")
io.open(out, "w", encoding="utf-8").write(json.dumps({"invoked": {k: sorted(v) for k, v in sorted(hits.items())}, "never": never}, ensure_ascii=False, indent=1))
print(" ".join(sorted(hits)))
