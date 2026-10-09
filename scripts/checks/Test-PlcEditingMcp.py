"""Exercise PLC document tools through real MCP transports without connecting to TIA."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)

XML = """<Document><SW.Blocks.FC ID="0"><AttributeList>
<Name>Main</Name><Number>7</Number><ProgrammingLanguage>LAD</ProgrammingLanguage>
<Interface><Sections xmlns="urn:test:interface"><Section Name="Input"><Member Name="Speed" Datatype="Int"><StartValue>1</StartValue></Member></Section></Sections></Interface>
</AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage>
<NetworkSource><FlgNet xmlns="urn:test:flg"><Parts><Call UId="2"><CallInfo Name="Helper" BlockType="FC"/></Call></Parts></FlgNet></NetworkSource>
</AttributeList><ObjectList><MultilingualText ID="3" CompositionName="Title"><ObjectList><MultilingualTextItem ID="4" CompositionName="Items"><AttributeList><Culture>zh-CN</Culture><Text>Original</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>
</SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--public-api', type=Path, required=True)
    parser.add_argument('--host-harness', type=Path, required=True)
    parser.add_argument('--major', type=int, choices=(20, 21), required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--transport', choices=('both', 'stdio', 'http'), default='both')
    args = parser.parse_args()
    for key in ('exe', 'public_api', 'host_harness', 'output'):
        setattr(args, key, getattr(args, key).resolve())
    root = args.output / ('plc-editing-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    passed, runs = 0, []

    def check(condition, message):
        nonlocal passed
        resources.require(condition, message)
        passed += 1

    for isolated in (False,):  # P7-08 retired the engine isolation supervisor
        for transport in (('stdio', 'http') if args.transport == 'both' else (args.transport,)):
            for profile in ('full', 'lite'):
                label = f'v{args.major}-{transport}-{profile}-isolated-{isolated}'
                case = root / label
                exports = case / 'exports'
                exports.mkdir(parents=True)
                source, output = exports / 'Main.xml', case / 'patched.xml'
                source.write_text(XML, encoding='utf-8')
                (exports / 'Helper.xml').write_text(XML.replace('<Name>Main</Name>', '<Name>Helper</Name>'), encoding='utf-8')
                (exports / 'unsupported.scl').write_text('FUNCTION Unindexed : Void', encoding='utf-8')
                original_bytes = source.read_bytes()
                start_checks = passed
                with resources.server(args.exe, args.public_api, args.major, transport, profile,
                                      args.host_harness, args.public_api,
                                      env_overrides={'TIA_MCP_MAX_RESPONSE_CHARS': '100000'},
                                      evidence_directory=case) as (rpc, http, logs):
                    reply = rpc('initialize', 'init', {'protocolVersion': '2024-11-05', 'capabilities': {},
                                                      'clientInfo': {'name': 'plc-editing-test', 'version': '1'}})
                    check('result' in reply, 'Initialization failed')
                    rpc('notifications/initialized', notification=True)
                    number = 0

                    def call(name, arguments):
                        nonlocal number
                        number += 1
                        if profile == 'lite':
                            arguments = {'name': name, 'arguments': arguments}
                            name = 'CallTool'
                        reply = rpc('tools/call', str(number), {'name': name, 'arguments': arguments})
                        resources.require('error' not in reply, str(reply.get('error')))
                        payload = resources.envelope(reply)
                        (case / f'response-{number:02}.json').write_text(json.dumps(payload, indent=2), encoding='utf-8')
                        return payload

                    cap = call('GetPlcBlockEditCapabilities', {'filePath': str(source)})
                    check(cap['ok'] and cap['data']['evidence']['offlineOnly'] and not cap['data']['evidence']['data']['nativeImportValidated'], 'Capability status incorrect')
                    fingerprint = cap['data']['evidence']['data']['documentFingerprint']
                    edits = {'filePath': str(source), 'outputPath': str(output), 'expectedFingerprint': fingerprint,
                             'changes': [{'action': 'setNetworkText', 'networkIndex': 0, 'field': 'Title',
                                                         'culture': 'zh-CN', 'expectedValue': 'Original', 'value': 'Updated & <safe>'}]}
                    preview = call('PatchPlcBlockDocument', edits)
                    check(preview['ok'] and not output.exists(), 'Patch preview changed filesystem')
                    stale = call('PatchPlcBlockDocument', dict(edits, dryRun=False, expectedFingerprint='stale'))
                    check(not stale['ok'] and not output.exists(), 'Stale document was written')
                    apply = call('PatchPlcBlockDocument', dict(edits, dryRun=False))
                    check(apply['ok'] and output.exists() and source.read_bytes() == original_bytes, 'Patch did not preserve source')
                    check(ET.parse(output).find('.//Text').text == 'Updated & <safe>', 'Patch result lost literal text')
                    saved = output.read_bytes()
                    repeat = call('PatchPlcBlockDocument', dict(edits, dryRun=False))
                    check(not repeat['ok'] and output.read_bytes() == saved, 'Existing output was overwritten')
                    references = call('AnalyzePlcReferences', {'directory': str(exports), 'action': 'callers', 'target': 'Helper'})
                    data = references['data']['evidence']['data']
                    check(references['ok'] and data['rowCount'] == 2, 'Explicit call graph incorrect')
                    check(not data['coverageComplete'] and not data['safeToDelete'] and not data['nativeCrossReferencesQueried']
                          and not data['inputParsedCompletely'] and len(data['failures']) == 1, 'Partial scope was hidden')
                    evidence = case / 'native-evidence'
                    refused = call('ImportPlcBlockVerified', {'softwarePath': 'PLC', 'blockPath': 'Main',
                                   'importPath': str(output), 'evidenceDirectory': str(evidence), 'dryRun': False})
                    check(not refused['ok'] and not (refused['meta']['execution'] != 'not-started')
                          and not evidence.exists(), 'Disconnected import was not refused before effects')
                    check(call('GetPlcBlockEditCapabilities', {'filePath': str(source)})['ok'], 'Refused import broke later local calls')
                runs.append({'label': label, 'checks': passed - start_checks, 'status': 'passed'})
                print('PASS ' + label, flush=True)
    result = {'status': 'passed', 'major': args.major, 'checks': passed, 'runs': runs,
              'runtimeSha256': hashlib.sha256(args.exe.read_bytes()).hexdigest(),
              'scriptSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              'nativeTiaExecuted': False,
              'transport': args.transport,
              'scope': 'Real SDK dispatch, selected transports, full/lite, direct/isolated offline hosts. Synthetic XML only; no native import or TIA connection.'}
    (root / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(f'COMPLETE: {passed} PLC editing MCP checks passed; no TIA connection attempted; evidence: {root}', flush=True)


if __name__ == '__main__':
    main()
