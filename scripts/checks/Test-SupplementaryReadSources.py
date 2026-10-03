#!/usr/bin/env python3
"""Source-wiring regression only; no native SDK or execution claim."""
from pathlib import Path
import importlib.util
import re
import xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[2]
src=root/'tools/tiaportal-mcp/src'
props=ET.parse(src/'TiaMcp.Adapters/build/Adapter.Sources.props').getroot()
files=[src / x.attrib['Include'].replace('$(AdapterSourceRoot)/','') for x in props.iter('AdapterSource')]
assert len(files)==len(set(files)), 'Duplicate adapter source'
for f in files: assert f.is_file(), f'Missing source: {f}'
for f in (src/'TiaMcpServer.PlcFoundation').glob('*.cs'):
 assert f in files, f'Foundation source/policy omitted from explicit adapter source inventory: {f}'
code='\n'.join(f.read_text(encoding='utf-8-sig') for f in files)
ops=set(re.findall(r'"([A-Za-z]+)"',(src/'TiaMcpServer.PlcWorker/WorkerOperations.cs').read_text(encoding='utf-8-sig')))
methods=set(re.findall(r'public\s+(?:[\w<>?\[\],]+\s+)+([A-Za-z]+)\s*\(',code))
assert ops<=methods, f'Worker operations absent from adapter sources: {sorted(ops-methods)}'
spec=importlib.util.spec_from_file_location('tia_features',root/'scripts/checks/Check-TiaFeatures.py')
features=importlib.util.module_from_spec(spec)
spec.loader.exec_module(features)
for release in ['V14Sp1','V15_1','V16','V17','V18','V19','V20','V21']:
 project=next((src/f'TiaMcp.Adapters/{release}').glob('*.csproj'))
 constants=features.evaluate(root,project.relative_to(root).as_posix(),{},'dotnet')['DefineConstants'].split(';')
 assert ('PLC_WATCH_READ' in constants)==(release!='V14Sp1'),release
 assert ('PLC_TECH_GROUP_READ' in constants)==(release in ['V19','V20','V21']),release
native=(src/'TiaMcpServer.PlcFoundation/PlcSupplementaryRead.cs').read_text(encoding='utf-8-sig')
assert native.count('PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey')==2
assert native.count('PlcSupplementaryReadPolicy.ReadSnapshot(')==2
assert '.ForceTables' not in native and 'GetType().Get' not in native
assert '()=>item.OfSystemLibElement,()=>item.OfSystemLibVersion' in native
assert 'PlcSupplementaryReadPolicy.TechnologyMetadata(item.Name' in native
for forbidden in ['GetAttributeInfos', 'GetAttribute(', 'SetAttribute(', 'IEngineeringObject']:
 assert forbidden not in native, f'Technology metadata must use typed getters only: {forbidden}'
assert not re.search(r'item\.OfSystemLib(?:Element|Version)\s*=',native), 'No metadata setter permitted'
worker=(src/'TiaMcpServer.PlcWorker/Program.cs').read_text(encoding='utf-8-sig')
for name in ['ReadWatchTableNames','ReadTechnologyObjects','ReadState','ReadPortalProcessProjects','ReadPortalConnectReadiness']:
 assert f'name=="{name}"' in worker
print(f'PASS: {len(ops)} worker operations wired to explicit shared adapter sources; five supplementary/runtime read-only dispatch guards; eight release-symbol gates. Source checks only.')
