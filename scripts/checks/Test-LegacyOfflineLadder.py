#!/usr/bin/env python3
"""Real stdio MCP checks; .NET host only, no worker/TIA process or SDK assembly."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mcp_results import envelope

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
        initialized = call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "offline-ladder-check", "version": "1"}})
        assert "result" in initialized, initialized
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        discovered = call("tools/list", {})["result"]["tools"]
        names = [tool["name"] for tool in discovered]
        assert len(names) == len(set(names))
        for name, parameter, kind in (("BuildFlgNetCall", "flgNet", "Call"), ("BuildPlcLadFcBlock", "ladFcBlock", "FC")):
            tool = next(tool for tool in discovered if tool["name"] == name)
            assert set(tool["inputSchema"]["required"]) == {parameter, "outputReleaseKey"}
            call_value = {"callName": "SampleFC", "parameters": [{"name": "InputA", "section": "Input", "dataType": "Int", "sourceKind": "constant", "constantValue": "42"}, {"name": "OutputB", "section": "Output", "dataType": "Bool", "symbolPath": ["数据", "Ready"]}]}
            value = call_value if kind == "Call" else {"blockName": "Caller", "blockNumber": 42, "networks": [{"call": call_value}, {"call": call_value}]}
            def invoke(value, output="21"):
                return call("tools/call", {"name": name, "arguments": {parameter: value, "outputReleaseKey": output}})
            result = invoke(value)["result"]
            assert not result.get("isError"), result
            payload = envelope(result)
            assert payload["ok"] and payload["data"]["offlineOnly"]
            assert payload["data"]["outputReleaseKey"] == "21"
            assert all(payload["data"][flag] is False for flag in ("schemaValidated", "importValidated", "programSemanticsValidated"))
            assert payload["data"]["outputPath"] is None and payload["data"]["outputFiles"] is None
            root = ET.fromstring(payload["data"]["xml"])
            ns = "{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5}"
            if kind == "Call":
                assert root.tag == ns + "FlgNet"
                networks = [root]
            else:
                assert root.find("Engineering").attrib["version"] == "V21"
                assert root.find("SW.Blocks.FC") is not None
                networks = root.findall(".//" + ns + "FlgNet")
                assert len(networks) == 2
                ids = [e.attrib["ID"] for e in root.iter() if "ID" in e.attrib]
                assert len(ids) == len(set(ids))
            for network in networks:
                parts = list(network.find(ns + "Parts"))
                wires = list(network.find(ns + "Wires"))
                assert len(parts) == 3 and len(wires) == 3
                ids = [e.attrib["UId"] for e in parts + wires]
                assert len(ids) == len(set(ids))
            passed += 1
            fidelity = json.loads(json.dumps(value))
            fidelity_call = fidelity if kind == "Call" else fidelity["networks"][0]["call"]
            exact_text = "</ConstantValue><Injected/>\r\n\t<&中文😀 C:\\private"
            fidelity_call["parameters"][0]["constantValue"] = exact_text
            exact = invoke(fidelity)["result"]
            exact = envelope(exact)
            exact_xml = ET.fromstring(exact["data"]["xml"])
            assert exact_xml.find(".//" + ns + "ConstantValue").text == exact_text
            assert not any(e.tag == "Injected" for e in exact_xml.iter())
            passed += 1
            invalids = [dict(value, rawXml="<!DOCTYPE SECRET_CANARY>"), dict(value, path="SECRET_CANARY")]
            for field, text in (("section", "InOut"), ("dataType", "Unknown"), ("name", "EN"), ("sourceKind", "Unknown")):
                invalid = json.loads(json.dumps(value))
                invalid_call = invalid if kind == "Call" else invalid["networks"][0]["call"]
                invalid_call["parameters"][0][field] = text
                invalids.append(invalid)
            for invalid in invalids:
                rejected = invoke(invalid)
                assert envelope(rejected)["error"]["code"] == "INVALID_ARGUMENT", rejected
                assert "SECRET_CANARY" not in json.dumps(rejected), rejected
                passed += 1
            rejected = invoke(value, "17")
            assert envelope(rejected)["error"]["code"] == "INVALID_ARGUMENT", rejected
            passed += 1
            if release == "21":
                sample = evidence_dir / (name + ".xml")
                sample.write_text(payload["data"]["xml"], encoding="utf-8")
                print(name + " sample SHA-256 " + hashlib.sha256(sample.read_bytes()).hexdigest())
        print(f"{release}: both LAD call candidates, fidelity, unsupported-output rejection and sanitized errors passed")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()  # Only the disposable test host, never TIA or a worker.
            process.wait(timeout=5)
print(f"{passed} real MCP checks passed across eight isolated host releases; no native flag, worker executable, or PublicAPI supplied. Samples: {evidence_dir}")
