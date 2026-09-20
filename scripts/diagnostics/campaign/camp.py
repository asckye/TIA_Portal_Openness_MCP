# -*- coding: utf-8 -*-
"""Real-machine campaign runner (scripts/diagnostics/campaign). Talks to the VM engine through scripts/diagnostics/Probe-McpServer.py
(the tia-portal-vm entry of ~/.claude.json: url + Authorization header), so it works from any machine that has that entry.
  python plans/plan_x.py                            regenerate plans/plan_x.json from its .py
  python camp.py run plans/plan_x.json [start]      run it; rows go to ledger/plan_x.jsonl; stops when TIA dies
  python make_ledger.py                             regenerate docs/reference/real-machine-ledger.md (ledger-runs.jsonl.gz + ledger/*.jsonl + overrides)

  camp.py call <Tool> '<json>' [metaKey,...]      one call, full result (trimmed)
  camp.py run <plan.json> [startIndex]            run a plan, append to ledger/<plan>.jsonl, stop when TIA dies
  camp.py raw <Tool> '<json>'                     print the raw text of the tool result (first 6000 chars)
"""
import sys, json, importlib.util, os, time, io
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))   # scripts/diagnostics/campaign -> repo root
spec = importlib.util.spec_from_file_location("probe", os.path.join(REPO, "scripts/diagnostics/Probe-McpServer.py"))
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
e = m.find_claude_entry(); p = m.Probe(e["url"], e["headers"]["Authorization"], 900); p.initialize()
LITE = {t["name"] for t in p.rpc("tools/list", {})["result"]["tools"]}

def call_raw(name, args):
    if name in LITE:
        r = p.rpc("tools/call", {"name": name, "arguments": args})
    else:
        r = p.rpc("tools/call", {"name": "CallTool", "arguments": {"name": name, "argumentsJson": json.dumps(args, ensure_ascii=False)}})
    if "error" in r: return {"rpcError": r["error"]}, ""
    txt = r["result"]["content"][0]["text"]
    try: d = json.loads(txt)
    except ValueError: return {"raw": txt}, txt
    if name not in LITE:
        msg = d.get("message")
        if isinstance(msg, str):
            try: msg = json.loads(msg)
            except ValueError: pass
        if isinstance(msg, dict) and ("Message" in msg or "Meta" in msg or "message" in msg):
            inner = msg
            d = {"message": inner.get("Message", inner.get("message")), "meta": inner.get("Meta", inner.get("meta", {})), "_outer": d.get("meta", {})}
        else:
            d = {"message": msg, "meta": d.get("meta", {})}
    return d, txt

def alive():
    d, _ = call_raw("GetState", {})
    try:
        h = d["meta"]["hmiReadHealth"]; pp = h["portalProcess"]
        return {"pid": pp.get("boundProcessId"), "alive": pp.get("processAlive"), "blocked": h.get("snapshotReadsBlocked"), "connected": d.get("isConnected"), "project": d.get("project")}
    except Exception:
        return {"raw": json.dumps(d, ensure_ascii=False)[:200]}

def classify(d):
    meta = d.get("meta") or {}
    if "rpcError" in d: return False, "rpc:" + str(d["rpcError"])[:300]
    if "raw" in d: return None, d["raw"][:300]
    err = meta.get("error") or meta.get("errorCode") or d.get("error")
    ok = meta.get("success")
    if ok is None: ok = err is None
    text = str(d.get("message") or "")
    if err: text = str(err).split("\n")[0][:400]
    return bool(ok) and not err, text

def trim(v, n=900):
    s = json.dumps(v, ensure_ascii=False, default=str)
    return s if len(s) <= n else s[:n] + "...(" + str(len(s)) + ")"

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode in ("call", "raw", "full"):
        tool = sys.argv[2]; args = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
        d, txt = call_raw(tool, args)
        if mode == "raw": print(txt[:6000]); sys.exit()
        if mode == "full": print(txt); sys.exit()
        ok, text = classify(d)
        print("==", tool, "ok" if ok else "FAIL", "::", text[:400])
        keys = sys.argv[4].split(",") if len(sys.argv) > 4 else []
        meta = d.get("meta") or {}
        for k in keys:
            if k == "*": print("    meta =", trim(meta, 4000))
            elif k in meta: print("   ", k, "=", trim(meta[k], 1500))
        print("   ", alive())
    elif mode == "run":
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
            meta = d.get("meta") or {}
            if isinstance(meta.get("exportId"), str): globals()["LAST_EXPORT"] = meta["exportId"]
            state = alive()
            verdict = "PASS" if (ok and expect in ("ok", "any")) or (not ok and expect in ("error", "any")) else "FAIL"
            rec = {"i": i, "tool": tool, "args": args, "expect": expect, "ok": ok, "verdict": verdict, "text": text[:600], "secs": round(dt, 1), "note": step.get("note", ""),
                   "keys": {k: meta.get(k) for k in step.get("keys", []) if k in meta}, "tia": state}
            led.write(json.dumps(rec, ensure_ascii=False, default=str) + "\n"); led.flush()
            print("%3d %-4s %-36s %5.1fs %s" % (i, verdict, tool, dt, text[:150].replace("\n", " ")))
            for k, v in rec["keys"].items(): print("        ", k, "=", trim(v, 500))
            if state.get("alive") is False or state.get("connected") is False:
                print("!!! TIA / binding lost after step", i, state); break
        led.close()
