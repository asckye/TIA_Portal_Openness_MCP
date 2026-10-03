"""Check ecosystem adapters through offline MCP hosts, never initialize or attach to TIA."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('resources', Path(__file__).with_name('Test-ResourceDiscovery.py'))
resources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resources)

# Independently authored synthetic fixture; no customer or upstream native corpus.
XML = '''<Document><Engineering version="V21"/><SW.Blocks.FC ID="0"><AttributeList><Name>Demo</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"><Member Name="Enabled" Datatype="Bool"/></Section></Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage><NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"><Parts><Access Scope="GlobalVariable" UId="10"><Symbol><Component Name="Input"/></Symbol></Access><Access Scope="GlobalVariable" UId="11"><Symbol><Component Name="Output"/></Symbol></Access><Part Name="Contact" UId="20"/><Part Name="Coil" UId="21"/></Parts><Wires><Wire UId="30"><Powerrail/><NameCon UId="20" Name="in"/></Wire><Wire UId="31"><IdentCon UId="10"/><NameCon UId="20" Name="operand"/></Wire><Wire UId="32"><NameCon UId="20" Name="out"/><NameCon UId="21" Name="in"/></Wire><Wire UId="33"><IdentCon UId="11"/><NameCon UId="21" Name="operand"/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--major', type=int, choices=(20,21))
    parser.add_argument('--host-harness', type=Path)
    parser.add_argument('--public-api', type=Path)
    parser.add_argument('--schema-root', type=Path)
    parser.add_argument('--transport', choices=('stdio', 'http', 'both'), default='both')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    folder = args.output.resolve() / ('ecosystem-' + uuid.uuid4().hex)
    folder.mkdir(parents=True)
    source = folder/'Demo.xml'; source.write_text(XML, encoding='utf-8')
    checks = 0
    def check(value, message):
        nonlocal checks
        resources.require(value, message); checks += 1
    bridge = ROOT/'scripts/ecosystem/simaticml_decode_bridge.py'
    def decode(path):
        run = subprocess.run([sys.executable,'-I','-B',str(bridge)], input=json.dumps({'filePath':str(path)}), text=True, encoding='utf-8', capture_output=True, timeout=30)
        data = json.loads(run.stdout)
        return run.returncode, data
    code, data = decode(source)
    check(code == 0 and data['success'] and 'Output' in data['readableScl'] and 'Input' in data['readableScl'], 'Contact/coil decoding failed: '+str(data))
    check(not data['reimportable'] and not data['nativeFormatQualified'] and not data['dataComplete'], 'Decoder overclaims qualification')
    for label, bad in [('v20', XML.replace('version="V21"','version="V20"')), ('missing-version',XML.replace('<Engineering version="V21"/>','')),('xxe','<!DOCTYPE Document [<!ENTITY x SYSTEM "file:///not-read">]>'+XML),('multiple',XML.replace('</Document>','<SW.Blocks.FC/></Document>')),('ob',XML.replace('SW.Blocks.FC','SW.Blocks.OB'))]:
        target=folder/(label+'.xml'); target.write_text(bad, encoding='utf-8')
        check(decode(target)[0] != 0, 'Decoder accepted '+label)
    unknown=folder/'unknown.xml'; unknown.write_text(XML.replace('Name="Contact"','Name="UnknownBox"'), encoding='utf-8')
    code,data=decode(unknown)
    check(code != 0 or data['hasWarnings'], 'Unknown logic silently accepted')
    catalog=json.loads((ROOT/'reference/v21-ecosystem.json').read_text(encoding='utf-8'))
    check(len(catalog['entries']) >= 80 and len(catalog['search']['hits']) == 144, 'Survey lost entries')
    check(len({e['id'] for e in catalog['entries']}) == len(catalog['entries']), 'Duplicate catalog ID')
    check(all(e['localNativeValidated'] is False for e in catalog['entries']), 'Catalog invents native validation')
    if not args.self_test:
        for name in ('exe','host_harness','public_api','schema_root'):
            resources.require(getattr(args,name) is not None, '--'+name+' required')
            setattr(args,name,getattr(args,name).resolve())
        for isolated in (False,True):
            for transport in (('stdio','http') if args.transport == 'both' else (args.transport,)):
                for profile in ('full','lite'):
                    case=folder/f'{transport}-{profile}-{isolated}'; case.mkdir()
                    cwc=case/'cwc'; (cwc/'control').mkdir(parents=True)
                    (cwc/'control/index.html').write_text('<html>test</html>',encoding='utf-8')
                    (cwc/'manifest.json').write_text(json.dumps({'mver':'1.2.0','control':{'identity':{'name':'Demo','displayname':'Demo','version':'1.0','type':'guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073'}}}),encoding='utf-8')
                    with resources.server(args.exe,args.public_api,args.major,transport,profile,args.host_harness,args.public_api,isolate=isolated,
                                          env_overrides={'TIA_MCP_REPOSITORY_ROOT':str(ROOT),'TIA_MCP_PLC_TOOLS_PYTHON':sys.executable,'TIA_MCP_MAX_RESPONSE_CHARS':'100000'},evidence_directory=case) as (rpc,http,logs):
                        rpc('initialize','init',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'ecosystem-check','version':'1'}})
                        rpc('notifications/initialized',notification=True)
                        number=0
                        def call(name,arguments):
                            nonlocal number
                            number+=1
                            if profile=='lite': arguments={'name':name,'argumentsJson':json.dumps(arguments)};name='CallTool'
                            response=rpc('tools/call',str(number),{'name':name,'arguments':arguments})
                            payload=json.loads(response['result']['content'][0]['text'])
                            (case/f'{number:02}.json').write_text(json.dumps(payload,indent=2),encoding='utf-8')
                            if profile=='lite':
                                inner=json.loads(payload['message']);return inner.get('Meta',inner.get('meta'))
                            return payload['meta']
                        cat=call('ReadV21EcosystemCatalog',{'query':'CWC','source':'official','limit':1})
                        check(cat['success'] and len(cat['rows'])==1 and cat['hasMore'],'Catalog query/pagination failed')
                        result=call('DecodePlcSimaticMl',{'filePath':str(source)})
                        check(result['success'] and result['data']['analysisOnly'],'MCP decoder failed: '+str(result))
                        result=call('ValidatePlcXmlSchemas',{'filePath':str(source),'schemaDirectory':str(args.schema_root)})
                        check(result['success'] and result['data']['fragmentSchemasPassed'] and not result['data']['wholeDocumentValidated'],'Installed Siemens schema validation failed: '+str(result))
                        preview=call('ManageUnifiedCwcPackage',{'directory':str(cwc)})
                        check(preview['success'] and not preview['data']['written'],'CWC inspection failed')
                        output=case/preview['data']['suggestedFileName']
                        params={'directory':str(cwc),'action':'build','outputPath':str(output),'dryRun':False,'expectedFingerprint':preview['data']['packageFingerprint']}
                        check(call('ManageUnifiedCwcPackage',dict(params,expectedFingerprint='stale'))['success'] is False and not output.exists(),'Stale CWC built')
                        applied=call('ManageUnifiedCwcPackage',params)
                        check(applied['success'] and output.exists(),'CWC build failed')
                        with zipfile.ZipFile(output) as z: check(set(z.namelist())=={'manifest.json','control/index.html'},'CWC root layout wrong')
                        check(call('ManageUnifiedCwcPackage',params)['success'] is False,'Existing ZIP overwritten')
                    print(f'PASS V{args.major} {transport} {profile} isolated={isolated}',flush=True)
    result={'status':'passed','checks':checks,'major':args.major,'nativeTiaExecuted':False,'selfTestOnly':args.self_test,
            'runtimeSha256':hashlib.sha256(args.exe.read_bytes()).hexdigest() if args.exe else None,
            'scriptSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            'scope':'Synthetic decoder fixtures, real local PublicAPI fragment schemas and offline MCP transport dispatch; no TIA attach, writes or runtime CWC deployment.'}
    (folder/'result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(f'COMPLETE: {checks} ecosystem checks passed; native TIA NOT RUN; evidence: {folder}',flush=True)


if __name__=='__main__':
    main()
