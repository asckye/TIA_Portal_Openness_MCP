# -*- coding: utf-8 -*-
"""Real-machine campaign runner (scripts/diagnostics/campaign). Talks to the VM engine through scripts/diagnostics/Probe-McpServer.py
(the tia-portal-vm entry of ~/.claude.json: url + Authorization header), so it works from any machine that has that entry.
Before any write campaign, keep the Workbench open to approve each call, or switch approvals off in the MCP menu. This client does not bypass approval.
  python plans/plan_x.py                            regenerate plans/plan_x.json from its .py
  python camp.py run plans/plan_x.json [start]      run it; rows go to ledger/plan_x.jsonl; stops when TIA dies
  python make_ledger.py                             regenerate docs/reference/real-machine-ledger.md (ledger-runs.jsonl.gz + ledger/*.jsonl + overrides)

  camp.py call <Tool> '<json>' [metaKey,...]      one call, full result (trimmed)
  camp.py run <plan.json> [startIndex]            run a plan, append to ledger/<plan>.jsonl, stop when TIA dies
  camp.py raw <Tool> '<json>'                     print the raw text of the tool result (first 6000 chars)
"""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from mcp_results import envelope, successful

import sys, json, importlib.util, os, time, io
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))   # scripts/diagnostics/campaign -> repo root
spec = importlib.util.spec_from_file_location("probe", os.path.join(REPO, "scripts/diagnostics/Probe-McpServer.py"))
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
p = None
LITE = set()
APPROVAL_NOTICE = "Before any write campaign, keep the Workbench open to approve each call, or switch approvals off in the MCP menu. This client does not bypass approval."

def print_approval_notice():
    print(APPROVAL_NOTICE, file=sys.stderr, flush=True)

def connect():
    global p, LITE
    if p is None:
        e = m.find_claude_entry()
        if not e: raise RuntimeError('No tia-portal-vm connection configured')
        p = m.Probe(e['url'], e['headers']['Authorization'], 900)
        p.initialize()
        LITE = {t['name'] for t in p.rpc('tools/list', {})['result']['tools']}

def call_raw(name, args):
    connect()
    request = {'name': name, 'arguments': args} if name in LITE else {
        'name': 'CallTool', 'arguments': {'name': name, 'arguments': args}}
    r = p.rpc('tools/call', request)
    d = envelope(r)
    return d, json.dumps(d, ensure_ascii=False)


def result_fields(d):
    data = d.get('data') or {}
    fields = dict(data.get('evidence') or {})
    fields.update(data)
    fields.update(d.get('meta') or {})
    if data.get('export'): fields['exportId'] = data['export']['id']
    return fields

def alive():
    d, _ = call_raw("GetSessionState", {})
    try:
        data = d["data"]; h = data["evidence"]["hmiReadHealth"]; pp = h["portalProcess"]
        return {"pid": pp.get("boundProcessId"), "alive": pp.get("processAlive"), "blocked": h.get("snapshotReadsBlocked"), "connected": data.get("isConnected"), "project": data.get("project")}
    except Exception:
        return {"raw": json.dumps(d, ensure_ascii=False)[:200]}

def classify(d):
    if 'rpcError' in d: return False, 'rpc:' + str(d['rpcError'])[:300]
    value = envelope(d)
    error = value['error']
    return value['ok'], (error['code'] + ': ' + error['message'] if error else
                          str((value['data'] or {}).get('summary') or ''))

def step_verdict(d, expect):
    value = envelope(d)
    meta = value['meta']
    if meta.get('requiresSessionReset') or meta.get('outcome') == 'unknown': return 'UNCONFIRMED'
    return 'PASS' if (value['ok'] and expect in ('ok', 'any')) or (not value['ok'] and expect in ('error', 'any')) else 'FAIL'

def trim(v, n=900):
    s = json.dumps(v, ensure_ascii=False, default=str)
    return s if len(s) <= n else s[:n] + "...(" + str(len(s)) + ")"

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode in ("call", "raw", "full"):
        print_approval_notice()
        tool = sys.argv[2]; args = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
        d, txt = call_raw(tool, args)
        if mode == "raw": print(txt[:6000]); sys.exit()
        if mode == "full": print(txt); sys.exit()
        ok, text = classify(d)
        print("==", tool, "ok" if ok else "FAIL", "::", text[:400])
        keys = sys.argv[4].split(",") if len(sys.argv) > 4 else []
        meta = result_fields(d)
        for k in keys:
            if k == "*": print("    meta =", trim(meta, 4000))
            elif k in meta: print("   ", k, "=", trim(meta[k], 1500))
        print("   ", alive())
    elif mode == "run":
        print_approval_notice()
        plan_path = sys.argv[2]; start = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        plan = json.load(io.open(plan_path, encoding="utf-8"))
        os.makedirs(os.path.join(HERE, "ledger"), exist_ok=True)
        led = io.open(os.path.join(HERE, "ledger", os.path.basename(plan_path).replace(".json", ".jsonl")), "a", encoding="utf-8")
        for i, step in enumerate(plan):
            if i < start: continue
            tool, args = step["tool"], dict(step.get("args", {}))
            expect = step.get("expect", "ok")
            for k, v in list(args.items()):
                if v == "LAST": args[k] = globals().get("LAST_EXPORT", "")
            t0 = time.time(); d, txt = call_raw(tool, args); dt = time.time() - t0
            ok, text = classify(d)
            meta = result_fields(d)
            if isinstance(meta.get("exportId"), str): globals()["LAST_EXPORT"] = meta["exportId"]
            state = alive()
            verdict = step_verdict(d, expect)
            rec = {"i": i, "tool": tool, "args": args, "expect": expect, "ok": ok, "verdict": verdict, "text": text[:600], "secs": round(dt, 1), "note": step.get("note", ""),
                   "contract": "v4", "outcome": (d.get("meta") or {}).get("outcome"), "execution": (d.get("meta") or {}).get("execution"),
                   "keys": {k: meta.get(k) for k in step.get("keys", []) if k in meta}, "tia": state}
            led.write(json.dumps(rec, ensure_ascii=False, default=str) + "\n"); led.flush()
            print("%3d %-4s %-36s %5.1fs %s" % (i, verdict, tool, dt, text[:150].replace("\n", " ")))
            for k, v in rec["keys"].items(): print("        ", k, "=", trim(v, 500))
            if (d.get("meta") or {}).get("requiresSessionReset") or (d.get("meta") or {}).get("outcome") == "unknown":
                print("!!! Unconfirmed outcome; stop without retry or cleanup"); break
            if state.get("alive") is False or state.get("connected") is False:
                print("!!! TIA / binding lost after step", i, state); break
        led.close()
