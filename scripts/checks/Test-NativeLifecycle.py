"""Opt-in supervisor for the standalone, owned-project net48 native smoke tests.

Default and --self-test never load Openness. A timeout kills only the test runner,
not TIA: inspect owned PID journal entries before manually cleaning up leftovers.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path


def read_evidence(native: Path, major: int, iterations: int) -> dict:
    report_path = native / "native-result.json"
    if report_path.stat().st_size > 65536:
        raise ValueError("Native report exceeds size limit")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    expected = {"schemaVersion": 1, "status": "PASSED", "tiaMajor": major,
                "iterations": iterations, "completedCycles": iterations,
                "assertions": 7 * iterations, "nativeExecuted": True,
                "scratchRetained": False, "failure": None}
    for key, value in expected.items():
        if type(report.get(key)) is not type(value) or report[key] != value:
            raise ValueError(f"Native result incomplete: {key}")
    events = native / "native-events.jsonl"
    if events.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("Native journal exceeds size limit")
    pending = None
    ids, owned, stages = set(), [], {}
    for line in events.read_text(encoding="utf-8").splitlines():
        event = json.loads(line)
        phase, stage, detail = event["phase"], event["stage"], event["detail"]
        if phase == "OWNED_PORTAL":
            if detail["cycle"] != len(owned) or type(detail["pid"]) is not int or detail["pid"] <= 0:
                raise ValueError("Invalid owned portal record")
            owned.append(detail["pid"])
            continue
        identity = (detail["id"], stage)
        if phase == "BEFORE":
            if pending is not None or identity[0] in ids:
                raise ValueError("Overlapping or duplicate native call")
            pending = identity
            ids.add(identity[0])
        elif phase == "RETURNED":
            if identity != pending:
                raise ValueError("Unmatched native return")
            pending = None
            stages[stage] = stages.get(stage, 0) + 1
        else:
            raise ValueError(f"Native failure or unknown phase: {phase}")
    required = ("portal.create", "portal.pid", "project.create", "project.name",
                "transaction.commit", "transaction.rollback", "transaction.readback",
                "project.save", "project.close", "project.reopen", "project.persistence",
                "teardown.project.close", "teardown.portal.dispose")
    if pending or len(owned) != iterations or any(stages.get(s) != iterations for s in required) or stages.get("scratch.cleanup") != 1:
        raise ValueError("Native journal is incomplete")
    return {"assertions": report["assertions"], "completedCycles": report["completedCycles"],
            "ownedPortalPids": owned, "matchedCalls": len(ids)}


def supervise(command: list[str], output: Path, timeout: float, major: int, iterations: int) -> dict:
    # Caller must supply a new directory. No deletion or overwrite of previous runs.
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    status, exit_code, error, evidence = "FAILED", None, None, None
    with (output / "runner-output.log").open("wb") as log:
        process = None
        try:
            process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
            exit_code = process.wait(timeout=timeout)
            if exit_code != 0:
                error = f"Test runner exited with {exit_code}; inspect native-events.jsonl and runner-output.log"
            else:
                evidence = read_evidence(output / "native", major, iterations)
                status = "PASSED"
        except subprocess.TimeoutExpired:
            status, error = "TIMED_OUT", "Native outcome unknown; no retry. TIA may remain running; only the test runner was terminated."
            process.kill()
            exit_code = process.wait(timeout=10)
        except KeyboardInterrupt:
            status, error = "ABORTED", "Interrupted; native outcome unknown; no retry. TIA may remain running."
            if process is not None:
                process.kill()
                exit_code = process.wait(timeout=10)
        except (OSError, ValueError, KeyError, TypeError) as exc:
            error = f"Result verification failed: {exc}"
    result = {"schemaVersion": 1, "status": status, "runnerExitCode": exit_code,
              "elapsedSeconds": round(time.monotonic() - started, 3), "error": error,
              "tiaMajor": major, "iterations": iterations, "evidence": evidence}
    (output / "supervisor-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def self_test() -> int:
    # Fake child processes only: exercise failure/timeout handling without Siemens DLLs.
    count = 0
    from offline_fixtures import fixture_directory
    with fixture_directory('native-supervisor-selftest-') as temp:
        root = Path(temp)
        for name, code, expected, timeout in (
            ("nonzero", "raise SystemExit(7)", "FAILED", 5),
            ("missing", "pass", "FAILED", 5),
            ("timeout", "import time; time.sleep(30)", "TIMED_OUT", .2),
        ):
            result = supervise([sys.executable, "-c", code], root / name, timeout, 21, 1)
            assert result["status"] == expected, result
            assert (root / name / "supervisor-result.json").exists()
            count += 2
        try:
            supervise([sys.executable, "-c", "pass"], root / "missing", 5, 21, 1)
        except FileExistsError:
            count += 1
        else:
            raise AssertionError("Existing output reused")
        result = supervise([str(root / "missing-runner.exe")], root / "launch-failed", 5, 21, 1)
        assert result["status"] == "FAILED" and result["runnerExitCode"] is None
        count += 1
        native = root / "fixture"
        native.mkdir()
        report = {"schemaVersion": 1, "status": "PASSED", "tiaMajor": 21, "iterations": 1,
                  "completedCycles": 1, "assertions": 7, "nativeExecuted": True,
                  "scratchRetained": False, "failure": None}
        rows = [{"phase": "OWNED_PORTAL", "stage": "portal", "detail": {"pid": 123, "cycle": 0}}]
        stages = ("portal.create portal.pid project.create project.name transaction.commit transaction.rollback "
                  "transaction.readback project.save project.close project.reopen project.persistence "
                  "teardown.project.close teardown.portal.dispose scratch.cleanup").split()
        for i, stage in enumerate(stages):
            for phase in ("BEFORE", "RETURNED"):
                rows.append({"phase": phase, "stage": stage, "detail": {"id": i + 1}})

        def write_result(value, events):
            (native / "native-result.json").write_text(json.dumps(value), encoding="utf-8")
            (native / "native-events.jsonl").write_text("\n".join(json.dumps(r) for r in events), encoding="utf-8")

        write_result(report, rows)
        assert read_evidence(native, 21, 1)["matchedCalls"] == 14
        count += 1
        success = root / "success"
        child = "import pathlib,shutil; shutil.copytree(pathlib.Path(" + repr(str(native)) + "), pathlib.Path(" + repr(str(success / "native")) + "))"
        assert supervise([sys.executable, "-c", child], success, 5, 21, 1)["status"] == "PASSED"
        count += 1
        cases = [(dict(report, **{key: value}), rows) for key, value in (
            ("status", "FAILED"), ("tiaMajor", 20), ("completedCycles", 0),
            ("assertions", 0), ("nativeExecuted", False), ("scratchRetained", True),
            ("failure", "error"), ("iterations", True))]
        cases += [(report, rows[:-1]), (report, rows + rows[-1:]),
                  (report, rows[:1]), (report, rows[1:])]
        for value, events in cases:
            write_result(value, events)
            try:
                read_evidence(native, 21, 1)
            except ValueError:
                count += 1
            else:
                raise AssertionError("Incomplete native evidence accepted")
    print(f"COMPLETE: {count} native supervisor checks passed; live TIA tests NOT RUN")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--run-live", action="store_true")
    parser.add_argument("--confirm-new-portal", action="store_true")
    parser.add_argument("--runner", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--major", type=int, choices=(20, 21))
    parser.add_argument("--iterations", type=int, default=1)
    parser.add_argument("--timeout-seconds", type=int, default=600)
    args = parser.parse_args()
    if args.self_test:
        if args.run_live or args.confirm_new_portal or args.runner or args.output or args.major:
            parser.error("--self-test must not be combined with live arguments")
        return self_test()
    if not args.run_live and not args.confirm_new_portal:
        print("NOT RUN: explicit --run-live and --confirm-new-portal are required; no TIA process contacted.")
        return 0
    if not (args.run_live and args.confirm_new_portal and args.runner and args.output and args.major):
        parser.error("Live run needs both opt-in flags, --runner, --output and --major")
    if not 1 <= args.iterations <= 100 or not 15 <= args.timeout_seconds <= 3600:
        parser.error("Iterations must be 1..100; timeout 15..3600 seconds")
    runner = args.runner.resolve(strict=True)
    if runner.name != f"NativeTests.V{args.major}.exe":
        parser.error("Runner filename must match the requested TIA version")
    output = args.output.resolve()
    command = [str(runner), "--run-live", "--confirm-new-portal", "--output", str(output / "native"), "--iterations", str(args.iterations)]
    result = supervise(command, output, args.timeout_seconds, args.major, args.iterations)
    print(json.dumps(result, indent=2))
    return 0 if result["status"] == "PASSED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
