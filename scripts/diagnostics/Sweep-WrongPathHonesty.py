"""给每个只读工具喂一条**不存在的路径**，凡是「返回成功」的都列为嫌疑。

盯的是这一类缺陷：**路径写错却报成功**。
它比崩溃危险得多 —— 调用方（尤其是模型）拿到 isError=false + 一张空清单，
会把「我路径写错了」记成「这个 PLC 里确实没有这种东西」，然后据此继续往下走：
去重建已经存在的块、去汇报一个不存在的结论。错误在这里不会停，只会被放大。

这条检查**离线跑不了**：要判断「路径不存在时的反应」，就得有一个真的项目。
所以它不在 offline-checks 里，是发版前的手工闸门。

用法：
    python scripts/diagnostics/Sweep-WrongPathHonesty.py <项目.ap21> [引擎.exe]

退出码：有嫌疑 = 1，全部正确报错 = 0。

嫌疑不等于缺陷 —— 有些工具「找不到」时返回一个带 ok=false 的结构体也是自洽的。
但每一条都必须**被看过并有意保留**，而不是没人注意到。
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import envelope, successful

import json
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]

# 只调只读工具：前缀白名单 + 关键词黑名单双重过滤。
# 宁可漏掉几个也不能误调写操作 —— 这个脚本是拿真项目跑的。
READ_PREFIX = ("Get", "List", "Describe", "Find", "Probe", "Validate", "Check",
               "Read", "Search", "Analyze", "Preflight", "Inspect", "Diagnose")
DENY = re.compile(r"Set|Write|Delete|Create|Add|Plug|Import|Export|Compile|Download|"
                  r"Upload|Save|Clear|Sync|ConnectPortal|DisconnectPortal|Open|Close|Scaffold|"
                  r"Build|Generate|Ensure|Attach|Detach|Go(Online|Offline)|Start|Stop|"
                  r"Apply|Repair|Fix|Rename|Move|Copy|Reset|Run", re.I)

# 故意写错的路径。带 _zzz 后缀是为了不可能和真实对象重名。
BOGUS = {
    "softwarePath": "NoSuchPlc_zzz",
    "plcSoftwarePath": "NoSuchPlc_zzz",
    "deviceItemPath": "NoSuchStation_zzz/NoSuchItem_zzz",
    "devicePath": ["NoSuchStation_zzz"],
    "blockPath": "NoSuchBlock_zzz",
    "blockName": "NoSuchBlock_zzz",
    "objectPath": "NoSuchBlock_zzz",
    "tagTableName": "NoSuchTable_zzz",
    "objectKind": "Block",
    "maxDepth": 3,
    "changedOnly": True,
}
PATH_ARGS = ("softwarePath", "plcSoftwarePath", "deviceItemPath",
             "devicePath", "blockPath", "objectPath")


class Engine:
    def __init__(self, exe):
        self.p = subprocess.Popen([exe, "--logging", "0", "--profile", "full"],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True,
                                  encoding="utf-8", errors="replace", bufsize=1)
        self.seq = 0
        self.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                    "clientInfo": {"name": "wrong-path-sweep", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def request(self, method, params=None, timeout=600):
        self.seq += 1
        message = {"jsonrpc": "2.0", "id": self.seq, "method": method}
        if params is not None:
            message["params"] = params
        self.p.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.p.stdin.flush()
        deadline = time.time() + timeout
        while True:
            if time.time() > deadline:
                raise RuntimeError("timeout on " + method)
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("engine exited: " + self.p.stderr.read()[:600])
            try:
                parsed = json.loads(line)
            except Exception:
                continue
            if parsed.get("id") == self.seq:
                return parsed

    def call(self, tool, args, timeout=180):
        answer = self.request("tools/call", {"name": tool, "arguments": args}, timeout)
        return envelope(answer)

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=60)
        except Exception:
            self.p.kill()


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    project = str(pathlib.Path(sys.argv[1]).resolve())
    exe = str(pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) > 2
              else ROOT / 'runtime/v21/TiaMcp.Engine.V21.exe')
    engine = Engine(exe)
    reset_required = False
    try:
        tools = engine.request('tools/list')['result']['tools']
        successful(engine.call('ConnectPortal', {}))
        successful(engine.call('OpenProject', {'path': project}, 900))
        # A successful sentinel prevents a broken session from passing every refusal.
        successful(engine.call('ListDevices', {}))
        suspects, honest, skipped = [], [], []
        for tool in tools:
            name = tool['name']
            if not name.startswith(READ_PREFIX) or DENY.search(name): continue
            schema = tool.get('inputSchema') or {}
            required = schema.get('required') or []
            if not any(r in PATH_ARGS for r in required): continue
            args, missing = {}, []
            for key in required:
                if key not in BOGUS: missing.append(key); continue
                args[key] = BOGUS[key]
                if schema['properties'][key].get('type') == 'array' and isinstance(args[key], str):
                    args[key] = [args[key]]
            if missing:
                skipped.append((name, missing)); continue
            try:
                value = engine.call(name, args)
            except (RuntimeError, ValueError) as ex:
                suspects.append((name, args, 'Protocol/timeout: ' + str(ex)))
                reset_required = True
                break
            if value['meta']['requiresSessionReset'] or value['meta']['outcome'] == 'unknown':
                suspects.append((name, args, json.dumps(value, ensure_ascii=False)))
                reset_required = True
                break
            if not value['ok'] and value['error']['code'] == 'INVALID_ARGUMENT':
                skipped.append((name, ['V4 argument admission; wrong-path behavior not exercised']))
            elif not value['ok']:
                honest.append(name)
            else:
                suspects.append((name, args, json.dumps(value, ensure_ascii=False)))
        if not reset_required: successful(engine.call('CloseProject', {}))
        print('Wrong-path refusals %d | suspects %d | not covered %d' % (len(honest), len(suspects), len(skipped)))
        for name, args, body in suspects:
            print(name, json.dumps(args, ensure_ascii=False), body[:300])
        for name, missing in skipped: print('NOT COVERED', name, ', '.join(missing))
        return 1 if suspects else 0
    finally:
        engine.close()


if __name__ == '__main__':
    sys.exit(main())
