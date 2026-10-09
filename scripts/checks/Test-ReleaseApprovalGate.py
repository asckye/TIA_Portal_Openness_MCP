"""Release-time proof that default-on approvals refuse writes before dispatch.

This is an offline process check. It never attaches to or connects to TIA.
Each host process receives a private TIA_MCP_DATA_DIRECTORY and an explicit
approval.settings file. Foundation releases do not advertise CallTool, so that
bridge case is run for the two engine hosts and recorded as unavailable for
Foundation rather than changing its public tool catalog.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import json
import importlib.util
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import uuid

ROOT = Path(__file__).resolve().parents[2]
KEYS = ("14sp1", "15.1", "16", "17", "18", "19")
# Catalog WRITE tools with arguments valid against the released schemas (reference/tool-examples), so admission passes
# and the default-on approval is what refuses the write. SaveProject is SESSION in the catalog and is not approval-gated;
# safe-v4 candidate arguments (mode, expectedPlanHash) are not in the released schemas.
STAGING_WRITE = ("StageImportFiles", {"files": [{"fileName": "ApprovalProbe.scl", "kind": "scl",
    "content": "FUNCTION ApprovalProbe : Void\nBEGIN\nEND_FUNCTION\n"}], "dryRun": False})
ENGINE_WRITE = ("CreateDevice", {"orderNumber": "6ES7 515-2AM02-0AB0", "version": "V2.9", "deviceName": "PLC_2"})
FOUNDATION_WRITE = ("CreatePlcTagTable", {"plc": "PLC_1", "group": "", "name": "ExampleTags", "dryRun": True, "confirm": False})

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from mcp_results import envelope

spec = importlib.util.spec_from_file_location("release_resource_helpers", Path(__file__).with_name("Test-ResourceDiscovery.py"))
resources = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(resources)


class CheckFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise CheckFailure(message)


def parse_envelope(reply: dict) -> tuple[dict, bool]:
    result = reply.get("result")
    require(isinstance(result, dict), f"Missing MCP result: {reply}")
    body = envelope(reply)
    require(isinstance(body, dict) and body.get("schemaVersion") == 4, f"Invalid V4 envelope: {body}")
    return body, bool(result.get("isError"))


def assert_refused(reply: dict, label: str) -> None:
    body, is_error = parse_envelope(reply)
    error = body.get("error") or {}
    details = error.get("details") or {}
    meta = body.get("meta") or {}
    require(is_error and body.get("ok") is False, f"{label}: refusal was not an MCP error: {body}")
    require(error.get("code") == "CONFIRMATION_REQUIRED", f"{label}: wrong error code: {body}")
    require(details.get("reason") == "workbench-unavailable", f"{label}: wrong refusal reason: {body}")
    require(meta.get("outcome") == "rejected-before-operation" and meta.get("execution") == "not-started",
            f"{label}: operation was not proven to stop before dispatch: {body}")
    require(meta.get("requiresSessionReset") is False, f"{label}: refusal incorrectly requires a session reset")


def assert_readiness_refused(reply: dict, label: str) -> None:
    body, is_error = parse_envelope(reply)
    error = body.get("error") or {}
    details = error.get("details") or {}
    meta = body.get("meta") or {}
    require(is_error and body.get("ok") is False, f"{label}: refusal was not an MCP error: {body}")
    require(error.get("code") == "RESOURCE_UNAVAILABLE" and details.get("resource") == "tia-openness-environment",
            f"{label}: readiness did not take precedence: {body}")
    require(meta.get("outcome") == "rejected-before-operation" and meta.get("execution") == "not-started",
            f"{label}: readiness was not proven to stop before dispatch: {body}")
    require(meta.get("requiresSessionReset") is False, f"{label}: refusal incorrectly requires a session reset")


def assert_foundation_write_stopped(reply: dict, label: str) -> str:
    """Foundation admits a write only with a live worker before it asks for approval (P6-44: approval follows admission).
    Since P6-55 the bundled worker's readiness (TIA installation and Openness group) is checked first, so a release
    build without TIA stops at readiness; otherwise at worker admission, and with a worker the default-on approval
    refuses it. Every path must stop before dispatch. The approval decision itself is covered by ApprovalHostTests."""
    body, is_error = parse_envelope(reply)
    error = body.get("error") or {}
    details = error.get("details") or {}
    meta = body.get("meta") or {}
    require(is_error and body.get("ok") is False, f"{label}: refusal was not an MCP error: {body}")
    readiness = error.get("code") == "RESOURCE_UNAVAILABLE" and details.get("resource") == "tia-openness-environment"
    admission = error.get("code") == "PRECONDITION_FAILED" and details.get("condition") == "worker-admission"
    approval = error.get("code") == "CONFIRMATION_REQUIRED" and details.get("reason") == "workbench-unavailable"
    require(readiness or admission or approval,
            f"{label}: write was not stopped by readiness, worker admission or approval: {body}")
    require(meta.get("outcome") == "rejected-before-operation" and meta.get("execution") == "not-started",
            f"{label}: operation was not proven to stop before dispatch: {body}")
    return "readiness" if readiness else "worker-admission" if admission else "approval"


def assert_engine_read(reply: dict, label: str) -> None:
    body, is_error = parse_envelope(reply)
    require(not is_error and body.get("ok") is True and body.get("error") is None,
            f"{label}: read call failed while approval was enabled: {body}")


def fresh_data_root(temp_root: Path, label: str, approval_enabled: bool | None = True) -> tuple[Path, dict[str, str]]:
    root = temp_root / "approval-host-data" / f"{label}-{uuid.uuid4().hex}"
    (root / "config").mkdir(parents=True)
    (root / "temp").mkdir()
    (root / "local-app-data").mkdir()
    (root / "app-data").mkdir()
    if approval_enabled is not None:
        settings = root / "config" / "approval.settings"
        settings.write_text(f"enabled={'true' if approval_enabled else 'false'}\ntimeoutSeconds=120\n", encoding="utf-8")
    env = os.environ.copy()
    env.update({
        "TIA_MCP_DATA_DIRECTORY": str(root),
        "TIA_MCP_DIAGNOSTICS_DIRECTORY": str(root / "diagnostics"),
        "LOCALAPPDATA": str(root / "local-app-data"),
        "APPDATA": str(root / "app-data"),
        "TEMP": str(root / "temp"),
        "TMP": str(root / "temp"),
    })
    return root, env


@contextmanager
def retained_on_failure(root: Path):
    completed = False
    try:
        yield
        completed = True
    finally:
        if completed:
            try:
                shutil.rmtree(root)
            except OSError as error:
                raise CheckFailure(f"Could not remove successful check data root {root}: {error}") from error
        else:
            print(f"RETAINED FAILED HOST DATA: {root}", file=sys.stderr, flush=True)


class FoundationStdio:
    def __init__(self, command: list[str], env: dict[str, str], stderr_path: Path):
        self.errors = stderr_path.open("w", encoding="utf-8")
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.errors, text=True, encoding="utf-8", env=env,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.lines: queue.Queue[str | None] = queue.Queue()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        self.sequence = 0

    def _read(self):
        assert self.process.stdout is not None
        for line in self.process.stdout:
            if line.strip():
                self.lines.put(line)
        self.lines.put(None)

    def rpc(self, method: str, params: dict | None = None, *, notification: bool = False) -> dict | None:
        self.sequence += 1
        request_id = self.sequence
        request = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        if not notification:
            request["id"] = request_id
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        if notification:
            return None
        while True:
            raw = self.lines.get(timeout=30)
            require(raw is not None, "Foundation host exited before the MCP response")
            reply = json.loads(raw)
            if reply.get("id") == request_id:
                return reply

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=5)
        if self.process.stdout:
            self.process.stdout.close()
        self.reader.join(timeout=5)
        self.errors.close()
        require(self.process.returncode == 0, f"Foundation host exited with {self.process.returncode}")


@contextmanager
def foundation_host(exe: Path, key: str, public_api: Path, env: dict[str, str], stderr_path: Path):
    command = [str(exe), "--release-key", key, "--public-api", str(public_api), "--offline", "--transport", "stdio", "--logging", "0"]
    if key in ("20", "21"):
        command += ["--profile", "full"]
    host = FoundationStdio(command, env, stderr_path)
    try:
        initialized = host.rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                               "clientInfo": {"name": "release-approval-gate", "version": "1"}})
        require(initialized is not None and "result" in initialized, f"{key}: Foundation initialize failed")
        host.rpc("notifications/initialized", notification=True)
        yield host
    finally:
        host.close()


def rpc_call(rpc, name: str, arguments: dict, request_id: str) -> dict:
    reply = rpc("tools/call", request_id, {"name": name, "arguments": arguments})
    require(reply is not None, f"{name}: no MCP response")
    return reply


def run_engine(args) -> dict:
    require(os.name == "nt", "Release host checks require Windows")
    exe, portal, api = args.exe.resolve(), args.portal_root.resolve(), args.public_api.resolve()
    harness = args.host_harness.resolve() if args.host_harness else None
    require(exe.is_file() and portal.is_dir() and (harness is None or harness.is_file()) and api.is_dir(), "Engine check input is missing")
    require(harness is not None, "The default-on approval proof must run through the TiaMcp.Engine.Harness harness")
    count = 0
    results = {}

    direct_root, direct_env = fresh_data_root(args.temp_root, f"engine-v{args.major}-direct")
    with retained_on_failure(direct_root):
        with resources.server(exe, portal, args.major, "stdio", "full", harness, api,
                              env_overrides=direct_env, engine_worker=args.engine_worker, engine_catalog=args.engine_catalog) as (rpc, _, _):
            initialized = rpc("initialize", "init", {"protocolVersion": "2024-11-05", "capabilities": {},
                                                       "clientInfo": {"name": "release-approval-gate", "version": "1"}})
            require("result" in initialized, f"V{args.major}: direct host initialize failed")
            rpc("notifications/initialized", notification=True)
            names = {row["name"] for row in rpc("tools/list", "list")["result"]["tools"]}
            require(ENGINE_WRITE[0] in names, f"V{args.major}: {ENGINE_WRITE[0]} is missing from the full roster")
            assert_refused(rpc_call(rpc, ENGINE_WRITE[0], ENGINE_WRITE[1], "write"), f"V{args.major} direct {ENGINE_WRITE[0]}")
            count += 1
            assert_refused(rpc_call(rpc, STAGING_WRITE[0], STAGING_WRITE[1], "stage-write"), f"V{args.major} direct staging")
            count += 1
            assert_engine_read(rpc_call(rpc, "GetSessionState", {}, "read"), f"V{args.major} direct GetSessionState")
            count += 1
        results["direct"] = "refused-before-dispatch; read-succeeded"

    bridge_root, bridge_env = fresh_data_root(args.temp_root, f"engine-v{args.major}-calltool")
    with retained_on_failure(bridge_root):
        with resources.server(exe, portal, args.major, "stdio", "lite", harness, api,
                              env_overrides=bridge_env, engine_worker=args.engine_worker, engine_catalog=args.engine_catalog) as (rpc, _, _):
            initialized = rpc("initialize", "init", {"protocolVersion": "2024-11-05", "capabilities": {},
                                                       "clientInfo": {"name": "release-approval-gate", "version": "1"}})
            require("result" in initialized, f"V{args.major}: CallTool host initialize failed")
            rpc("notifications/initialized", notification=True)
            names = {row["name"] for row in rpc("tools/list", "list")["result"]["tools"]}
            require("CallTool" in names, f"V{args.major}: CallTool is missing from the lite roster")
            bridge_args = {"name": ENGINE_WRITE[0], "arguments": ENGINE_WRITE[1]}
            assert_refused(rpc_call(rpc, "CallTool", bridge_args, "bridge-write"), f"V{args.major} CallTool {ENGINE_WRITE[0]}")
            count += 1
            assert_refused(rpc_call(rpc, "CallTool", {"name": STAGING_WRITE[0], "arguments": STAGING_WRITE[1]}, "bridge-stage"), f"V{args.major} CallTool staging")
            count += 1
        results["CallTool"] = "refused-before-dispatch"

    readiness_root, readiness_env = fresh_data_root(args.temp_root, f"engine-v{args.major}-readiness", approval_enabled=None)
    with retained_on_failure(readiness_root):
        readiness_install = readiness_root / "sdk-only-tia-install"
        readiness_install.mkdir()
        readiness_api = readiness_install / "PublicAPI" / f"V{args.major}"
        readiness_api.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(api, readiness_api)
        with resources.server(exe, readiness_install, args.major, "stdio", "full", None, api,
                              env_overrides=readiness_env) as (rpc, _, logs):
            initialized = rpc("initialize", "init", {"protocolVersion": "2024-11-05", "capabilities": {},
                                                       "clientInfo": {"name": "release-readiness-gate", "version": "1"}})
            require("result" in initialized, f"V{args.major}: real EXE host initialize failed: {logs[-10:]}")
            rpc("notifications/initialized", notification=True)
            names = {row["name"] for row in rpc("tools/list", "list")["result"]["tools"]}
            require(ENGINE_WRITE[0] in names, f"V{args.major}: {ENGINE_WRITE[0]} is missing from the real EXE roster")
            assert_readiness_refused(rpc_call(rpc, ENGINE_WRITE[0], ENGINE_WRITE[1], "readiness-write"),
                                     f"V{args.major} real EXE readiness-before-approval")
            count += 1
            assert_refused(rpc_call(rpc, STAGING_WRITE[0], STAGING_WRITE[1], "readiness-stage"), f"V{args.major} real EXE staging without Openness")
            count += 1
        results["realExeReadiness"] = "readiness refused before approval; default approval.settings absent"

    return {"product": "engine", "major": args.major, "checksPassed": count, "checksExpected": 7, "results": results}


def run_foundation(args) -> dict:
    require(os.name == "nt", "Release host checks require Windows")
    runtime_root, api = args.runtime_root.resolve(), args.public_api.resolve()
    require(runtime_root.is_dir() and api.is_dir(), "Foundation check input is missing")
    count = 0
    per_release = {}
    for key in KEYS:
        exe = runtime_root / f"v{key}" / "TiaMcp.FoundationHost.exe"
        require(exe.is_file(), f"{key}: Foundation host is missing: {exe}")
        root, env = fresh_data_root(args.temp_root, f"foundation-{key}")
        with retained_on_failure(root):
            with foundation_host(exe, key, api, env, root / "foundation-host-stderr.log") as host:
                names_reply = host.rpc("tools/list")
                require(names_reply is not None and "result" in names_reply, f"{key}: tools/list failed")
                names = {row["name"] for row in names_reply["result"]["tools"]}
                require(FOUNDATION_WRITE[0] in names and "GetToolUsage" in names, f"{key}: expected tools missing")
                require("CallTool" not in names, f"{key}: CallTool unexpectedly appeared in the frozen Foundation V4 roster")
                count += 1
                refused = host.rpc("tools/call", {"name": FOUNDATION_WRITE[0], "arguments": FOUNDATION_WRITE[1]})
                require(refused is not None, f"{key}: direct write received no response")
                stopped_by = assert_foundation_write_stopped(refused, f"Foundation {key} direct {FOUNDATION_WRITE[0]}")
                count += 1
                staged = host.rpc("tools/call", {"name": STAGING_WRITE[0], "arguments": STAGING_WRITE[1]})
                require(staged is not None, f"{key}: staging write received no response")
                assert_refused(staged, f"Foundation {key} staging approval")
                count += 1
                read = host.rpc("tools/call", {"name": "GetToolUsage", "arguments": {"toolName": "SaveProject"}})
                require(read is not None, f"{key}: Foundation read received no response")
                assert_engine_read(read, f"Foundation {key} GetToolUsage")
                count += 1
        per_release[key] = {"checksPassed": 4, "stagingWrite": "approval-refused-before-filesystem-write", "directWrite": "refused-before-dispatch", "stoppedBy": stopped_by,
                            "read": "succeeded", "CallTool": "not-advertised-by-Foundation-V4"}
    return {"product": "foundation", "checksPassed": count, "checksExpected": len(KEYS) * 4,
            "callToolUnsupportedReleases": list(KEYS), "releases": per_release}


def self_test() -> int:
    refused = {"result": {"isError": True, "structuredContent": {
        "schemaVersion": 4, "ok": False, "data": {},
        "error": {"code": "CONFIRMATION_REQUIRED", "details": {"reason": "workbench-unavailable"}},
        "meta": {"outcome": "rejected-before-operation", "execution": "not-started", "requiresSessionReset": False}}}}
    assert_refused(refused, "synthetic refusal")
    readiness = json.loads(json.dumps(refused))
    readiness["result"]["structuredContent"]["error"] = {
        "code": "RESOURCE_UNAVAILABLE", "details": {"resource": "tia-openness-environment"}}
    assert_readiness_refused(readiness, "synthetic readiness refusal")
    bad = json.loads(json.dumps(refused))
    bad["result"]["structuredContent"]["meta"]["execution"] = "completed"
    try:
        assert_refused(bad, "synthetic dispatched write")
    except CheckFailure:
        pass
    else:
        raise AssertionError("A dispatched write was accepted")
    bad = json.loads(json.dumps(refused))
    bad["result"]["structuredContent"]["error"]["details"]["reason"] = "denied"
    try:
        assert_refused(bad, "wrong refusal reason")
    except CheckFailure:
        pass
    else:
        raise AssertionError("Wrong refusal reason was accepted")
    bad = json.loads(json.dumps(readiness))
    bad["result"]["structuredContent"]["meta"]["execution"] = "completed"
    try:
        assert_readiness_refused(bad, "synthetic dispatched readiness failure")
    except CheckFailure:
        pass
    else:
        raise AssertionError("A dispatched readiness failure was accepted")
    return 5


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--product", choices=("engine", "foundation"))
    parser.add_argument("--major", type=int, choices=(20, 21))
    parser.add_argument("--exe", type=Path)
    parser.add_argument("--portal-root", type=Path)
    parser.add_argument("--host-harness", type=Path)
    parser.add_argument('--engine-worker', type=Path, help='Explicit SDK-only worker for the combined net10 host')
    parser.add_argument('--engine-catalog', type=Path)
    parser.add_argument("--public-api", type=Path)
    parser.add_argument("--runtime-root", type=Path)
    parser.add_argument("--temp-root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.self_test:
        passed = self_test()
        print(f"COMPLETE: {passed} release approval gate self-tests passed; no hosts launched")
        return 0
    require(args.product is not None and args.temp_root is not None and args.output is not None,
            "--product, --temp-root and --output are required")
    args.temp_root = args.temp_root.resolve()
    require(args.temp_root.is_dir(), f"Temporary run directory is missing: {args.temp_root}")
    args.output = args.output.resolve()
    require(not args.output.exists(), f"Choose a new output directory: {args.output}")
    args.output.mkdir(parents=True)
    report = run_engine(args) if args.product == "engine" else run_foundation(args)
    if report["checksPassed"] != report["checksExpected"]:
        raise CheckFailure(f"Check count mismatch: {report}")
    report.update(resources.engine_fixture_evidence(args.engine_worker))
    report["status"] = "passed"
    report["approvalSettings"] = ("explicit enabled=true; timeoutSeconds=120 for approval proof; absent for real-EXE readiness proof"
                                   if args.product == "engine" else "explicit enabled=true; timeoutSeconds=120")
    report["workbenchConnected"] = False
    report["tiaConnected"] = False
    report["resultPath"] = str(args.output / "result.json")
    (args.output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if args.product == "engine":
        proof = "combined host" if args.engine_worker is not None else "harness"
        print(f"COMPLETE: {report['checksPassed']} approval/readiness checks passed for V{args.major}; {proof} proves default-on approval and real EXE proves readiness precedence; no TIA connection attempted")
    else:
        print(f"COMPLETE: {report['checksPassed']} default-approval checks passed across six Foundation releases; CallTool is not advertised by the Foundation V4 contract; no TIA connection attempted")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except CheckFailure as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
