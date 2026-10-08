"""Small stdio-only smoke proof for all eight shipped MCP hosts.

The SDK-only installation fixture has no TIA executable. GUI launchers and the
updater have no MCP roster; their startup checks use argument validation/help.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import time


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("smoke_approval", Path(__file__).with_name("Test-ReleaseApprovalGate.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def assert_roster(reply, key):
    tools = reply.get("result", {}).get("tools", [])
    names = [tool["name"] for tool in tools]
    baseline = json.loads((ROOT / f"manifest/contracts/v4/baseline/{key}.json").read_text("utf-8"))
    expected = [tool["name"] for tool in baseline["tools"]]
    gate.require(len(names) == len(set(names)) and set(names) == set(expected), f"V{key}: smoke roster differs from baseline")


def assert_environment(reply, key):
    body, _ = gate.parse_envelope(reply)
    gate.require(isinstance(body.get("data"), dict) and isinstance(body.get("ok"), bool),
                 f"V{key}: InitializeEnvironment did not answer with a V4 envelope")


def run(args):
    started = time.monotonic()
    results = {}
    for key in args.releases:
        root, env = gate.fresh_data_root(args.temp_root, f"smoke-{key}", approval_enabled=None)
        with gate.retained_on_failure(root):
            exe = args.runtime_root / f"v{key}/TiaMcp.FoundationHost.exe"
            with gate.foundation_host(exe, key, args.public_api_root, env, root / "stderr.log") as host:
                assert_roster(host.rpc("tools/list"), key)
                assert_environment(host.rpc("tools/call", {"name": "InitializeEnvironment", "arguments": {}}), key)
                stopped = gate.assert_foundation_write_stopped(host.rpc("tools/call", {
                    "name": gate.FOUNDATION_WRITE[0], "arguments": gate.FOUNDATION_WRITE[1]}), f"V{key} smoke")
        results[key] = {"checksPassed": 4, "transport": "stdio", "stoppedBy": stopped, "tiaConnected": False}
    for name, exe, arguments, expected in (
        ("studio", args.runtime_root / "studio/TiaOpenness.exe", ["--network", "127.0.0.1", "not-a-port", "S-1-5-18"], 1),
        ("configurator", args.runtime_root.parent / "TiaOpenness.exe", ["--network", "127.0.0.1", "not-a-port", "S-1-5-18"], 0),
        ("updater", args.runtime_root / "tools/TiaMcp.Updater.exe", ["-Help"], 0),
    ):
        root, env = gate.fresh_data_root(args.temp_root, f"smoke-{name}", approval_enabled=None)
        with gate.retained_on_failure(root):
            env["TIA_OPENNESS_NETWORK_NO_DIALOG"] = "1"
            reply = subprocess.run([str(exe), *arguments], env=env, capture_output=True, text=True, timeout=30,
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            gate.require(reply.returncode == expected, f"{name}: startup check exited {reply.returncode}: {reply.stdout} {reply.stderr}")
        results[name] = {"checksPassed": 1, "startup": "help" if name == "updater" else "argument-validation", "mcp": "not-applicable"}
    result = {"status": "passed", "checksPassed": len(args.releases) * 4 + 3, "products": results, "elapsedSeconds": time.monotonic() - started}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result))


def self_test():
    body = {"result": {"isError": True, "structuredContent": {"schemaVersion": 4, "ok": False, "data": {}, "error": {}, "meta": {}}}}
    assert_environment(body, "synthetic")
    bad = {"result": {"structuredContent": {"schemaVersion": 3}}}
    try:
        assert_environment(bad, "bad")
    except (gate.CheckFailure, ValueError):
        pass
    else:
        raise AssertionError("Invalid environment response accepted")
    gate.self_test()
    print("PASS smoke envelope checks: 2; approval helper checks: 5")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--releases", nargs="+", choices=[*gate.KEYS, "20", "21"], default=[*gate.KEYS, "20", "21"])
    for name in ("runtime-root", "public-api-root", "temp-root", "output"):
        parser.add_argument("--" + name, type=Path)
    options = parser.parse_args()
    if options.self_test:
        self_test()
    else:
        if any(getattr(options, name.replace("-", "_")) is None for name in ("runtime-root", "public-api-root", "temp-root", "output")):
            parser.error("runtime-root, public-api-root, temp-root and output are required")
        run(options)
