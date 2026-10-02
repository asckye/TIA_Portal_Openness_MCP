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
        for name, parameter, value in (
            ("BuildPlcGlobalDbXml", "globalDbJson", {"dbName": "SampleDb", "dbNumber": 42, "staticMembers": [{"name": "Ready", "datatype": "Bool", "externalWritable": False, "startValue": "TRUE", "commentZhCn": "Combined ordering sample"}]}),
            ("BuildStructuredTextXml", "structuredTextJson", {"operations": [{"op": "assignment", "target": "#Count", "value": "1"}]}),
        ):
            tool = next(tool for tool in discovered if tool["name"] == name)
            assert set(tool["inputSchema"]["required"]) == {parameter, "outputReleaseKey"}
            arguments = {parameter: json.dumps(value), "outputReleaseKey": "21"}
            result = call("tools/call", {"name": name, "arguments": arguments})["result"]
            assert not result.get("isError"), result
            payload = json.loads(result["content"][0]["text"])
            # The SDK currently uses camelCase. Accept the configured policy, not a separate envelope.
            payload = {key[0].lower() + key[1:]: value for key, value in payload.items()}
            assert payload["ok"] and payload["meta"]["offlineOnly"]
            assert payload["meta"]["outputReleaseKey"] == "21"
            assert payload["meta"]["schemaValidated"] is False
            assert payload["meta"]["importValidated"] is False
            root = ET.fromstring(payload["xml"])
            assert payload["meta"]["programSemanticsValidated"] is False
            if name == "BuildPlcGlobalDbXml":
                assert root.find("Engineering").attrib["version"] == "V21"
                ns = "{http://www.siemens.com/automation/Openness/SW/Interface/v5}"
                member = root.find(".//" + ns + "Member")
                assert [el.tag for el in member] == [ns + "AttributeList", ns + "StartValue", ns + "Comment"]
            else:
                assert root.tag == "{http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4}StructuredText"
                inner_args = dict(arguments, innerOnly=True)
                inner_result = call("tools/call", {"name": name, "arguments": inner_args})["result"]
                assert not inner_result.get("isError"), inner_result
                inner = json.loads(inner_result["content"][0]["text"])
                inner = {key[0].lower() + key[1:]: item for key, item in inner.items()}
                wrapped = ET.fromstring('<StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">' + inner["xml"] + '</StructuredText>')
                assert [ET.tostring(x) for x in root] == [ET.tostring(x) for x in wrapped]
                passed += 1
            if name == "BuildStructuredTextXml":
                fidelity_negatives = [
                    {"operations": [{"op": "token", "text": "SECRET_CANARY\nvalue"}]},
                    {"operations": [{"op": "global", "name": "SECRET_CANARY\tvalue"}]},
                    {"operations": [{"op": "symbol", "name": '\"SECRET_CANARY\nvalue\"'}]},
                    {"operations": [{"op": "literal", "value": "SECRET_CANARY\rvalue"}]},
                ]
                fidelity_positive = {"operations": [{"op": "literal", "value": "a\n\t<& value"}]}
            else:
                fidelity_negatives = [
                    dict(value, dbName="SECRET_CANARY\rvalue"),
                    dict(value, staticMembers=[{"name": "x", "datatype": "Bool", "comment": "SECRET_CANARY\rvalue"}]),
                    dict(value, staticMembers=[{"name": "x", "datatype": "Bool", "startValue": "SECRET_CANARY\rvalue"}]),
                ]
                fidelity_positive = dict(value, dbName="a\n\t<& value")
            for invalid in fidelity_negatives:
                rejected = call("tools/call", {"name": name, "arguments": {parameter: json.dumps(invalid), "outputReleaseKey": "21"}})
                assert rejected.get("error", {}).get("code") == -32602, rejected
                assert "SECRET_CANARY" not in json.dumps(rejected), rejected
                passed += 1
            exact_result = call("tools/call", {"name": name, "arguments": {parameter: json.dumps(fidelity_positive), "outputReleaseKey": "21"}})["result"]
            assert not exact_result.get("isError"), exact_result
            exact_payload = json.loads(exact_result["content"][0]["text"])
            exact_payload = {key[0].lower() + key[1:]: item for key, item in exact_payload.items()}
            exact_xml = ET.fromstring(exact_payload["xml"])
            if name == "BuildStructuredTextXml":
                assert exact_xml.find(".//{http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4}ConstantValue").text == "a\n\t<& value"
            else:
                assert exact_xml.find(".//SW.Blocks.GlobalDB/AttributeList/Name").text == "a\n\t<& value"
            passed += 1
            if release == "21":
                sample = evidence_dir / (name + ".xml")
                sample.write_text(payload["xml"], encoding="utf-8")
                if name == "BuildPlcGlobalDbXml":
                    fragment = evidence_dir / "GlobalDbSections.xml"
                    # Extract without namespace removal; XML prefix choice has no semantic effect.
                    fragment.write_bytes(ET.tostring(root.find(".//" + ns + "Sections"), encoding="utf-8"))
                print(name + " sample SHA-256 " + hashlib.sha256(sample.read_bytes()).hexdigest())
            passed += 1
            arguments["outputReleaseKey"] = "17"
            rejected = call("tools/call", {"name": name, "arguments": arguments})
            assert rejected.get("error", {}).get("code") == -32602, rejected
            passed += 1
            secret = "SECRET-example-token-9f781"
            arguments = {parameter: json.dumps({secret: True}), "outputReleaseKey": "21"}
            rejected = call("tools/call", {"name": name, "arguments": arguments})
            assert rejected.get("error", {}).get("code") == -32602, rejected
            assert secret not in json.dumps(rejected), rejected
            assert len(rejected["error"]["message"]) < 256, rejected
            passed += 1
        print(f"{release}: both composition builders, unsupported-output rejection and sanitized errors passed")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()  # Only the disposable test host, never TIA or a worker.
            process.wait(timeout=5)
print(f"{passed} real MCP checks passed across eight isolated host releases; no native flag, worker executable, or PublicAPI supplied. Samples: {evidence_dir}")
