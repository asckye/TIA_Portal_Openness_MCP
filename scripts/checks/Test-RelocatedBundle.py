"""Relocate a release bundle and check its read-only, no-TIA startup paths.

Run from the source checkout with --bundle-root pointing at an extracted package.
The checker copies it to a path containing spaces and Chinese characters, applies
a temporary read-only ACL, and enumerates every packaged product through MCP.
Foundation hosts are explicitly started with --offline. V20/V21 run as real EXEs over
STDIO and local HTTP with isolation off and on. A copied PublicAPI SDK-only fixture
resolves API assemblies; it contains no TIA executable and never connects to TIA or a
PLC. The approval probe uses RestartOpennessWorker, a local diagnostic write that does
not reach Openness.

The packaged no-TIA matrix separately stages V20/V21 runtime directories without
Siemens.Engineering*.dll and starts those EXEs over STDIO and HTTP, with isolation
off and on. It runs only when no TIA installation path is detected.

The check also probes the root launcher with a temporary target executable, verifies
that an invalid explicit bundle root does not fall back to the environment, checks
selected-engine refusal and calls UpdateCheck.Launch against this source checkout.
It requires Windows, .NET 10 SDK, the .NET Framework 4.8 launcher prerequisite,
and an extracted release bundle. The read-only ACL and temporary copy are removed
when the run ends. Hosts run with LOCALAPPDATA/APPDATA/TEMP inside the run's temp root,
so the user-directory fallbacks are exercised without touching the real profile.
The current user must not belong to the Siemens TIA Openness group. Pass the local SDK
root with --public-api-root or set TIA_MCP_TEST_PUBLIC_API_ROOT.

Examples:
  python scripts/checks/Test-RelocatedBundle.py --self-test
  python scripts/checks/Test-RelocatedBundle.py --bundle-root bin-build/release/TIA_MCP_Delivery_4.0.0
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from contextlib import contextmanager


ROOT = Path(__file__).resolve().parents[2]
RELEASE_KEYS = ("14sp1", "15.1", "16", "17", "18", "19", "20", "21")
FOUNDATION_KEYS = RELEASE_KEYS[:6]
PROTOCOL_VERSION = "2024-11-05"
OPENNESS_GROUP = "Siemens TIA Openness"
# PublicAPI folders below --public-api-root, as Run-ReleaseBuild passes them to the engine builds.
ENGINE_PUBLIC_API = {"20": ("TIA_V20_PublicAPI", "V20"), "21": ("TIA_V21_PublicAPI", "V21", "net48")}
resource_spec = importlib.util.spec_from_file_location("resource_discovery", Path(__file__).with_name("Test-ResourceDiscovery.py"))
resource_discovery = importlib.util.module_from_spec(resource_spec)
resource_spec.loader.exec_module(resource_discovery)


class CheckFailure(RuntimeError):
    pass


def is_within(path: Path, parent: Path) -> bool:
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def baseline_counts(repo: Path = ROOT) -> dict[str, int]:
    counts: dict[str, int] = {}
    for key in RELEASE_KEYS:
        path = repo / "manifest" / "contracts" / "v4" / "baseline" / f"{key}.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        tools = data.get("tools")
        capabilities = data.get("behaviorCapabilities")
        if not isinstance(tools, list) or not isinstance(capabilities, list):
            raise CheckFailure(f"Invalid V4 baseline shape: {path}")
        if any(row.get("state") != "current" or row.get("l5") != "NOT RUN" for row in capabilities):
            raise CheckFailure(f"V4 baseline has a switched or accepted family: {path}")
        if len({tool.get("name") for tool in tools}) != len(tools):
            raise CheckFailure(f"V4 baseline has duplicate tool names: {path}")
        counts[key] = len(tools)
    return counts


def product_executable(root: Path, key: str) -> Path:
    if key in ("20", "21"):
        return root / "runtime" / f"v{key}" / f"TiaMcp.Engine.V{key}.exe"
    return root / "runtime" / f"v{key}" / "TiaMcp.FoundationHost.exe"


def extract_v4_body(reply: dict) -> dict:
    result = reply.get("result")
    if not isinstance(result, dict):
        raise CheckFailure(f"MCP call did not return a result: {reply}")
    body = result.get("structuredContent")
    if body is None:
        content = [part.get("text") for part in result.get("content", []) if part.get("type") == "text"]
        if len(content) != 1:
            raise CheckFailure("MCP V4 result had no structuredContent or single text body")
        body = json.loads(content[0])
    if not isinstance(body, dict) or body.get("schemaVersion") != 4:
        raise CheckFailure(f"MCP result was not a V4 envelope: {body}")
    return body


class StdioSession:
    def __init__(self, command: list[str], env: dict[str, str], cwd: Path):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8",
                                        errors="replace", bufsize=1, cwd=str(cwd), env=env)
        self.output: queue.Queue[str | None] = queue.Queue()
        self.errors: list[str] = []
        self.sequence = 0
        self.reader = threading.Thread(target=self._read_stdout, daemon=True)
        self.error_reader = threading.Thread(target=self._read_stderr, daemon=True)
        self.reader.start()
        self.error_reader.start()

    def _read_stdout(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            self.output.put(line)
        self.output.put(None)

    def _read_stderr(self) -> None:
        assert self.process.stderr is not None
        for line in self.process.stderr:
            self.errors.append(line.rstrip())

    def request(self, method: str, params: dict | None = None, timeout: int = 60) -> dict | None:
        assert self.process.stdin is not None
        message: dict = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if method.startswith("notifications/"):
            self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
            self.process.stdin.flush()
            return None
        self.sequence += 1
        message["id"] = self.sequence
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                line = self.output.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                break
            if line is None:
                raise CheckFailure("MCP host closed STDIO: " + "\n".join(self.errors[-20:]))
            try:
                reply = json.loads(line)
            except json.JSONDecodeError:
                continue
            if reply.get("id") == self.sequence:
                if "error" in reply:
                    raise CheckFailure(f"MCP {method} failed: {reply['error']}")
                return reply
        raise CheckFailure(f"Timed out waiting for MCP {method}: " + "\n".join(self.errors[-20:]))

    def close(self) -> None:
        if self.process.poll() is None:
            try:
                self.request("notifications/exit")
            except Exception:
                pass
            if self.process.stdin:
                try:
                    self.process.stdin.close()
                except OSError:
                    pass
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.terminate()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=5)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream:
                try:
                    stream.close()
                except OSError:
                    pass

    def __enter__(self) -> "StdioSession":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


def foundation_stdio_command(root: Path, key: str) -> list[str]:
    exe = product_executable(root, key)
    return [str(exe), "--bundle-root", str(root), "--release-key", key, "--offline"]


def fake_engine_installations(public_api_root: Path, temp_root: Path) -> dict[str, Path]:
    if not public_api_root.is_dir():
        raise CheckFailure(f"PublicAPI SDK root does not exist: {public_api_root}")
    fixtures: dict[str, Path] = {}
    for key, api_parts in ENGINE_PUBLIC_API.items():
        source = public_api_root.joinpath(*api_parts)
        if not source.is_dir():
            raise CheckFailure(f"V{key} PublicAPI SDK directory is missing: {source}")
        install = temp_root / "sdk-only-fixtures" / f"Portal V{key}"
        public_api = install / "PublicAPI" / f"V{key}"
        public_api.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, public_api)
        fixtures[key] = install
    return fixtures


def in_openness_group() -> bool:
    result = subprocess.run(["whoami.exe", "/groups", "/fo", "csv", "/nh"], stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", check=False)
    return any(row and row[0].split("\\")[-1].strip().lower() == OPENNESS_GROUP.lower()
               for row in csv.reader(result.stdout.splitlines()))


def tia_installation_detected() -> bool:
    explicit = os.environ.get("TiaPortalLocation")
    if explicit and Path(explicit).is_dir():
        return True
    if os.name != "nt":
        return True
    import winreg

    base = r"SOFTWARE\Siemens\Automation\_InstalledSW"
    access = winreg.KEY_READ
    for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base, 0, access | view) as installed:
                index = 0
                while True:
                    try:
                        release = winreg.EnumKey(installed, index)
                    except OSError as error:
                        if getattr(error, "winerror", None) == 259:
                            break
                        raise CheckFailure("Cannot confirm that TIA is absent from the registry") from error
                    index += 1
                    if not release.upper().startswith("TIAP"):
                        continue
                    for section in ("TIA_Opns", "Global"):
                        try:
                            with winreg.OpenKey(installed, release + "\\" + section, 0, access) as entry:
                                path, _ = winreg.QueryValueEx(entry, "Path")
                            if path and Path(path).is_dir():
                                return True
                        except FileNotFoundError:
                            pass
        except FileNotFoundError:
            continue
        except PermissionError as error:
            raise CheckFailure("Cannot confirm that TIA is absent from the registry") from error
    return False


@contextmanager
def direct_engine_server(root: Path, key: str, installation: Path, temp_root: Path,
                         transport: str, isolate: bool):
    # Use only the locally copied SDK assemblies to resolve Openness types. These fixture folders
    # contain no TIA executable, never connect to a Portal/PLC, and live outside the read-only bundle.
    removed_names = ("TIA_MCP_DATA_DIRECTORY", "TIA_MCP_DIAGNOSTICS_DIRECTORY", "TIA_MCP_BUNDLE_ROOT")
    saved = {name: os.environ.pop(name, None) for name in removed_names}
    try:
        with resource_discovery.server(product_executable(root, key), installation, int(key), transport, "full",
                isolate=isolate, env_overrides={
                    "TIA_MCP_BUNDLE_ROOT": str(root), "TEMP": str(temp_root), "TMP": str(temp_root),
                    "LOCALAPPDATA": str(temp_root / "profile" / "local"),
                    "APPDATA": str(temp_root / "profile" / "roaming"),
                }) as session:
            yield session
    finally:
        for name, value in saved.items():
            if value is not None:
                os.environ[name] = value
            else:
                os.environ.pop(name, None)


def stdio_environment(temp_root: Path, bundle_root: Path | None = None) -> dict[str, str]:
    env = os.environ.copy()
    for name in ("TIA_MCP_DATA_DIRECTORY", "TIA_MCP_DIAGNOSTICS_DIRECTORY", "TIA_MCP_BUNDLE_ROOT"):
        env.pop(name, None)
    env["TEMP"] = str(temp_root)
    env["TMP"] = str(temp_root)
    # An isolated profile: read-only-install fallbacks land here, never in the real user folders.
    env["LOCALAPPDATA"] = str(temp_root / "profile" / "local")
    env["APPDATA"] = str(temp_root / "profile" / "roaming")
    if bundle_root is not None:
        env["TIA_MCP_BUNDLE_ROOT"] = str(bundle_root)
    return env


def enumerate_tools(root: Path, key: str, expected: int, temp_root: Path) -> int:
    with StdioSession(foundation_stdio_command(root, key), stdio_environment(temp_root), root) as session:
        initialized = session.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "p6-42-relocation-check", "version": "1"},
        })
        if not initialized or not isinstance(initialized.get("result"), dict):
            raise CheckFailure(f"{key}: MCP initialize failed")
        session.request("notifications/initialized", {})
        tools: list[dict] = []
        cursor = None
        seen: set[str] = set()
        while True:
            params = {} if cursor is None else {"cursor": cursor}
            reply = session.request("tools/list", params)
            assert reply is not None
            page = reply.get("result", {}).get("tools")
            if not isinstance(page, list):
                raise CheckFailure(f"{key}: tools/list response has no tools array")
            tools.extend(page)
            cursor = reply.get("result", {}).get("nextCursor")
            if cursor is None:
                break
            if cursor in seen:
                raise CheckFailure(f"{key}: tools/list repeated a cursor")
            seen.add(cursor)
        names = [tool.get("name") for tool in tools]
        if len(names) != len(set(names)):
            raise CheckFailure(f"{key}: tools/list contains duplicate names")
        if len(tools) != expected:
            raise CheckFailure(f"{key}: tools/list count {len(tools)} != baseline {expected}")
        return len(tools)


def check_engine_startup(root: Path, key: str, expected: int, temp_root: Path,
                         installation: Path, transport: str, isolate: bool) -> int:
    mode = "isolation-on" if isolate else "build-default-off"
    with direct_engine_server(root, key, installation, temp_root, transport, isolate) as (rpc, _http, logs):
        initialized = rpc("initialize", "initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "p6-54-relocation-check", "version": "1"},
        })
        if not initialized or not isinstance(initialized.get("result"), dict):
            raise CheckFailure(f"V{key} {transport} {mode}: MCP initialize failed: {logs[-10:]}")
        rpc("notifications/initialized", notification=True)
        roster: list[dict] = []
        cursor = None
        seen: set[str] = set()
        while True:
            reply = rpc("tools/list", "list" if cursor is None else "list-" + cursor,
                        {} if cursor is None else {"cursor": cursor})
            page = reply.get("result", {}).get("tools")
            if not isinstance(page, list):
                raise CheckFailure(f"V{key}: tools/list returned no array")
            roster.extend(page)
            cursor = reply.get("result", {}).get("nextCursor")
            if cursor is None:
                break
            if cursor in seen:
                raise CheckFailure(f"V{key}: tools/list repeated a cursor")
            seen.add(cursor)
        names = [tool.get("name") for tool in roster]
        if len(names) != len(set(names)) or len(names) != expected:
            raise CheckFailure(f"V{key} {transport} {mode}: tool roster {len(names)} != {expected}")

        next_id = 0
        def call(name: str, arguments: dict | None = None) -> dict:
            nonlocal next_id
            next_id += 1
            reply = rpc("tools/call", f"call-{next_id}", {"name": name, "arguments": arguments or {}})
            return extract_v4_body(reply)

        status = call("GetOpennessWorkerStatus")["data"]["evidence"]["worker"]
        if status.get("enabled") is not isolate or status.get("enabledByDefault") is not False:
            raise CheckFailure(f"V{key} {transport} {mode}: unexpected worker default/state: {status}")
        bootstrap = call("InitializeEnvironment")
        reason = bootstrap.get("data", {}).get("recommendedReason") or ""
        if bootstrap.get("data", {}).get("ready") is not False or not reason:
            raise CheckFailure(f"V{key} {transport} {mode}: Bootstrap omitted cause/fix: {bootstrap}")
        doctor = call("GetEnvironmentDiagnostics", {"fix": False})
        doctor_checks = doctor.get("data", {}).get("checks", [])
        if not any(OPENNESS_GROUP in json.dumps(check, ensure_ascii=False) for check in doctor_checks):
            raise CheckFailure(f"V{key} {transport} {mode}: environment doctor omitted the Openness cause/fix: {doctor}")
        if not any(check.get("fix") for check in doctor_checks if isinstance(check, dict)):
            raise CheckFailure(f"V{key} {transport} {mode}: environment doctor omitted repair steps: {doctor}")
        diagnostic = call("GetSessionState")
        if diagnostic.get("ok") is not True:
            raise CheckFailure(f"V{key} {transport} {mode}: read-only session diagnostic stopped: {diagnostic}")

        refusal = call("SaveProject")
        if (refusal.get("error", {}).get("code") != "RESOURCE_UNAVAILABLE"
                or refusal.get("error", {}).get("details", {}).get("resource") != "tia-openness-environment"
                or refusal.get("meta", {}).get("outcome") != "rejected-before-operation"
                or refusal.get("meta", {}).get("execution") != "not-started"):
            raise CheckFailure(f"V{key} {transport} {mode}: TIA write was not refused before dispatch: {refusal}")
        evidence = refusal.get("data", {}).get("environment", {})
        if not evidence.get("cause") or not evidence.get("recommendedFix"):
            raise CheckFailure(f"V{key} {transport} {mode}: refusal omitted cause/fix: {refusal}")
        status = call("GetOpennessWorkerStatus")["data"]["evidence"]["worker"]
        if status.get("environmentReady") is not False or not status.get("environmentCause"):
            raise CheckFailure(f"V{key} {transport} {mode}: status did not retain environment-not-ready state: {status}")
        return len(roster)


def stage_packaged_engine_without_siemens(root: Path, key: str, temp_root: Path) -> Path:
    source = product_executable(root, key).parent
    runtime = temp_root / f"packaged-no-tia-v{key}" / "runtime" / f"v{key}"

    def omit_siemens(_directory: str, names: list[str]) -> set[str]:
        return {name for name in names
                if name.lower().startswith("siemens.engineering") and name.lower().endswith(".dll")}

    if not runtime.exists():
        shutil.copytree(source, runtime, ignore=omit_siemens)
    remaining = [path for path in runtime.rglob("*.dll")
                 if path.name.lower().startswith("siemens.engineering")]
    if remaining:
        raise CheckFailure(f"Packaged test layout retained Siemens.Engineering assemblies: {remaining[:3]}")
    exe = runtime / product_executable(root, key).name
    if not exe.is_file():
        raise CheckFailure(f"Staged V{key} packaged engine is missing: {exe}")
    return exe


def check_packaged_no_tia(root: Path, key: str, expected: int, temp_root: Path,
                          transport: str, isolate: bool) -> int:
    mode = "isolation-on" if isolate else "isolation-off"
    exe = stage_packaged_engine_without_siemens(root, key, temp_root)
    data = temp_root / f"packaged-no-tia-data-v{key}-{transport}-{mode}"
    data.mkdir(parents=True, exist_ok=True)
    local = temp_root / "profile" / "local"
    roaming = temp_root / "profile" / "roaming"
    local.mkdir(parents=True, exist_ok=True)
    roaming.mkdir(parents=True, exist_ok=True)
    overrides = {
        "TiaPortalLocation": "",
        "TIA_MCP_DATA_DIRECTORY": str(data),
        "TIA_MCP_BUNDLE_ROOT": str(exe.parents[2]),
        "TEMP": str(temp_root), "TMP": str(temp_root),
        "LOCALAPPDATA": str(local), "APPDATA": str(roaming),
    }
    with resource_discovery.server(exe, None, int(key), transport, "full",
            isolate=isolate, env_overrides=overrides) as (rpc, _http, logs):
        initialized = rpc("initialize", "initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "p6-54b-packaged-no-tia", "version": "1"},
        })
        if not initialized or not isinstance(initialized.get("result"), dict):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: initialize failed: {logs[-10:]}")
        rpc("notifications/initialized", notification=True)
        roster: list[dict] = []
        cursor = None
        while True:
            reply = rpc("tools/list", "roster" if cursor is None else "roster-" + cursor,
                        {} if cursor is None else {"cursor": cursor})
            page = reply.get("result", {}).get("tools")
            if not isinstance(page, list):
                raise CheckFailure(f"V{key} packaged {transport} {mode}: tools/list returned no tools")
            roster.extend(page)
            cursor = reply.get("result", {}).get("nextCursor")
            if cursor is None:
                break
        names = [tool.get("name") for tool in roster]
        if len(names) != expected or len(names) != len(set(names)):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: tool roster {len(names)} != {expected}")

        def call(name: str, arguments: dict | None = None) -> dict:
            return extract_v4_body(rpc("tools/call", "call-" + name,
                {"name": name, "arguments": arguments or {}}))

        bootstrap = call("InitializeEnvironment")
        if (bootstrap.get("data", {}).get("ready") is not False
                or "no TIA Portal V" not in bootstrap.get("data", {}).get("recommendedReason", "")):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: InitializeEnvironment omitted no-TIA readiness: {bootstrap}")
        doctor = call("GetEnvironmentDiagnostics", {"fix": False})
        if (doctor.get("data", {}).get("ready") is not False
                or "no TIA Portal V" not in json.dumps(doctor.get("data", {}), ensure_ascii=False)):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: diagnostics omitted no-TIA readiness: {doctor}")
        refusal = call("GetSessionState")
        if (refusal.get("error", {}).get("code") != "RESOURCE_UNAVAILABLE"
                or refusal.get("error", {}).get("details", {}).get("resource") != "tia-openness-environment"
                or refusal.get("meta", {}).get("outcome") != "rejected-before-operation"
                or refusal.get("meta", {}).get("execution") != "not-started"):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: GetSessionState was not refused before dispatch: {refusal}")
        worker_refusal = call("RestartOpennessWorker")
        if (worker_refusal.get("error", {}).get("code") != "RESOURCE_UNAVAILABLE"
                or worker_refusal.get("error", {}).get("details", {}).get("resource") != "tia-openness-environment"
                or worker_refusal.get("meta", {}).get("execution") != "not-started"):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: worker control was not refused before dispatch: {worker_refusal}")
        discovery = call("FindTools", {"query": "BuildPlcUdt", "limit": 1})
        if not isinstance(discovery.get("data", {}).get("items"), list):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: local discovery failed: {discovery}")
        usage = call("GetToolUsage", {"toolName": "BuildPlcUdt"})
        build_arguments = usage.get("data", {}).get("example", {}).get("request", {}).get("params", {}).get("arguments")
        if not isinstance(build_arguments, dict):
            raise CheckFailure(f"V{key} packaged {transport} {mode}: local usage example was unavailable: {usage}")
        built = call("BuildPlcUdt", build_arguments)
        if built.get("ok") is not True:
            raise CheckFailure(f"V{key} packaged {transport} {mode}: offline builder failed: {built}")
        if isolate:
            worker = call("GetOpennessWorkerStatus").get("data", {}).get("evidence", {}).get("worker", {})
            if worker.get("state") != "NotStarted":
                raise CheckFailure(f"V{key} packaged {transport} {mode}: a safe local call started the worker: {worker}")
        return len(roster)


def find_user_paths(temp_root: Path) -> dict[str, Path]:
    local_app_data = temp_root / "profile" / "local"
    return {
        "logs": temp_root / "TiaMcp" / "logs",
        "config": local_app_data / "TiaPortalMcp",
        "audit": local_app_data / "TiaMcp" / "logs" / "audit",
    }


def approval_settings_state(config_path: Path) -> str:
    settings = config_path / "approval.settings"
    if not settings.exists():
        return "absent-default"
    try:
        lines = settings.read_text(encoding="utf-8").splitlines()
    except OSError:
        return "unreadable-default"
    if lines and lines[0] == "enabled=false":
        return "explicit-disabled"
    if lines and lines[0] == "enabled=true":
        return "explicit-enabled"
    return "invalid-default"


def write_approval_probe(root: Path, temp_root: Path, installation: Path, expected_enabled: bool) -> str:
    if not expected_enabled:
        raise CheckFailure("Approval probe was requested while user settings explicitly disable approvals")
    key = "21"
    before_files = {}
    paths = find_user_paths(temp_root)
    audit = paths["audit"]
    if audit.exists():
        before_files = {p.name: (p.stat().st_size, p.stat().st_mtime_ns) for p in audit.glob("audit-*.jsonl")}
    with direct_engine_server(root, key, installation, temp_root, "stdio", False) as (rpc, _http, logs):
        rpc("initialize", "initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "p6-54-approval-check", "version": "1"},
        })
        rpc("notifications/initialized", notification=True)
        # The readiness admission guard must stop this TIA write before dispatch on this
        # no-TIA machine. It still exercises the production engine's user config/audit bootstrap.
        result = rpc("tools/call", "approval-probe", {
            "name": "CreateDevice",
            "arguments": {"orderNumber": "6ES7 515-2AM02-0AB0", "version": "V2.9", "deviceName": "PLC_2"},
        })
        body = extract_v4_body(result)
        if (body.get("error", {}).get("code") != "RESOURCE_UNAVAILABLE"
                or body.get("error", {}).get("details", {}).get("resource") != "tia-openness-environment"
                or (body.get("meta") or {}).get("outcome") != "rejected-before-operation"
                or (body.get("meta") or {}).get("execution") != "not-started"):
            raise CheckFailure(f"No-TIA approval probe did not refuse before native dispatch: {body}; logs={logs[-10:]}")
    if not audit.is_dir():
        raise CheckFailure(f"Approval probe did not create the user audit fallback: {audit}")
    changed = []
    for path in audit.glob("audit-*.jsonl"):
        signature = (path.stat().st_size, path.stat().st_mtime_ns)
        if before_files.get(path.name) != signature:
            changed.append(path)
    if not changed:
        raise CheckFailure("Approval probe did not append a user-fallback audit event")
    matching = False
    for path in changed:
        for line in path.read_text(encoding="utf-8").splitlines():
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if row.get("release") == key and row.get("tool") == "CreateDevice":
                matching = True
                break
    if not matching:
            raise CheckFailure("Audit fallback has no CreateDevice/V21 admission evidence")
    config_lock = paths["config"] / "approval.settings.lock"
    if not config_lock.is_file():
        raise CheckFailure(f"Approval settings did not resolve to the user config fallback: {config_lock}")
    return "configured-on" if approval_settings_state(paths["config"]) == "explicit-enabled" else "default-on"


def file_inventory(root: Path) -> dict[str, str]:
    inventory = {}
    for path in root.rglob("*"):
        if path.is_dir():
            inventory["D:" + path.relative_to(root).as_posix()] = ""
        elif path.is_file():
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
            inventory["F:" + path.relative_to(root).as_posix()] = digest.hexdigest()
    return inventory


def icacls(executable: str, *args: str) -> subprocess.CompletedProcess:
    result = subprocess.run([executable, *args], text=True, encoding="utf-8", errors="replace",
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
    if result.returncode != 0:
        raise CheckFailure(f"icacls {' '.join(args)} exited {result.returncode}: {result.stdout[-2000:]}")
    return result


def current_user_sid() -> str:
    result = subprocess.run(["whoami.exe", "/user", "/fo", "csv", "/nh"], check=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            encoding="utf-8", errors="replace")
    row = next(csv.reader(result.stdout.splitlines()))
    if len(row) < 2 or not row[1].startswith("S-"):
        raise CheckFailure("Could not read the current Windows user SID")
    return row[1]


def require_read_only(root: Path, icacls_exe: str, sid: str) -> None:
    # Inheritance flags come first, and only the write-type rights are denied: the generic W right includes
    # SYNCHRONIZE/READ_CONTROL and would block reads. Inheritance reaches existing children without /T.
    icacls(icacls_exe, str(root), "/deny", f"*{sid}:(OI)(CI)(WD,AD,WEA,WA,DC,DE)", "/C")
    probe = root / f".p6-42-write-probe-{uuid.uuid4().hex}"
    try:
        try:
            with probe.open("xb") as stream:
                stream.write(b"must-not-be-created")
        except PermissionError:
            return
        except OSError as error:
            if getattr(error, "winerror", None) in (5, 19, 1314):
                return
            raise CheckFailure(f"Could not confirm read-only install ACL: {error}") from error
        raise CheckFailure("Read-only install ACL still permits writes to the bundle root")
    finally:
        if probe.exists():
            probe.unlink()


def remove_read_only(root: Path, icacls_exe: str, sid: str) -> None:
    icacls(icacls_exe, str(root), "/remove:d", f"*{sid}", "/T", "/C")


def build_bundle_probe(dotnet: str, work: Path) -> Path:
    project = work / "BundleProbe.csproj"
    config = work / "NuGet.Config"
    source = work / "Program.cs"
    project.write_text("""<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <AssemblyName>TiaOpenness</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AppHostRelativeDotNet>../dotnet</AppHostRelativeDotNet>
    <AppHostDotNetSearch>AppRelative;EnvironmentVariable;Global</AppHostDotNetSearch>
  </PropertyGroup>
</Project>
""", encoding="utf-8")
    config.write_text("<configuration><packageSources><clear /></packageSources></configuration>", encoding="utf-8")
    source.write_text(r'''using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--launcher-target")
{
    var marker = Environment.GetEnvironmentVariable("P6_42_LAUNCHER_MARKER");
    if (String.IsNullOrWhiteSpace(marker)) return 11;
    File.WriteAllText(marker, JsonSerializer.Serialize(new { baseDirectory = AppContext.BaseDirectory,
        currentDirectory = Environment.CurrentDirectory, arguments = args }));
    return 0;
}

if (args.Length != 4) return 12;
var mode = args[0]; var assemblyPath = Path.GetFullPath(args[1]);
var root = Path.GetFullPath(args[2]); var releaseKey = args[3];
var productDirectory = Path.GetDirectoryName(assemblyPath)!;
// The probe must be named TiaOpenness for the launcher check, so the product's TiaOpenness.dll is loaded into its own
// context; the default context already holds an assembly with that simple name.
var gui = new ProductLoadContext(productDirectory).LoadFromAssemblyPath(assemblyPath);
try
{
    if (mode == "--update-source")
    {
        var type = gui.GetType("TiaMcpConfigurator.UpdateCheck", throwOnError: true)!;
        var method = type.GetMethod("Launch", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(string), typeof(int) }, modifiers: null)!;
        _ = method.Invoke(null, new object[] { root, 1 });
        Console.Error.WriteLine("UpdateCheck.Launch unexpectedly accepted a source checkout");
        return 20;
    }
    if (mode == "--missing-engine")
    {
        var type = gui.GetType("TiaMcpConfigurator.ConfigCore", throwOnError: true)!;
        var method = type.GetMethod("Engine", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(string), typeof(string), typeof(string) }, modifiers: null)!;
        _ = method.Invoke(null, new object[] { root, releaseKey, productDirectory });
        Console.Error.WriteLine("ConfigCore.Engine unexpectedly resolved a missing release engine");
        return 21;
    }
    return 22;
}
catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException && mode == "--update-source")
{
    Console.WriteLine("UPDATE_REFUSED_SOURCE_CHECKOUT"); return 0;
}
catch (TargetInvocationException error) when (error.InnerException is FileNotFoundException && mode == "--missing-engine")
{
    Console.WriteLine("MISSING_ENGINE_REFUSED"); return 0;
}

sealed class ProductLoadContext(string directory) : AssemblyLoadContext("product")
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (String.IsNullOrEmpty(name.Name) || name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var folder in new[] { directory, Path.Combine(directory, "bridge") })
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;
    }
}
''', encoding="utf-8")
    restored = subprocess.run([dotnet, "restore", str(project), "--configfile", str(config),
                               "-p:NuGetAudit=false"], cwd=work, text=True, encoding="utf-8",
                              errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if restored.returncode != 0:
        raise CheckFailure(f"dotnet restore BundleProbe failed ({restored.returncode}): {restored.stdout[-3000:]}")
    built = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--no-restore",
                            "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false"], cwd=work,
                           text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT)
    if built.returncode != 0:
        raise CheckFailure(f"dotnet build BundleProbe failed ({built.returncode}): {built.stdout[-3000:]}")
    output = work / "bin" / "Release" / "net10.0-windows"
    if not (output / "TiaOpenness.exe").is_file():
        raise CheckFailure(f"Launcher probe apphost missing: {output / 'TiaOpenness.exe'}")
    return output


def invoke_reflection_probe(bundle_root: Path, dotnet_runtime: Path, probe_dll: Path,
                            gui_assembly: Path, mode: str, key: str) -> str:
    result = subprocess.run([str(dotnet_runtime), str(probe_dll), mode, str(gui_assembly),
                             str(bundle_root), key], cwd=gui_assembly.parent,
                            text=True, encoding="utf-8", errors="replace", timeout=45,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    expected = "UPDATE_REFUSED_SOURCE_CHECKOUT" if mode == "--update-source" else "MISSING_ENGINE_REFUSED"
    if result.returncode != 0 or expected not in result.stdout:
        raise CheckFailure(f"{mode} probe failed ({result.returncode}): {result.stdout[-3000:]}")
    return expected


def invoke_updater_source_refusal(updater: Path, source_root: Path) -> str:
    result = subprocess.run([str(updater), "-InstallRoot", str(source_root), "-Check"],
                            cwd=updater.parent, text=True, encoding="utf-8", errors="replace",
                            timeout=20, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode == 0 or "source checkout" not in result.stdout.lower():
        raise CheckFailure(f"Updater did not refuse a source checkout (exit={result.returncode}): {result.stdout[-2000:]}")
    return result.stdout.strip()


def root_launcher_probe(bundle_root: Path, probe_output: Path, launcher: Path) -> str:
    marker = bundle_root.parent / f"launcher-target-{uuid.uuid4().hex}.json"
    studio = bundle_root / "runtime" / "studio"
    target_files = ["TiaOpenness.exe", "TiaOpenness.dll", "TiaOpenness.deps.json", "TiaOpenness.runtimeconfig.json"]
    backups: dict[str, bytes | None] = {}
    try:
        for name in target_files:
            path = studio / name
            backups[name] = path.read_bytes() if path.exists() else None
            source = probe_output / name
            if not source.is_file():
                raise CheckFailure(f"Launcher sentinel output missing: {source}")
            shutil.copy2(source, path)
        env = os.environ.copy()
        env["P6_42_LAUNCHER_MARKER"] = str(marker)
        started = subprocess.run([str(launcher), "--launcher-target", str(marker)], cwd=bundle_root,
                                 env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                 stderr=subprocess.STDOUT, timeout=30, check=False)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and not marker.is_file():
            time.sleep(0.1)
        if not marker.is_file():
            raise CheckFailure(f"Root launcher did not start runtime/studio/TiaOpenness.exe; exit={started.returncode}, output={started.stdout[-1000:]!r}")
        data = json.loads(marker.read_text(encoding="utf-8"))
        if Path(data["currentDirectory"]).resolve() != bundle_root.resolve():
            raise CheckFailure(f"Root launcher target working directory was not the bundle root: {data}")
        if data["arguments"] != ["--launcher-target", str(marker)]:
            raise CheckFailure(f"Root launcher did not pass its arguments intact: {data}")
        return str(data["baseDirectory"])
    finally:
        # The launcher returns before its target exits; the target keeps its image locked for a moment after writing
        # the marker, so restoring the Studio files retries briefly instead of failing on a sharing violation.
        deadline = time.monotonic() + 20
        for name, content in backups.items():
            target = studio / name
            while True:
                try:
                    if content is None:
                        target.unlink(missing_ok=True)
                    else:
                        target.write_bytes(content)
                    break
                except PermissionError:
                    if time.monotonic() > deadline:
                        raise
                    time.sleep(0.2)
        marker.unlink(missing_ok=True)


def invalid_root_checks(root: Path, temp_root: Path) -> int:
    invalid = root.parent / f"missing explicit root {uuid.uuid4().hex}"
    env = stdio_environment(temp_root, root)
    total = 0
    for key in RELEASE_KEYS:
        command = [str(product_executable(root, key)), "--bundle-root", str(invalid)]
        if key in FOUNDATION_KEYS:
            command += ["--release-key", key, "--offline"]
        result = subprocess.run(command, cwd=root, env=env, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=35,
                                text=True, encoding="utf-8", errors="replace")
        if result.returncode == 0:
            raise CheckFailure(f"{key}: invalid explicit --bundle-root fell back to the valid environment root")
        if "RESOURCE_UNAVAILABLE" not in result.stdout:
            raise CheckFailure(f"{key}: explicit root failed for an unexpected reason: {result.stdout[-1500:]}")
        total += 1
    return total


def verify_fallback_logs(root: Path, keys: tuple[str, ...], temp_root: Path) -> None:
    for key in keys:
        folder = temp_root / "TiaMcp" / "logs" / key
        logs = list(folder.glob("TiaMcpServer-*.log")) if folder.is_dir() else []
        if not logs:
            raise CheckFailure(f"{key}: expected host log in user temp fallback {folder}")


def framework48_available() -> bool:
    if os.name != "nt":
        return False
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
            r"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", 0,
            winreg.KEY_READ | winreg.KEY_WOW64_32KEY)
        with key:
            release, _ = winreg.QueryValueEx(key, "Release")
        return isinstance(release, int) and release >= 528040
    except (OSError, ImportError):
        return False


def run_bundle_check(args: argparse.Namespace) -> int:
    if os.name != "nt":
        raise CheckFailure("Relocation/install checks require Windows")
    bundle = args.bundle_root.resolve()
    if not bundle.is_dir() or not (bundle / "manifest" / "package-manifest.json").is_file():
        raise CheckFailure(f"Not an extracted release package root: {bundle}")
    counts = baseline_counts()
    source_launcher = bundle / "TiaOpenness.exe"
    gui_assembly = bundle / "runtime" / "studio" / "TiaOpenness.dll"
    if not source_launcher.is_file() or not gui_assembly.is_file():
        raise CheckFailure("Bundle is missing root launcher or Studio assembly")
    for key in RELEASE_KEYS:
        if not product_executable(bundle, key).is_file():
            raise CheckFailure(f"Bundle product missing for {key}: {product_executable(bundle, key)}")

    external_parent = (args.relocation_parent or Path(tempfile.gettempdir())).resolve()
    external_parent.mkdir(parents=True, exist_ok=True)
    if is_within(external_parent, ROOT):
        raise CheckFailure("Relocation parent must be outside the source checkout")
    destination = external_parent / f"TIA MCP 4.0 中文 {uuid.uuid4().hex[:10]}"
    print(f"Copying {bundle} to {destination}")
    shutil.copytree(bundle, destination)
    sid = current_user_sid()
    icacls_exe = shutil.which("icacls.exe") or shutil.which("icacls")
    if not icacls_exe:
        raise CheckFailure("icacls.exe is required for the read-only install check")
    acl_applied = False
    probe_work = external_parent / f"p6-42-probe-build-{uuid.uuid4().hex}"
    user_temp = external_parent / f"p6-42-user-temp-{uuid.uuid4().hex}"
    probe_work.mkdir()
    user_temp.mkdir()
    try:
        probe_output = build_bundle_probe(args.dotnet, probe_work)
        if framework48_available():
            target = root_launcher_probe(destination, probe_output, destination / "TiaOpenness.exe")
            print(f"PASS root launcher selected runtime/studio/TiaOpenness.exe; target base={target}")
        else:
            raise CheckFailure("Root launcher target check requires installed .NET Framework 4.8 or later")

        gui_copy = destination / "runtime" / "studio" / "TiaOpenness.dll"
        missing_engine = product_executable(destination, "21")
        missing_saved = missing_engine.with_name(missing_engine.name + ".p6-42-hidden")
        if missing_saved.exists():
            raise CheckFailure(f"Stale missing-engine probe file exists: {missing_saved}")
        missing_engine.rename(missing_saved)
        try:
            marker = invoke_reflection_probe(destination, destination / "runtime" / "dotnet" / "dotnet.exe",
                                             probe_output / "TiaOpenness.dll", gui_copy, "--missing-engine", "21")
            print(f"PASS selected V21 engine missing: {marker}")
        finally:
            missing_saved.rename(missing_engine)
        refusal = invoke_updater_source_refusal(destination / "runtime" / "tools" / "TiaMcp.Updater.exe", ROOT)
        print(f"PASS updater refused source checkout: {refusal}")

        before = file_inventory(destination)
        acl_applied = True
        require_read_only(destination, icacls_exe, sid)
        ro_probe = destination / f".p6-42-read-only-{uuid.uuid4().hex}"
        if ro_probe.exists():
            raise CheckFailure("Unexpected read-only probe file exists")
        print("PASS bundle root denies writes for the current user")

        if in_openness_group():
            raise CheckFailure("This check requires a user outside the Siemens TIA Openness group")
        public_api_root = args.public_api_root.resolve()
        installations = fake_engine_installations(public_api_root, user_temp)
        results = {}
        for key in RELEASE_KEYS:
            count = enumerate_tools(destination, key, counts[key], user_temp)
            results[key] = count
            print(f"PASS {key}: STDIO tools/list {count}/{counts[key]}")
        engine_modes = {}
        for key in ("20", "21"):
            engine_modes[key] = []
            for transport in ("stdio", "http"):
                for isolate in (False, True):
                    count = check_engine_startup(destination, key, counts[key], user_temp,
                                                 installations[key], transport, isolate)
                    engine_modes[key].append(f"{transport}/{'isolation-on' if isolate else 'default-off'}={count}")
                    print(f"PASS V{key}: real EXE {transport}, isolation={'on' if isolate else 'default-off'}, tools/list {count}")
        packaged_modes = {}
        if tia_installation_detected():
            print("SKIP packaged no-TIA matrix: a TIA installation path is present")
        else:
            for key in ("20", "21"):
                packaged_modes[key] = []
                for transport in ("stdio", "http"):
                    for isolate in (False, True):
                        count = check_packaged_no_tia(destination, key, counts[key], user_temp, transport, isolate)
                        mode = f"{transport}/{'isolation-on' if isolate else 'isolation-off'}={count}"
                        packaged_modes[key].append(mode)
                        print(f"PASS V{key}: packaged no-TIA {transport}, isolation={'on' if isolate else 'off'}, tools/list {count}")
        invalid_count = invalid_root_checks(destination, user_temp)
        print(f"PASS invalid explicit --bundle-root refused without fallback: {invalid_count}/{len(RELEASE_KEYS)} release keys")

        config_path = find_user_paths(user_temp)["config"]
        settings = approval_settings_state(config_path)
        if settings == "explicit-disabled":
            approval = "SKIP (existing approval.settings disables approvals; left user preference intact)"
        else:
            approval = write_approval_probe(destination, user_temp, installations["21"], True)
            print(f"PASS default-on write rejection and audit/config fallback: {approval}")
        verify_fallback_logs(destination, RELEASE_KEYS, user_temp)
        print("PASS logs resolved under the per-release user temp fallback")
        after = file_inventory(destination)
        if before != after:
            added = sorted(set(after) - set(before))
            removed = sorted(set(before) - set(after))
            changed = sorted(name for name in set(before) & set(after) if before[name] != after[name])
            raise CheckFailure(f"Read-only bundle file inventory changed: added={added[:10]} removed={removed[:10]} changed={changed[:10]}")
        print("PASS bundle file inventory unchanged")
        print("\nRESULT: relocation/install checks passed")
        print(f"bundle={destination}")
        print(f"tools={json.dumps(results, sort_keys=True)}")
        print(f"engineModes={json.dumps(engine_modes, sort_keys=True)}")
        print(f"packagedNoTiaModes={json.dumps(packaged_modes, sort_keys=True)}")
        print(f"invalidRoots={invalid_count}/{len(RELEASE_KEYS)} approval={approval}")
        return 0
    finally:
        if acl_applied:
            try:
                remove_read_only(destination, icacls_exe, sid)
            except Exception as error:
                print(f"WARNING: could not remove temporary read-only ACL; preserve location for recovery: {destination}; {error}", file=sys.stderr)
        if args.keep_relocation:
            print(f"Keeping relocation copy: {destination}")
        else:
            shutil.rmtree(destination, ignore_errors=True)
        shutil.rmtree(probe_work, ignore_errors=True)
        shutil.rmtree(user_temp, ignore_errors=True)


class RelocationCheckTests(unittest.TestCase):
    def test_baseline_rosters_and_deferred_policy(self):
        counts = baseline_counts()
        self.assertEqual(counts, {"14sp1": 59, "15.1": 60, "16": 62, "17": 62,
                                  "18": 62, "19": 64, "20": 477, "21": 488})

    def test_product_paths(self):
        root = Path("X:/candidate")
        self.assertEqual(product_executable(root, "19"), root / "runtime/v19/TiaMcp.FoundationHost.exe")
        self.assertEqual(product_executable(root, "21"), root / "runtime/v21/TiaMcp.Engine.V21.exe")

    def test_v4_reply_envelope(self):
        value = {"schemaVersion": 4, "ok": False, "data": None,
                 "error": {"code": "CONFIRMATION_REQUIRED", "details": {"reason": "workbench-unavailable"}},
                 "meta": {"outcome": "rejected-before-operation", "execution": "not-started"}}
        self.assertEqual(extract_v4_body({"result": {"structuredContent": value}}), value)
        self.assertEqual(extract_v4_body({"result": {"content": [{"type": "text", "text": json.dumps(value)}]}}), value)

    def test_stdio_request_and_notification(self):
        server = (
            "import json,sys\n"
            "for line in sys.stdin:\n"
            " m=json.loads(line)\n"
            " if 'id' in m: print(json.dumps({'jsonrpc':'2.0','id':m['id'],'result':{'tools':[{'name':'Probe'}]}}), flush=True)\n"
        )
        with StdioSession([sys.executable, "-u", "-c", server], os.environ.copy(), ROOT) as session:
            reply = session.request("tools/list", {})
            self.assertEqual(reply["result"]["tools"][0]["name"], "Probe")
            self.assertIsNone(session.request("notifications/initialized", {}))

    def test_external_path_validation(self):
        parent = Path("D:/p6-42-path-test")
        self.assertTrue(is_within(parent / "child", parent))
        self.assertFalse(is_within(Path("D:/outside"), parent))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--bundle-root", type=Path)
    parser.add_argument("--relocation-parent", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--keep-relocation", action="store_true")
    parser.add_argument("--public-api-root", type=Path,
                        default=Path(os.environ.get("TIA_MCP_TEST_PUBLIC_API_ROOT", "")) if os.environ.get("TIA_MCP_TEST_PUBLIC_API_ROOT") else None,
                        help="SDK root holding TIA_V20_PublicAPI and TIA_V21_PublicAPI (or TIA_MCP_TEST_PUBLIC_API_ROOT)")
    args = parser.parse_args()
    if args.self_test:
        result = unittest.main(argv=[sys.argv[0]], exit=False, verbosity=2)
        return 0 if result.result.wasSuccessful() else 1
    if args.bundle_root is None:
        parser.error("--bundle-root is required unless --self-test is used")
    if args.public_api_root is None:
        parser.error("--public-api-root or TIA_MCP_TEST_PUBLIC_API_ROOT is required")
    try:
        return run_bundle_check(args)
    except (CheckFailure, OSError, subprocess.SubprocessError, json.JSONDecodeError) as error:
        print(f"[FAIL] {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
