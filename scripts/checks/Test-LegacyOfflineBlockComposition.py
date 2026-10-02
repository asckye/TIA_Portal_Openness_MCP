#!/usr/bin/env python3
"""Real stdio MCP checks; .NET host only, no worker/TIA process or SDK assembly."""
import argparse
import json
import pathlib
import hashlib
import queue
import subprocess
import threading
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--dotnet", required=True)
parser.add_argument("--host", required=True)
parser.add_argument("--evidence-dir", required=True, help="New directory for generated owned test samples; must not exist")
options = parser.parse_args()
evidence_dir = pathlib.Path(options.evidence_dir)
evidence_dir.mkdir(parents=True, exist_ok=False)
passed = 0
for release in ("14sp1", "15.1", "16", "17", "18", "19", "20", "21"):
    process = subprocess.Popen(
        [options.dotnet, options.host, "--release-key", release],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    lines = queue.Queue()
    def pump(stream, destination):
        for line in stream:
            destination.put(line)
        destination.put(None)
    threading.Thread(target=pump, args=(process.stdout, lines), daemon=True).start()
    # Drain diagnostics independently so a failure cannot deadlock a full pipe.
    diagnostics = []
    def drain(stream, destination):
        destination.extend(stream)
    threading.Thread(target=drain, args=(process.stderr, diagnostics), daemon=True).start()
    sequence = 0
    def call(method, params):
        global sequence
        sequence += 1
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": sequence, "method": method, "params": params}) + "\n")
        process.stdin.flush()
        while True:
            line = lines.get(timeout=15)
            if line is None:
                raise AssertionError("Host exited: " + "".join(diagnostics))
            message = json.loads(line)
            if message.get("id") == sequence:
                return message
    try:
        initialized = call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "offline-builder-check", "version": "1"}})
        assert "result" in initialized, initialized
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        discovered = call("tools/list", {})["result"]["tools"]
        names = [tool["name"] for tool in discovered]
        assert len(names) == len(set(names))
        for name, parameter, kind in (("ComposePlcFcBlockXml", "fcBlockJson", "FC"), ("ComposePlcFbBlockXml", "fbBlockJson", "FB")):
            tool = next(tool for tool in discovered if tool["name"] == name)
            assert set(tool["inputSchema"]["required"]) == {parameter, "outputReleaseKey"}
            value = {"blockName": "Sample" + kind, "blockNumber": 42, "inputs": [{"name": "Ready", "datatype": "Bool"}], "outputs": [], "structuredText": {"operations": [{"op": "assignment", "target": "#Ready", "value": "TRUE"}]}}
            def invoke(value, output="21"):
                return call("tools/call", {"name": name, "arguments": {parameter: json.dumps(value), "outputReleaseKey": output}})
            result = invoke(value)["result"]
            assert not result.get("isError"), result
            payload = json.loads(result["content"][0]["text"])
            payload = {key[0].lower() + key[1:]: item for key, item in payload.items()}
            assert payload["ok"] and payload["meta"]["offlineOnly"]
            assert payload["meta"]["outputReleaseKey"] == "21"
            assert all(payload["meta"][flag] is False and payload["data"][flag] is False for flag in ("schemaValidated", "importValidated", "programSemanticsValidated"))
            assert payload["outputPath"] is None and payload["outputFiles"] is None
            root = ET.fromstring(payload["xml"])
            assert root.find("Engineering").attrib["version"] == "V21"
            assert root.find("SW.Blocks." + kind) is not None
            ns = "{http://www.siemens.com/automation/Openness/SW/Interface/v5}"
            assert root.find(".//" + ns + "Sections") is not None
            assert root.find(".//{http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4}StructuredText") is not None
            passed += 1
            fidelity = dict(value, blockName="a\r\n\t<&中文", commentZhCn="b\r\n\t<&中文")
            exact = invoke(fidelity)["result"]
            exact = json.loads(exact["content"][0]["text"])
            exact = {key[0].lower() + key[1:]: item for key, item in exact.items()}
            exact_xml = ET.fromstring(exact["xml"])
            assert exact_xml.find(".//SW.Blocks." + kind + "/AttributeList/Name").text == fidelity["blockName"]
            passed += 1
            for invalid in (dict(value, structuredTextInnerXml="<!DOCTYPE SECRET_CANARY>"), dict(value, name="SECRET_CANARY"), dict(value, inputs=None), dict(value, structuredText={"operations": [{"op":"token", "text":"SECRET_CANARY\n"}]})):
                rejected = invoke(invalid)
                assert rejected.get("error", {}).get("code") == -32602, rejected
                assert "SECRET_CANARY" not in json.dumps(rejected), rejected
                passed += 1
            rejected = invoke(value, "17")
            assert rejected.get("error", {}).get("code") == -32602, rejected
            passed += 1
            if release == "21":
                sample = evidence_dir / (name + ".xml")
                sample.write_text(payload["xml"], encoding="utf-8")
                print(name + " sample SHA-256 " + hashlib.sha256(sample.read_bytes()).hexdigest())
        print(f"{release}: both SCL block composers, fidelity, unsupported-output rejection and sanitized errors passed")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()  # Only the disposable test host, never TIA or a worker.
            process.wait(timeout=5)
print(f"{passed} real MCP checks passed across eight isolated host releases; no native flag, worker executable, or PublicAPI supplied. Samples: {evidence_dir}")
