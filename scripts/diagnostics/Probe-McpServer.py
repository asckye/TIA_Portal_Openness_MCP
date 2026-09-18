"""Direct HTTP probe for a running TiaMcpServer (Streamable HTTP MCP endpoint), for real-project verification
from a machine whose MCP client cannot reach the server (e.g. a system HTTP_PROXY swallows the LAN request).

    python scripts/diagnostics/Probe-McpServer.py tools [filter ...]          list the advertised (lite) tools
    python scripts/diagnostics/Probe-McpServer.py call   <Tool> [json|@file]  call a tool that is in the lite roster
    python scripts/diagnostics/Probe-McpServer.py bridge <Tool> [json|@file]  call ANY tool through CallTool(name, argumentsJson)

Connection: --url / --token, or the environment (TIA_MCP_URL / TIA_MCP_TOKEN), or - by default - the `tia-portal-vm`
entry of ~/.claude.json (url + Authorization header). The proxy is bypassed. The bearer token is never printed.
Arguments given as @file.json avoid shell backslash mangling on Windows. Responses larger than the server's page size
come back as a GetExport handle (see the engine's export paging); pass small `limit` values instead of paging here.
Every call opens its own MCP session; the engine keeps its Portal state across sessions.
"""
import argparse, json, os, sys, urllib.request


def find_claude_entry(name="tia-portal-vm"):
    home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
    path = os.path.join(home, ".claude.json")
    if not os.path.exists(path):
        return None

    def walk(o):
        if isinstance(o, dict):
            entry = o.get(name)
            if isinstance(entry, dict) and "url" in entry:
                return entry
            for v in o.values():
                r = walk(v)
                if r:
                    return r
        elif isinstance(o, list):
            for v in o:
                r = walk(v)
                if r:
                    return r
        return None

    with open(path, encoding="utf-8") as f:
        return walk(json.load(f))


class Probe:
    def __init__(self, url, auth, timeout):
        self.url, self.auth, self.timeout = url, auth, timeout
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))  # bypass any system proxy
        self.session = None
        self._id = 0

    def rpc(self, method, params=None, notify=False):
        body = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            body["params"] = params
        if not notify:
            self._id += 1
            body["id"] = self._id
        req = urllib.request.Request(self.url, data=json.dumps(body).encode("utf-8"), method="POST")
        req.add_header("Authorization", self.auth)
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        if self.session:
            req.add_header("Mcp-Session-Id", self.session)
        with self.opener.open(req, timeout=self.timeout) as resp:
            sid = resp.headers.get("Mcp-Session-Id")
            if sid:
                self.session = sid
            raw = resp.read().decode("utf-8", "replace")
            ctype = resp.headers.get("Content-Type", "")
        if notify:
            return None
        if "text/event-stream" in ctype:
            # SSE: events separated by blank lines; a payload may span several data: lines; the last JSON-RPC response wins.
            events, cur = [], []
            for line in raw.splitlines():
                if line.startswith("data:"):
                    cur.append(line[5:].lstrip())
                elif not line.strip() and cur:
                    events.append("\n".join(cur)); cur = []
            if cur:
                events.append("\n".join(cur))
            for ev in reversed(events):
                try:
                    obj = json.loads(ev)
                    if isinstance(obj, dict) and ("result" in obj or "error" in obj):
                        return obj
                except ValueError:
                    continue
            raw = events[-1] if events else raw
        return json.loads(raw) if raw.strip() else None

    def initialize(self):
        init = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "Probe-McpServer", "version": "1"}})
        self.rpc("notifications/initialized", notify=True)
        info = init["result"]["serverInfo"]
        return f"{info.get('name')} {info.get('version')}  protocol={init['result'].get('protocolVersion')}"


def load_args(text):
    if not text:
        return {}
    if text.startswith("@"):
        with open(text[1:], encoding="utf-8") as f:
            text = f.read()
    return json.loads(text)


def print_result(r, bridge):
    res = r.get("result") or r
    for c in res.get("content", []):
        if c.get("type") != "text":
            print(c); continue
        txt = c["text"]
        try:
            obj = json.loads(txt)
            if bridge and isinstance(obj.get("message"), str):
                try:
                    obj["message"] = json.loads(obj["message"])  # CallTool wraps the inner tool JSON as a string
                except ValueError:
                    pass
            print(json.dumps(obj, ensure_ascii=False, indent=2))
        except ValueError:
            print(txt)
    if res.get("isError"):
        print("** isError=true **")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("mode", choices=["tools", "call", "bridge"])
    p.add_argument("rest", nargs="*")
    p.add_argument("--url", default=os.environ.get("TIA_MCP_URL"))
    p.add_argument("--token", default=os.environ.get("TIA_MCP_TOKEN"), help="bearer token (without 'Bearer ')")
    p.add_argument("--timeout", type=int, default=600)
    a = p.parse_args()
    url, auth = a.url, ("Bearer " + a.token) if a.token else None
    if not url or not auth:
        entry = find_claude_entry()
        if not entry:
            sys.exit("no --url/--token, no TIA_MCP_URL/TIA_MCP_TOKEN and no tia-portal-vm entry in ~/.claude.json")
        url = url or entry["url"]
        auth = auth or entry.get("headers", {}).get("Authorization")
    probe = Probe(url, auth, a.timeout)
    print("server:", probe.initialize())
    if a.mode == "tools":
        tools, cursor = [], None
        while True:
            r = probe.rpc("tools/list", {"cursor": cursor} if cursor else {})
            tools += r["result"]["tools"]
            cursor = r["result"].get("nextCursor")
            if not cursor:
                break
        print(f"tools: {len(tools)}")
        for t in sorted(tools, key=lambda t: t["name"]):
            if not a.rest or any(k.lower() in t["name"].lower() for k in a.rest):
                print(" ", t["name"])
        return
    if not a.rest:
        sys.exit("tool name required")
    name, args = a.rest[0], load_args(a.rest[1] if len(a.rest) > 1 else "")
    if a.mode == "bridge":
        args = {"name": name, "argumentsJson": json.dumps(args, ensure_ascii=False)}
        name = "CallTool"
    print_result(probe.rpc("tools/call", {"name": name, "arguments": args}), a.mode == "bridge")


if __name__ == "__main__":
    main()
