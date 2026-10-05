"""Generate the reviewed V4 proposal, without building or loading product binaries.

Run --check in a clean checkout; --self-test also exercises rejection cases.
Only the embedded profile resource and the marked documentation block are outputs.
"""
import argparse
import collections, json, pathlib, re, runpy, subprocess, sys, xml.etree.ElementTree as ET
sys.dont_write_bytecode = True
root = pathlib.Path(__file__).resolve().parents[2]
read = lambda p: (root / p).read_text(encoding="utf-8-sig")
files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode("utf-8").rstrip("\0").split("\0")
RESOURCE = 'src/Logic/ModelContextProtocol/ToolProfiles.resx'
files = sorted(set(p for p in files if (root / p).exists()) | {RESOURCE})
keys = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"]
snap = {k: json.loads(read(f"manifest/contracts/baseline/{k}.json")) for k in keys}
tools = {k: {t["name"]: t for t in d["tools"]} for k, d in snap.items()}
names = sorted(set().union(*(set(t) for t in tools.values())))
catalog = dict(re.findall(r"^\| " + chr(96) + r"([^" + chr(96) + r"]+)" + chr(96) + r" \| ([^|]+) \|$", read("docs/reference/version-tool-catalog.md"), re.M))
assert set(catalog) == set(names)
for n in names:
    assert catalog[n].strip().split(", ") == [k for k in keys if n in tools[k]], n
E = "src/Engine/"
L = "src/Logic/"
F = "src/FoundationHost/"
S = "src/Studio/"
A = "src/Adapters/"
SH = "src/Shared/"
TE = "tests/Engine/"
TS = "tests/Studio/"
# D334 moved sources, not product identities. Resolve every inventory entry
# against tracked files so a stale pre-move path cannot silently disappear.
def validate_paths(paths):
    for p in paths:
        assert p in files or any(f.startswith(p.rstrip('/') + '/') for f in files), ("untracked inventory path", p)

validate_paths([E, L, F, S, A, SH, TE, TS, "build-tools/native-call-weaver", "plugin/skill"])
primitive_paths = []
for p in sorted(f for f in files if f.startswith(SH + "shared-native/") and f.endswith(".props")):
    for item in ET.fromstring(read(p)).iter("TiaSharedNativePrimitive"):
        include = item.attrib["Include"].replace("$(MSBuildThisFileDirectory)", "")
        primitive_paths.append(((root / p).parent / include).resolve().relative_to(root).as_posix())
validate_paths(primitive_paths)
assert len(primitive_paths) == len(set(primitive_paths)) == 5
step_i = {
    "p4-i1": ("VersionControl", "Vci/VersionControlPrimitives.cs"),
    "p4-i2": ("PlcTables TechnologyObjects", "Plc/WatchTechnologyPrimitives.cs"),
    "p4-i3": ("Devices", "Hardware/HardwarePrimitives.cs"),
    "p4-i4a": ("PlcBlocks PlcSoftware Types", "Plc/PlcBlockPrimitives.cs"),
    "p4-i4b": ("Documents PlcExternalSources", "Plc/PlcDocumentPrimitives.cs"),
}
for task, (services, primitive) in step_i.items():
    assert A + "Native/" + primitive in primitive_paths
    validate_paths([E + "Siemens/Services/" + s + "Service.cs" for s in services.split()])
    assert json.loads(read("docs/development/evidence/" + task + "-native-evidence.json"))["accepted"] is True
shared_paths = ET.fromstring(read(SH + "TiaSharedAdapterPaths.props"))
assert shared_paths.findtext(".//TiaSharedAdapterPaths") == "false"
behavior_families = "DEVICE IMPORT EXPORT SESSION CLOSE SOURCE COMPILE FALLBACK".split()
ledger = read("docs/reference/real-machine-ledger.md")
for fam in behavior_families:
    rows = re.findall(r"^\| P6-" + fam + r" .*", ledger, re.M)
    assert len(rows) == 1 and "**NOT RUN**" in rows[0], (fam, rows)
sys.path.insert(0, str(root / "scripts/checks"))
import engine_sources
engine = engine_sources.EngineSources(root)
VERBS = set("Analyze Apply Archive Attach Audit Bind Build Call Check Clear Close Compare Compile Configure Connect Create Decode Delete Describe Disconnect Download Ensure Exchange Export Extract Find Generate Get Import Initialize Inspect Instantiate Invoke List Manage Monitor Move Open Patch Plan Plug Preview Probe Release Render Repair Resolve Restart Retrieve Run Sample Save Scan Search Seed Set Show Synchronize Trace Upgrade Upload Validate Write".split())
VERB_RENAMES = {"Read": "Get", "Compose": "Build", "Update": "Set", "Sync": "Synchronize", "Preflight": "Preview", "Dump": "Get", "Add": "Create"}
SPECIAL_NAMES = {
    "AddDevice": "CreateDevice", "AddDeviceWithFallback": "CreateHardwareDevice",
    "AddGsdDeviceWithProbe": "CreateGsdDevice", "AddHardwareCatalogDeviceWithProbe": "CreateHardwareCatalogDevice",
    "Bootstrap": "InitializeEnvironment", "Doctor": "GetEnvironmentDiagnostics",
    "DiagnosePortalConnectReadiness": "GetPortalConnectionReadiness",
    "LintPlcSclSource": "AnalyzePlcSclSource",
    "ScaffoldProject": "BuildProjectScaffold",
    "ReadPlcTags": "ListPlcTags", "ReadPlcSystemConstants": "ListPlcSystemConstants", "ReadPlcUserConstants": "ListPlcUserConstants",
    "Connect": "ConnectPortal", "Disconnect": "DisconnectPortal", "ConnectIsolated": "ConnectIsolatedPortal",
    "ConnectToProject": "ConnectProject", "AttachToOpenProject": "AttachOpenProject",
    "CompileSoftware": "CompilePlcSoftware", "CompileAndDiagnosePlc": "CompilePlcDiagnostics",
    "CompileAndDiagnoseHmi": "CompileHmiDiagnostics",
    "PlcBuildAndImport": "BuildAndImportPlcArtifact", "UnifiedOpenPipeRequest": "InvokeUnifiedOpenPipe",
    "CheckForUpdate": "CheckProductUpdate", "SaveAsProject": "SaveProjectCopy",
    "GoOnline": "ConnectOnlinePlc", "GoOffline": "DisconnectOnlinePlc", "GoOfflineAll": "DisconnectOnlinePlcs",
    "EnsureStartStopUnifiedHmi": "SetUnifiedHmiRuntimeState", "DownloadToPlc": "DownloadPlc",
    "ExportAsDocuments": "ExportPlcBlockDocuments", "ExportBlocksAsDocuments": "ExportPlcBlocksDocuments",
    "ImportFromDocuments": "ImportPlcBlockDocuments", "ImportBlocksFromDocuments": "ImportPlcBlocksDocuments",
    "GetState": "GetSessionState", "GetProject": "GetProjectInfo",
    "GetBlockInfo": "GetPlcBlockInfo", "GetBlocks": "ListPlcBlocks", "GetBlocksWithHierarchy": "GetPlcBlockHierarchy",
    "GetTypeInfo": "GetPlcTypeInfo", "GetTypes": "ListPlcTypes", "GetCrossReferences": "GetPlcCrossReferences",
    "MoveBlockToGroup": "MovePlcBlockToGroup", "DescribeBlockLogic": "DescribePlcBlockLogic",
    "ImportBlock": "ImportPlcBlock", "ExportBlock": "ExportPlcBlock", "ExportBlocks": "ExportPlcBlocks",
    "ImportType": "ImportPlcType", "ExportType": "ExportPlcType", "ExportTypes": "ExportPlcTypes",
    "ImportBlocksFromDirectory": "ImportPlcBlocksFromDirectory", "RepairAndReimportBlock": "RepairAndReimportPlcBlock",
    "GetExport": "GetExportContent", "ListExports": "ListExportHandles", "ClearExports": "ClearExportHandles",
    "DeleteExport": "DeleteExportHandle", "SaveExport": "SaveExportContent",
    "GetPutGetAccess": "GetPlcPutGetAccess", "SetPutGetAccess": "SetPlcPutGetAccess",
    "SetCpuCommonSettings": "SetPlcCpuSettings", "GetOpcUaConfig": "GetPlcOpcUaConfiguration",
    "ReadToolBatch": "RunReadOnlyToolBatch", "RunToolsInTransaction": "RunToolTransaction",
    "MonitorWatchTableLiveS7": "MonitorPlcWatchTableS7", "SetWatchTableModifyValue": "SetPlcWatchTableModifyValue",
    "GetAuthoringGuide": "GetToolUsage", "GetRecipe": "GetToolUsage",
    "ListUnifiedLibraryFolder": "ListUnifiedLibraryFolderEntries",
    "RebuildReleaseHandoffArtifacts": "BuildReleaseHandoffArtifacts",
    "ValidatePlcXmlSchemas": "ValidatePlcDocumentSchemas",
    "BuildPlcSymbolManifestFromXmlPath": "BuildPlcSymbolManifestFromPath",
    "InstantiatePlcXmlTemplates": "InstantiatePlcTemplates",
}
COLLECTIONS = set("GetDevices GetHmiConnections GetHmiScreens GetHmiTagTables GetHmiTags GetPlcExternalSources GetPlcForceTables GetPlcTagTables GetPlcWatchTables GetTechnologyObjects GetVersionControlWorkspaces ReadClassicHmiFaceplates ReadClassicHmiScripts ReadCommunicationConnections ReadDccCharts ReadDeviceItemChannels ReadDriveObjects ReadIoSystems ReadNetworkDomains ReadPlcSimAdvancedInstances ReadPlcSoftwareUnits ReadPlcSystemGroups ReadSafetyActivationTests ReadSiVArcRules ReadSivarcBlockDefinitions ReadTestSuiteCases ReadTransferAreas ReadTransferRoutes ReadUnifiedEngineeringObjects ReadUnifiedTagDefinitions".split())

def rename(n):
    if n in SPECIAL_NAMES:
        target = SPECIAL_NAMES[n]
    elif n in COLLECTIONS:
        target = re.sub(r"^(Get|Read)", "List", n)
    else:
        verb = re.match(r"[A-Z][a-z]*", n)[0]
        target = VERB_RENAMES.get(verb, verb) + n[len(verb):]
    return target.replace("SiVArc", "Sivarc").replace("Json", "").replace("Xml", "").replace("Xlsx", "")


source_tools = {}
for p, text in engine.sources.items():
    for m in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', text):
        decl = re.search(r"^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>?,\[\] .]+?\s+(\w+)\s*\(", text[m.end():], re.M)
        assert decl and m[1] not in source_tools
        source_tools[m[1]] = (p.relative_to(root).as_posix(), decl[1])
registered_tools = dict(source_tools)
usage_generator = runpy.run_path(str(root / 'scripts/generate/Generate-ToolUsage.py'))
registered_rosters, _ = usage_generator['registered_rosters'](root)
renames = {n: rename(n) for n in names}
current_names = {}
for n in tools["21"]:
    candidates = {n, renames[n]} & registered_tools.keys()
    if n in ("GetAuthoringGuide", "GetRecipe"):
        assert n not in registered_tools, (n, "merged alias still registered")
        candidates = {"GetToolUsage"}
    assert len(candidates) == 1, (n, "missing or double registration", candidates)
    current_names[n] = next(iter(candidates))
assert set(current_names.values()) == set(registered_tools), "unmapped registrations"
source_tools = {n: registered_tools[current_names[n]] for n in tools["21"]}
source_tools["GetRecipe"] = (E + "ModelContextProtocol/Tools/McpServer.ToolBridge.cs", "GetRecipe")
policy = read(E + "Siemens/ToolVersionPolicy.cs")
only21 = set(re.findall(r'\["([^"]+)"\]\s*=', policy.split("internal static string ToolProblem")[0]))
only21 = {old for old, current in current_names.items() if old in only21 or current in only21}
assert set(tools["20"]) == set(source_tools) - only21
lite = set(snap["21"]["liteTools"])
assert all(lite == set(snap[k]["liteTools"]) for k in ["20", "21"])
foundation = "\n".join(read(f) for f in files if f.startswith(F) and f.endswith(".cs"))
definitions = {}
for line in read(F+"FoundationTools.cs").splitlines():
    m=re.match(r'\s*new\("([^"]+)"',line)
    if m:
        response=re.search(r',\s*"([^"]+)"\),?\s*$',line)
        definitions[m[1]]=response[1] if response else ""
helpers=set()
for f in files:
    if f.startswith(F) and f.endswith(".cs") and not f.endswith("FoundationTools.cs"):
        source=read(f)
        helpers.update(re.findall(r'new (?:Offline\w+Tool|PassiveDiagnosticTool)\("([^"]+)"',source))
        helpers.update(re.findall(r'\bName\s*=\s*"([^"]+)"',source))
helpers &= set(names)
foundation_current = {k: usage_generator['resolve_names'](tools[k], registered_rosters[k], renames) for k in keys[:6]}
for k in keys[:6]:
    for n, t in tools[k].items():
        assert '"' + foundation_current[k][n] + '"' in foundation, n
        for p in t["inputSchema"]["properties"]:
            assert '"' + p + '"' in foundation, (n, p)
calls = json.loads(read("reference/tool-examples/calls.json"))["profiles"]
for k in keys:
    expected = registered_rosters[k]
    assert expected <= set(calls[snap[k]["profile"]]), k
typed = {}
for n in names:
    for k in keys:
        if n not in tools[k]: continue
        for p, schema in tools[k][n]["inputSchema"]["properties"].items():
            if p.endswith("Json") or (n == "PlcBuildAndImport" and p == "json"):
                typed.setdefault(n, {}).setdefault(p, set()).add(k)
def signature(method):
    member = engine.member(method, tool=True)
    tokens, _ = engine_sources.lexer.Lexer(member).scan()
    pairs = engine_sources.lexer.matching_pairs(tokens)
    op = next(i for i,t in enumerate(tokens) if t.value == "(" and tokens[i-1].value == method)
    return member[:tokens[pairs[op]].end]


def parameters(sig):
    tokens, _ = engine_sources.lexer.Lexer(sig).scan()
    pairs = engine_sources.lexer.matching_pairs(tokens)
    op = next(i for i,t in enumerate(tokens) if t.value == '(')
    result, part, i = {}, [], op + 1
    while i < pairs[op]:
        token = tokens[i]
        if token.value == '[' and not part:
            i = pairs[i] + 1
            continue
        if token.value in ('(', '['):
            part.extend(t.value for t in tokens[i:pairs[i]+1]); i = pairs[i] + 1; continue
        if token.value == ',' and part.count('<') == part.count('>'):
            declaration = part[:part.index('=')] if '=' in part else part
            result[declaration[-1]] = ''.join(declaration[:-1]); part = []
        else: part.append(token.value)
        i += 1
    if part:
        declaration = part[:part.index('=')] if '=' in part else part
        result[declaration[-1]] = ''.join(declaration[:-1])
    return result


signatures = {n: signature(method) for n, (_, method) in source_tools.items() if n not in ('GetAuthoringGuide', 'GetRecipe')}
# This is the explicit runtime marker. Return-type migration is checked together
# with name and typed-parameter targets below; renaming alone never enables V4.
envelope_versions = {n: 4 if re.search(r'\b(?:CallToolResult|Task<CallToolResult>)\s+\w+\s*\(', sig) else 3
                     for n, sig in signatures.items()}
family_groups = {
 "P":"assignmentItemPath branch collectionPath destinationDevicePath destinationItemPath devicePath durationPath groupPath itemPath localInterfaceItemPath modifiedDevicePath modifiedItemPath participantDevicePath participantItemPath partnerDevicePath partnerInterfaceItemPath partnerItemPath tagPath targetDevicePath targetItemPath",
 "S":"additionalHmiDeviceNames attributeNames blockPaths chartNames cultures deviceNames expectedNames expectedTagNames extensions fields files itemNames items markers names nodeIds objectPaths permissions plcSoftwarePaths plcSymbols scopeSoftwarePaths subjectAlternativeNames systemNames tagPaths tags textListNames vars",
 "N":"numbers",
 "R":"objectPath",
 "M":"attributes changes customAttributes entry properties scriptProperties settings",
 "L":"accessLevels comments promptAnswers texts",
 "V":"value",
 "C":"arguments calls operations",
 "W":"values writes",
 "B":"fbBlock fcBlock flgNet globalDb ladFcBlock structuredText tagTable udt",
 "H":"design layout package spec table theme",
 "D":"artifacts plan rows scenario",
 "X":"afterPages beforePages deviceSelection harmonizeOptions itemDetails mappingEntries options partner references request revisionDetails rules scope selection target templateIntent",
}
families = {p+"Json":f for f,ps in family_groups.items() for p in ps.split()}
def family(n,p):
    if p == "json": return "B"
    if (n,p) == ("RunPlcCompanionTool","argumentsJson"): return "S"
    if (n,p) == ("PatchPlcBlockDocument","changesJson"): return "D"
    if p == "harmonizeOptionsJson": return "S"
    assert p in families, (n,p)
    return families[p]

# One spelling per operation. List is reserved for enumerations; Get includes
# compound snapshots (tree, diagnostics, properties) even when they contain arrays.
# These are documentation mappings, never executable redirects. Merge proof is
# deliberately closed; native compile/connect paths do not qualify.
MERGES = {"GetToolUsage": {"GetToolUsage", "GetAuthoringGuide", "GetRecipe"}}
MERGE_PROOF = {
    "GetToolUsage": "原指南入口已删除；ToolUsageCatalog.GuideSelection 保留逐主题选择器映射；GetToolUsage 和 ToolRecipes.Rows 读取同一 Sequences/语言示例库，保留目的、前置条件、步骤、预期与说明，无原生动作。",
}
MERGE_PARAMETERS = {
    "GetAuthoringGuide": "topic trim/lower 后：workflow→exampleId=sequence/connect-project；openness-workflow→query=openness-base；startdrive-bico→toolName=ManageStartdriveParameter,operation=read；hmi→language=hmi-javascript；errors→空选择；其余→language=原 topic。offset=0,limit=80。",
    "GetRecipe": "topic trim 后非空→exampleId=sequence/<精确目录 topic>；空→exampleKind=sequence（新增可选枚举过滤器，默认 all）；按当前发布版过滤目录；保留 purpose/preconditions/steps/expect/notes；未知 topic→NOT_FOUND。",
    "GetToolUsage": "toolName 按 A 表转换；query/documentId/offset/limit/operation/language/exampleId 同名；新增 exampleKind=all|sequence|language 默认 all；旧默认列表仍含 tools、languages、examples。",
}
assert 'GuideSelection(string topic)' in read(SH + "ToolUsageCatalog.cs")
assert 'ToolUsageCatalog.Sequences()' in read(L + "ModelContextProtocol/ToolRecipes.cs")
assert 'ToolRecipes.Find(topic)' in read(E + "ModelContextProtocol/Tools/McpServer.ToolBridge.cs")
assert 'exampleId' in read(E + "ModelContextProtocol/Tools/ToolUsageTools.cs")
assert all(n in names for n in SPECIAL_NAMES | dict.fromkeys(COLLECTIONS))
renames = {n: rename(n) for n in names}

def validate_mapping(mapping, release_tools, merge_groups=MERGES):
    assert set(mapping) == set(names), "every current tool must be mapped exactly once"
    groups = collections.defaultdict(set)
    for n, target in mapping.items():
        assert re.fullmatch(r"[A-Z][A-Za-z0-9]+", target), target
        assert re.match(r"[A-Z][a-z]*", target)[0] in VERBS, target
        assert not any(word in target for word in ("Json", "Xml", "Xlsx", "SiVArc")), target
        groups[target.casefold()].add(n)
    for group in groups.values():
        if len(group) > 1:
            target = mapping[next(iter(group))]
            assert group == merge_groups.get(target), ("unproven duplicate target", target, group)
            assert target in MERGE_PROOF and all(n in MERGE_PARAMETERS for n in group)
    # Availability is computed within each release, never from the global union.
    targets = {k: sorted({mapping[n] for n in release_tools[k]}) for k in keys}
    for k in keys:
        for target in targets[k]:
            assert any(mapping[n] == target for n in release_tools[k])
    return targets

target_tools = validate_mapping(renames, tools)

SHAPES = {
    "P": "string[]（路径段）", "S": "string[]", "N": "int32[]", "R": "PropertyStep[]",
    "M": "AttributeMap<Scalar>", "L": "map<string,string>", "V": "NativeValue",
    "C": "ToolCall[]", "W": "WriteValue[]", "B": "BuilderSpec", "H": "HmiDesign",
    "D": "DomainSpec", "X": "DomainSelection",
}
PARAM_SHAPES = {
    "fbBlockJson":"FbBlockSpec", "fcBlockJson":"FcBlockSpec", "flgNetJson":"FlgNetCallSpec",
    "globalDbJson":"GlobalDbSpec", "ladFcBlockJson":"LadFcBlockSpec", "structuredTextJson":"StructuredTextSpec",
    "tagTableJson":"PlcTagTableSpec", "udtJson":"UdtSpec", "json":"PlcArtifactSpec(kind)",
    "layoutJson":"UnifiedLayoutSpec", "themeJson":"UnifiedThemeSpec", "specJson":"DeviceAmlSpec",
    "afterPagesJson":"GraphicSelectionPage[]", "beforePagesJson":"GraphicSelectionPage[]",
    "deviceSelectionJson":"map<string,bool>", "itemDetailsJson":"TeamcenterItemSpec",
    "revisionDetailsJson":"TeamcenterRevisionSpec", "mappingEntriesJson":"DynamizationMapping[]",
    "optionsJson":"MonitoringOptions", "partnerJson":"DccPartnerSpec(action)",
    "referencesJson":"map<string,SivarcReference|null>", "requestJson":"OpenPipeRequest(message)",
    "scopeJson":"TestScope[]", "selectionJson":"LibrarySelection[]", "targetJson":"MotionTarget",
    "templateIntentJson":"TemplateIntent", "accessLevelsJson":"map<string,int32>",
    "artifactsJson":"Artifact[]", "planJson":"NetworkPlan", "scenarioJson":"PlcSimScenario",
    "settingsJson":"CpuSettings{exactAttributes:AttributeMap<Scalar>}",
}
EXACT_SHAPES = {
    ("ManagePlcCertificate", "subjectAlternativeNamesJson"): "SubjectAlternativeName[]",
    ("CallTool","argumentsJson"): "ToolArguments(target inputSchema)",
    ("PreflightToolCall","argumentsJson"): "ToolArguments(target inputSchema)",
    ("RunPlcCompanionTool","argumentsJson"): "string[]",
    ("AuditEngineeringExports","rulesJson"): "XPathRule[]",
    ("LintPlcSclSource","rulesJson"): "LintRules",
    ("ComposePlcAliasAlarmLad","rowsJson"): "PlcAliasRow[]",
    ("InstantiatePlcXmlTemplates","rowsJson"): "TemplateRow[]",
    ("PatchPlcBlockDocument","changesJson"): "BlockEdit[]",
    ("ManageUnifiedScreenItem","propertiesJson"): "CompositeAttributeMap",
}

def shape(n, p):
    if (n, p) in EXACT_SHAPES: return EXACT_SHAPES[n, p]
    if p == "designJson": return "ClassicScreenSpec" if "Classic" in n else "UnifiedScreenSpec"
    if p == "packageJson": return "ClassicPackageSpec"
    if p == "tableJson": return "ClassicTagTableSpec"
    result = PARAM_SHAPES.get(p, SHAPES[family(n, p)])
    assert result not in ("BuilderSpec", "HmiDesign", "DomainSpec", "DomainSelection"), (n, p)
    return result


def validate_parameter_transition(name, current, target, actual, expected, v4, target_shapes):
    if not v4:
        assert current == name, (name, 'rename without a V4 envelope')
        legacy = {p for p, t in actual.items() if p.endswith('Json') and t in ('string', 'string?')}
        assert legacy == {p for p in expected if p.endswith('Json')}, (name, legacy, expected)
        if 'json' in expected: assert actual.get('json') in ('string', 'string?'), name
        return
    assert current == target, (name, 'V4 tool has a legacy name', current, target)
    if name == 'ManageSafetyFunction':
        assert actual.get('signals') == 'string[]', (name, 'setTrace signals must be a separate typed array')
    for old in expected:
        new = 'spec' if old == 'json' else old[:-4]
        assert old not in actual and new in actual, (name, old, new, actual)
        declared = actual[new].rstrip('?')
        wanted = re.split(r'[({（]', target_shapes[old])[0]
        aliases = {'map<string,string>': {'AttributeMap<string>', 'Dictionary<string,string>'},
                   'map<string,int32>': {'AttributeMap<int>', 'Dictionary<string,int>'},
                   'map<string,bool>': {'AttributeMap<bool>', 'Dictionary<string,bool>'},
                   'map<string,SivarcReference|null>': {'AttributeMap<SivarcReference?>', 'Dictionary<string,SivarcReference?>'},
                   'int32[]': {'int[]'}, 'PlcArtifactSpec': {'ConstructionSpec'},
                   'DccPartnerSpec': {'DccPartnerSpec'}, 'ToolArguments': {'ToolArguments'}}
        permitted = aliases.get(wanted, set()) | {wanted}
        assert declared in permitted, (name, new, declared, wanted)


for n, sig in signatures.items():
    actual = parameters(sig)
    expected = {p for p in typed.get(n, {}) if '21' in typed[n][p]}
    validate_parameter_transition(n, current_names[n], renames[n], actual, expected,
                                  envelope_versions[n] == 4, {p: shape(n, p) for p in expected})

if 'new FoundationV4Tool(' in foundation:
    adapter = read(F + 'FoundationV4Tool.cs')
    for k in keys[:6]:
        for n in tools[k]:
            assert foundation_current[k][n] == renames[n], (k, n, 'Foundation target mismatch')
            for old in typed.get(n, {}):
                if k not in typed[n][old]: continue
                new = old[:-4]
                wanted = re.split(r'[({（]', shape(n, old))[0]
                element = wanted.removesuffix('[]')
                assert re.search(r'<' + re.escape(wanted) + r'>|<' + re.escape(element) + r'>', adapter), (k, n, wanted)
                assert '"' + new + '"' in adapter, (k, n, new)

# Re-selected by user workflow, not inherited from the current lite roster.
LITE_GROUPS = {
    "发现、用法与完整目录调用": "FindTools GetToolUsage ListToolCategories CallTool PreviewToolCall",
    "环境与会话诊断": "InitializeEnvironment GetEnvironmentDiagnostics GetSessionState GetOpennessWorkerStatus RestartOpennessWorker ValidateAutomationContext",
    "工程生命周期": "ListPortalProcessProjects ConnectProject ConnectPortal AttachOpenProject DisconnectPortal OpenProject CloseProject SaveProject CreateProject ArchiveSavedProject",
    "工程和 PLC 定位": "GetProjectInfo GetProjectTree ListDevices GetSoftwareTree GetSoftwareInfo GetPlcBlockHierarchy ListPlcBlocks GetPlcBlockInfo ListPlcTypes GetPlcTypeInfo ListPlcTagTables",
    "常用 PLC 交换与编译": "ImportPlcBlock ExportPlcBlock ImportPlcType ExportPlcType ImportPlcTagTable ExportPlcTagTable ImportPlcExternalSource GenerateBlocksFromExternalSource CompilePlcDiagnostics WritePlcSclSourceFile",
    "离线构造与规划": "BuildPlcUdt BuildPlcGlobalDb BuildPlcTagTable PlanArtifactImportOrder ValidatePlcDocumentSchemas",
    "硬件查找和精确创建": "SearchHardwareCatalog CreateHardwareDevice",
    "HMI 定位和诊断": "ListHmiScreens DescribeHmiScreen ListHmiTagTables ListHmiTags CompileHmiDiagnostics",
    "大结果分页与文件交付": "ListExportHandles GetExportContent SaveExportContent DeleteExportHandle ClearExportHandles",
    "诊断收尾": "GenerateErrorReport",
}
lite_proposal = {"schemaVersion": 1, "contractVersion": 4, "status": "runtime-transition", "foundationLite": False, "releases": {}}
for k in keys[-2:]:
    rows = []
    for reason, members in LITE_GROUPS.items():
        for v4_name in members.split():
            n = next(n for n in tools[k] if renames[n] == v4_name and n not in ("GetAuthoringGuide", "GetRecipe"))
            current = current_names[n]
            assert n in tools[k] and current in calls[snap[k]["profile"]], (k, n)
            example = calls[snap[k]["profile"]][current]
            assert isinstance(example.get("arguments"), dict), (k, n, "missing call example")
            rows.append({"name": renames[n], "currentName": current_names[n], "reason": reason,
                         "example": "reference/tool-examples/calls.json#/profiles/" + snap[k]["profile"] + "/" + current})
    assert len({r["name"] for r in rows}) == len(rows)
    assert 55 <= len(rows) <= 65
    lite_proposal["releases"][k] = sorted(rows, key=lambda r: r["name"])

# One embedded record per release, contract version and V4 target. The registration
# map is generated from the same transition decisions as the source checks.
runtime = {"schemaVersion": 1, "contractVersion": 4, "foundationLite": False, "releases": {}}
for k in keys[-2:]:
    lite_names = {r["name"] for r in lite_proposal["releases"][k]}
    runtime_rows = {}
    for old in tools[k]:
        if old in ("GetAuthoringGuide", "GetRecipe"): continue
        target = renames[old]
        current = current_names[old]
        if target in runtime_rows: continue
        arguments = json.loads(json.dumps(calls["full-engine"][current]["arguments"]))
        runtime_rows[target] = {"name": target, "currentName": current, "sourceName": old,
            "profiles": ["full", "lite"] if target in lite_names else ["full"], "arguments": arguments,
            "envelopeVersion": envelope_versions[old]}
    runtime["releases"][k] = sorted(runtime_rows.values(), key=lambda r: r["name"])
    assert {r["currentName"] for r in runtime["releases"][k]} == {current_names[n] for n in tools[k]}
    assert all(isinstance(r["arguments"], dict) for r in runtime["releases"][k] if "lite" in r["profiles"])

def resource_text():
    resource = ET.Element("root")
    for name, value in (("resmimetype", "text/microsoft-resx"), ("version", "2.0"),
        ("reader", "System.Resources.ResXResourceReader, System.Windows.Forms"),
        ("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms")):
        ET.SubElement(ET.SubElement(resource, "resheader", name=name), "value").text = value
    entry = ET.SubElement(resource, "data", {"name": "Catalog", "xml:space": "preserve"})
    ET.SubElement(entry, "value").text = json.dumps(runtime, ensure_ascii=False, indent=2)
    ET.indent(resource, space="  ")
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(resource, encoding="unicode") + '\n'


out = []
tick = lambda s: "`" + str(s) + "`"
def table(headers, rows):
    out.extend(["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"])
    out.extend("| " + " | ".join(str(x).replace("|", r"\|").replace("\n", " ") for x in row) + " |" for row in rows)
    out.append("")
def link(p, label=None):
    assert p == RESOURCE or (root / p).exists(), p
    return "[" + (label or p.removeprefix(E).removeprefix(L).removeprefix(F).removeprefix(S)) + "](../../" + p + ")"
def section(title):
    out.extend(["<details>", "<summary>" + title + "</summary>", ""])
def end(): out.extend(["</details>", ""])
def availability(n): return ", ".join(k for k in keys if n in tools[k])
def source(n):
    if n in source_tools: return source_tools[n][0]
    if n in definitions: return F + "FoundationTools.cs"
    candidates = [f for f in files if f.startswith(F) and f.endswith('.cs') and '"' + n + '"' in read(f)]
    assert candidates, n
    return sorted(candidates)[0]

MIGRATION_GROUPS = {
    "P6-07": "McpServer.ToolBridge McpServer.Batch McpServer.CallDiscipline McpServer.Exports ToolUsageTools",
    "P6-09": "EcosystemTools V21EcosystemTools EngineeringAuditTools GitWorkflowTools ImportOrderTools OfflineAnalysisTools OfflineSuiteTools QualityAuditTools TemplateTools XmlBuilderTools PlcBuildTools PlcDocumentationTools",
    "P6-10": "PlcBlocksTools PlcSoftwareTools TypesTools PlcTablesTools McpServer.BlockLogic McpServer.BlockImportVerification",
    "P6-11": "DocumentsTools NativeExchangeTools PlcExternalSourcesTools McpServer.Patch ExportTools",
    "P6-12": "DevicesTools HardwareAmlTools HardwareManagementTools ModulesTools AddressesTools",
    "P6-13": "HardwareNetworkTools HardwareServicesTools",
    "P6-14": "CertificateManagementTools ProjectSecurityTools SafetyManagementTools SafetyValidationTools SecurityDeepTools",
    "P6-15": "AlarmsTools OpcUaTools TechnologyObjectsTools SoftwareUnitDeepTools SoftwareUnitManagementTools",
    "P6-16": "ClassicHmiFoldersTools MotionProDiagClassicHmiTools",
    "P6-17": "UnifiedHmiTools UnifiedHmiGroupsTools UnifiedScreenItemsTools UnifiedUiModelTools",
    "P6-18": "HmiExchangeTools UnifiedExchangeTools HmiTagDeletionTools",
    "P6-19": "HmiDescribeTools HmiInspectionTools GlobalScriptEditTools GraphicSelectionTools UnifiedEngineeringTools UnifiedEventsTools UnifiedObjectServicesTools MigrationReadTools ReflectionTools",
    "P6-20": "CfcTools TestSuiteTools V20OptionsTools OptionalEngineeringTools SpecializedExchangeTools",
    "P6-21": "DccTools StartdriveTools TeamcenterTools",
    "P6-22": "LibraryTools SivarcTools VersionControlTools",
    "P6-23": "RuntimeChannelTools RuntimeTools RuntimeSettingsTools PlcSimAdvancedTools OnlineDownloadTools",
    "P6-24": "SessionTools ProjectSessionTools DiagnosticsTools EngineeringDiagnosticsTools McpServer.Doctor McpServer.Maintenance McpServer.Worker",
}
owners = {}
for task, stems in MIGRATION_GROUPS.items():
    for stem in stems.split():
        path = E + "ModelContextProtocol/Tools/" + stem + ".cs"
        assert path not in owners
        assert (root / path).is_file(), path
        owners[path] = task
assert set(p for p,m in source_tools.values()) <= set(owners), sorted(set(p for p,m in source_tools.values()) - set(owners))

# Existing entry points and ownership roots, not permission to edit whole trees.
# New files are created only by the named future task within these roots.
TASK_PATHS = {
    "P6-01": ["scripts/generate/Generate-Phase6Plan.py", "src/Logic/ModelContextProtocol/ToolProfiles.resx", "docs/development/phase6-review.md", "docs/development/refactor-plan.md", "docs/reference/real-machine-ledger.md"],
    "P6-02": [L+"V4", L+"TiaMcp.Logic.csproj", TE+"TiaMcpServer.Tests/V4EnvelopeTests.cs"],
    "P6-03": [L+"V4", L+"Siemens/ArgumentRules.cs", TE+"TiaMcpServer.Tests"],
    "P6-04": [L+"V4", L+"ModelContextProtocol/Builders", F+"OfflineCompositionBuilders.cs", F+"OfflineBlockCompositionBuilders.cs", F+"OfflineLadderBuilders.cs", F+"OfflineXmlBuilders.cs", TE+"TiaMcpServer.Tests", TE+"TiaMcpServer.LegacyHostTests"],
    "P6-05": [L+"V4", L+"ModelContextProtocol/Builders", TE+"TiaMcpServer.Tests"],
    "P6-06": [L+"V4", E+"Siemens", L+"Siemens", TE+"TiaMcpServer.Tests"],
    "P6-08": [F, TE+"TiaMcpServer.LegacyHostTests", TE+"TiaMcpServer.TransportFixture", "scripts/checks/Test-FoundationTransport.py"],
    "P6-25": [E+"Cli", S+"Client", S+"Core", S+"Gui/ViewModels", S+"Gui/Localization", TE+"TiaMcpServer.Tests", TS],
    "P6-26": [E+"ModelContextProtocol", F, S+"Gui/Localization", "scripts/checks/Check-McpText.py", "scripts/checks/mcp-text-baseline.json", "scripts/checks/Test-LocalStability.py", "scripts/checks/Snapshot-ToolContracts.py", "scripts/checks/Snapshot-ToolResponses.py"],
    "P6-27": [E+"Siemens/Services/DevicesService.cs", A+"Native/Hardware", F+"DeviceAddContract.cs", TE+"TiaMcpServer.DeviceAddTests", TE+"TiaMcpServer.HardwareCatalogTests"],
    "P6-28": [E+"Siemens/Services/PlcBlocksService.cs", E+"Siemens/Services/TypesService.cs", E+"Siemens/Services/PlcTablesService.cs", A+"Native/Plc", A+"Policy/PlcBlockXmlPolicy.cs", F+"BatchImportContract.cs", TE+"TiaMcpServer.LegacyHostTests"],
    "P6-29": [E+"Siemens/Services/NativeExchangeService.cs", E+"Siemens/Services/DocumentsService.cs", E+"Siemens/EngineeringExport.cs", A+"Native/Plc", F+"BatchExportContract.cs", F+"BatchDocumentExportContract.cs", F+"SpecialExportContract.cs"],
    "P6-30": [E+"Siemens/Portal", A+"Native/Session", A+"Policy/PlcLifecyclePolicy.cs", F+"WorkerClient.cs", S+"Core/Adapters"],
    "P6-31": [E+"Siemens/Portal", A+"Native/Session", A+"Policy/PlcLifecyclePolicy.cs", F+"DisconnectContract.cs", S+"Core/Adapters"],
    "P6-32": [E+"Siemens/PlcBlockLookup.cs", E+"Siemens/Services/PlcExternalSourcesService.cs", A+"Native/Plc", F+"ExternalSourcePlanContract.cs", F+"ExternalSourceWorkflowContract.cs", F+"ExternalSourceDeleteContract.cs"],
    "P6-33": [E+"Siemens/Services/PlcSoftwareService.cs", E+"Siemens/Services/PlcBlocksService.cs", E+"Siemens/Services/HmiDescribeService.cs", E+"Siemens/Services/HardwareServicesService.cs", A+"Native/Plc/PlcBlockPrimitives.cs", F+"V17CompileEnvelope.cs"],
    "P6-34": [E+"ModelContextProtocol/Tools/OnlineToolPolicy.cs", E+"Siemens/Services/OnlineDownloadService.cs", E+"Siemens/Services/VersionControlService.cs", A+"Native/Vci"],
    "P6-35": [E+"Siemens/ToolVersionPolicy.cs", E+"ModelContextProtocol/Tools/ToolCatalog.cs", F+"FoundationTools.cs", "reference/version-feature-matrix.json", "docs/reference/real-machine-ledger.md", "scripts/checks/Snapshot-ToolContracts.py", "scripts/checks/Snapshot-ToolResponses.py"],
    "P6-36": [E+"TiaMcpServer.V20.csproj", E+"TiaMcpServer.V21.csproj", F+"TiaMcpServer.LegacyHost.csproj", S+"Launcher/Launcher.cs", "scripts/build", "scripts/checks", "build-tools/native-call-weaver", "scripts/operations/delivery-files.json"],
    "P6-37": [SH+"BundleLayout.cs", E+"Program.cs", E+"Cli", E+"Siemens/EngineRouter.cs", F+"HostOptions.cs", F+"Program.cs", L+"ModelContextProtocol/Builders/EcosystemFiles.cs", TE+"TiaMcpServer.Tests/BundleLayoutTests.cs"],
    "P6-38": [S+"Gui/Configuration", S+"Gui/ConfigurationPage.cs", S+"Client/BridgeClient.cs", S+"Core/Abstractions/SessionFactoryLoader.cs", S+"Core/Adapters/SessionFactoryLoader.cs", TS],
    "P6-39": [SH+"DataLocations.cs", SH+"InvocationJournal.cs", E+"Program.cs", E+"Cli/ReportBuilders.cs", E+"Cli/HmiTemplateBuilder.cs", E+"ModelContextProtocol/Tools/EcosystemTools.cs", L+"ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs", S+"Gui/App.xaml.cs", "scripts/ecosystem/Install-PlcTools.ps1"],
    "P6-40": ["reference/tool-examples", E+"ModelContextProtocol/McpPrompts.cs", "plugin/skill", "docs", "scripts/generate/Generate-ToolUsage.py", "scripts/generate/Generate-ToolCapabilityMatrix.ps1", SH+"ToolUsageData.json", "docs/reference/tool-matrix.md"],
    "P6-41": ["manifest/contracts", "scripts/checks/Snapshot-ToolContracts.py", "scripts/checks/Snapshot-ToolResponses.py", "scripts/generate/Generate-Phase6Plan.py", ".github/workflows/offline-checks.yml", "scripts/checks/Check-Repository.py", "scripts/checks/Validate-Bundle.ps1", "scripts/build/Package-Release.py"],
    "P6-42": ["scripts/build/Build-MultiVersion.ps1", "scripts/checks/Test-DotnetSuites.py", "tests/test-suites.json", "scripts/checks/Validate-Bundle.ps1", "docs/reference/real-machine-ledger.md"],
    "P6-43": ["docs/releases", "docs/development/phase6-review.md", "reference/tool-examples", "manifest/contracts"],
    "P6-44": [E+"Program.cs", E+"ModelContextProtocol/Tools/McpServer.SerializedCalls.cs", E+"ModelContextProtocol/Tools/McpServer.ToolBridge.cs", E+"ModelContextProtocol/Tools/McpServer.Batch.cs", E+"ModelContextProtocol/Tools/ToolCatalog.cs", F+"Program.cs", F+"FoundationTools.cs", L+"V4/Error.cs", L+"V4/V4Json.cs", L+"V4/V4Validation.cs", SH, S+"Core", S+"Gui/MainWindow.xaml", S+"Gui/MainWindow.xaml.cs", S+"Gui/ViewModels", S+"Gui/Services", S+"Gui/Settings/UiSettings.cs", S+"Gui/Localization", TE+"TiaMcpServer.Tests", TE+"TiaMcpServer.LegacyHostTests", TS],
    "P6-45": [SH+"InvocationJournal.cs", E+"ModelContextProtocol/InvocationJournal.cs", S+"Core", S+"Gui/MainWindow.xaml", S+"Gui/ViewModels", S+"Gui/Controls", S+"Gui/Configuration/ClientProfiles.cs", S+"Gui/Localization", TS],
    "P6-46": [SH+"InvocationJournal.cs", SH+"NativeCallDiagnostics.Journal.cs", SH+"DataLocations.cs", E+"ModelContextProtocol/InvocationJournal.cs", E+"Cli/CliCommands.cs", S+"Core", S+"Gui/Settings/UiSettings.cs", S+"Gui/ViewModels", TE+"TiaMcpServer.DiagnosticsTests", TS],
    "P6-47": [E+"Runtime/EnvironmentDoctor.cs", E+"ModelContextProtocol/Tools/McpServer.Doctor.cs", F+"LegacyHostPassiveDiagnostics.cs", S+"Core/Environment/OpennessDoctor.cs", SH+"OpennessEnvironment.cs", SH+"DataLocations.cs", S+"Gui/MainWindow.xaml", S+"Gui/ViewModels", S+"Gui/Configuration", S+"Gui/Localization", TS],
    "P6-48": [L+"ModelContextProtocol/Builders/PlcVisualComparison.cs", L+"ModelContextProtocol/Builders/LadTextRenderer.cs", E+"ModelContextProtocol/Tools/EcosystemTools.cs", E+"ModelContextProtocol/Tools/PlcBlocksTools.cs", F+"FoundationTools.cs", S+"Gui/MainWindow.xaml", S+"Gui/ViewModels", S+"Gui/Localization", TE+"TiaMcpServer.Tests", TE+"TiaMcpServer.LegacyHostTests", TS],
}
for task in MIGRATION_GROUPS:
    paths = sorted(p for p in owners if owners[p] == task)
    services = [E + "Siemens/Services/" + pathlib.PurePosixPath(p).stem.removesuffix("Tools") + "Service.cs" for p in paths]
    TASK_PATHS[task] = paths + sorted(p for p in services if p in files)
TASK_PATHS["P6-07"] += [E+"ModelContextProtocol/Tools/McpServer.Profile.cs", SH+"ToolUsageCatalog.cs", L+"ModelContextProtocol/ToolRecipes.cs"]
TASK_PATHS["P6-23"] += ["src/Runtime", E+"Runtime"]
TASK_PATHS["P6-24"] += [E+"Siemens/Portal", E+"EngineServices.cs", E+"EngineRegistration.cs", E+"Program.cs", E+"ModelContextProtocol/Tools/ToolCatalog.cs", E+"TiaMcpServer.V20.csproj", E+"TiaMcpServer.V21.csproj", TE+"TiaMcpServer.HttpTests"]

def validate_inventory(inventory):
    expected = {f"P6-{i:02}" for i in range(1, 49)}
    plan = read("docs/development/refactor-plan.md").split("### 阶段 6：", 1)[1].split("## 待维护者决定", 1)[0]
    assert set(re.findall(r"^\| (P6-\d+) \|", plan, re.M)) == set(inventory) == expected
    for task, paths in inventory.items():
        assert paths and len(paths) == len(set(paths)), task
        validate_paths(paths)

validate_inventory(TASK_PATHS)

out.append("### 当前基线与 V4 提案计数\n")
rows = []
for k in keys:
    pairs = [(n,p,s) for n,t in tools[k].items() for p,s in t["inputSchema"]["properties"].items() if p.endswith("Json")]
    strings = [(n,p) for n,p,s in pairs if "string" in str(s.get("type"))]
    rows.append([k, len(tools[k]), len(snap[k].get("liteTools", [])) or "不设",
                 len(strings), len(set(n for n,p in strings)), len(target_tools[k]),
                 len(lite_proposal["releases"].get(k, [])) or "不设"])
table(["发布键", "当前广告工具", "当前 lite", "string …Json", "涉及工具", "V4 工具", "V4 lite 提案"], rows)
out.append(f"八版当前名称并集 {len(names)}；V4 名称并集 {len(set(renames.values()))}；改名/合并入口 {sum(n != t for n,t in renames.items())}；不变 {sum(n == t for n,t in renames.items())}。数字只指目录，不代表原生能力验收。\n")

section("A. 全量 current name → 4.0 name（包括不变项）")
rows = []
for n in names:
    reason = "不变；符合命名规则" if n == renames[n] else "规则：动词、对象、领域、复数或大小写/表示规范化"
    if renames[n] in MERGE_PROOF: reason = MERGE_PROOF[renames[n]]
    if n in {"Connect", "ConnectToProject", "AttachToOpenProject", "CompileSoftware", "CompileAndDiagnosePlc", "CompileAndDiagnoseHmi"}:
        reason += "；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义"
    if n.startswith("Add"): reason += "；精确创建政策须 D1/L5，未验收前保持原行为并披露能力状态"
    rows.append([tick(n), tick(renames[n]), availability(n), reason + "；" + link(source(n), "源码")])
table(["当前名称", "4.0 名称", "保留发布键", "依据/合并证明"], rows)
table(["合并来源", "参数映射（仅文档）"], [[tick(n), MERGE_PARAMETERS[n]] for n in sorted(MERGE_PARAMETERS)])
out.append("每版仅以该版已有来源构造目标目录；每个来源的 action、targetKind、输出版本门禁逐项保留。Compile/Connect 的相似名字不构成同义证明。合并后的 data 由现有目录记录投影；空配方列表须按 exampleKind=sequence 过滤，不把所有示例当配方。\n")
end()

section("B. 全部 …Json 与 PlcBuildAndImport.json 的类型目标")
rows = []
for n, ps in sorted(typed.items()):
    for p, releases in sorted(ps.items()):
        oldtypes = sorted({str(tools[k][n]["inputSchema"]["properties"][p].get("type")) for k in releases})
        rows.append([tick(n), ", ".join(k for k in keys if k in releases), tick(p) + " → " + tick("spec" if p == "json" else p[:-4]),
                     "/".join(oldtypes), family(n,p), tick(shape(n,p)), link(source(n), "入口及校验调用")])
table(["当前工具", "发布键", "参数", "当前 schema 类型", "族", "4.0 类型", "来源"], rows)
totals = collections.Counter(family(n,p) for n,ps in typed.items() for p in ps)
out.append("按 (当前工具,参数) 去重：" + "；".join(f"{f}={c}" for f,c in sorted(totals.items())) + f"；共 {sum(totals.values())} 项 / {len(typed)} 个工具。argumentsJson 的 JsonElement 输入也迁名；…JsonPath 仍为文件路径。\n")
end()

section("B1. 当前输入限制原文（生成提取；迁移不得放宽）")
# Exact original contract text keeps differing release limits visible. Source
# numeric guards supplement schemas which historically described strings only.
rows = []
for n, ps in sorted(typed.items()):
    for p, releases in sorted(ps.items()):
        variants = collections.defaultdict(list)
        for k in keys:
            if k in releases:
                schema = tools[k][n]["inputSchema"]["properties"][p]
                description = schema.get("description", "")
                bounds = {key:v for key,v in schema.items() if key not in ("type", "description", "default")}
                variants[(description, json.dumps(bounds, ensure_ascii=False, sort_keys=True))].append(k)
        for (description, bounds), ks in variants.items():
            if description or bounds != "{}": rows.append([n + "." + p, ", ".join(ks), description, bounds])
table(["输入", "发布键", "当前参数约束原文", "其他 schema 约束"], rows)
guard_paths = [f for f in files if f.endswith('.cs') and (
    (f.startswith(F) and pathlib.PurePosixPath(f).name.startswith("Offline") and "Builder" in f)
    or f.startswith(L) or f in {p for p,m in source_tools.values()}
    or f in primitive_paths
    or f.startswith(E + 'Siemens/') and (f.endswith('Logic.cs') or f.endswith('Rules.cs')))]
guard_rows = []
for f in sorted(guard_paths):
    for i, line in enumerate(read(f).splitlines(), 1):
        if re.search(r"(?:const int Max|MaxDepth\s*=|MaxCharactersInDocument\s*=|if.*(?:Length|Count|count|maxItems).*(?:\d{2}|Max)|Require.*(?:Length|Count|count).*(?:\d{2}|Max)|RequireText\(.*\d{2}|ParseNames\(.*\d|Parse.*Path\(.*\d)", line):
            guard_rows.append([link(f) + ":" + str(i), tick(line.strip())])
assert guard_rows
table(["parser/策略来源:行", "原始边界表达式"], guard_rows)
out.append(f"共 {len(guard_rows)} 个边界表达式；包含第 I 步由 src/Shared/shared-native/*.props 引用的共享原语及 src/Logic/V4 校验。路径移动只改变排序/行号，不改变输入契约。\n")
end()

section("C. 当前响应族 → V4 与保留的标记事实")
marks = collections.defaultdict(collections.Counter)
sites = collections.defaultdict(list)
for p, content in engine.sources.items():
    rel = p.relative_to(root).as_posix()
    for m in re.finditer(r"// envelope: (legacy-[\w-]+)", content):
        fam = "CLI" if "/Cli/" in rel else "F4" if rel.endswith("McpServer.ToolBridge.cs") else "F3" if "/Siemens/Services/" in rel or rel.endswith("PlcSimAdvancedTools.cs") else "F2"
        marks[fam][m[1]] += 1
        sites[m[1]].append(rel)
shapes = [
    ("F1", "VersionPolicyTool 的 isError 文本/preflight", "准入→rejected-before-operation，error.code/details；无原生动作"),
    ("F2", "POCO/Meta、McpException、success=false", "领域字段→data；异常由错误分类器生成 error；message 不判断成功"),
    ("F3", "operationSuccess/status/error、执行器", "按执行证据确定 outcome；逐项结果→data.items；保留完整性与原生 verdict"),
    ("F4", "Message 中序列化 JSON/failed 文本", "CallTool 透传目标 envelope；批次逐项 envelope；无二次编码"),
    ("F5", "导出句柄 ok/InvalidParams", "data.export 与 meta.paging；缺句柄 NOT_FOUND，覆盖 ALREADY_EXISTS"),
    ("F6", "Portal 文本失败，无 meta", "按实际分支判定，边界生成 error/outcome；无法证实写入结果则 unknown"),
    ("F7", "Foundation PascalCase DTO/裸数组/V17 envelope", "data 保留原领域数据及 evidence；Executed→meta.execution，RequiresSessionReset→meta；裸数组→data.items"),
    ("CLI", "报告 ok/roundtrip/后写判定", "同 envelope、同 outcome；退出码见正文；报告正文/路径进入 data"),
]
table(["族", "当前形状", "V4 映射", "标记站点（非工具数）", "variant"],
      [[f, old, new, sum(marks[f].values()), "; ".join(v+":"+str(c) for v,c in sorted(marks[f].items())) or "0"] for f,old,new in shapes])
out.append(f"共 {sum(sum(v.values()) for v in marks.values())} 个注释站点、{len(sites)} 个 variant；未标记的手写形状仍由 Inventory-ResponseEnvelopes.py 管理。F6 无标记不代表无此类结果。\n")
table(["variant", "源码文件"], [[v, "<br>".join(link(p) for p in sorted(set(ps)))] for v,ps in sorted(sites.items())])
end()

section("D. 产品输出与程序集（读取项目属性）")
rows = []
for p, target, ks, directory in [
    (F+"TiaMcpServer.LegacyHost.csproj", "TiaMcp.FoundationHost", "14sp1–19", "runtime/v<key>/"),
    (E+"TiaMcpServer.V20.csproj", "TiaMcp.Engine.V20", "20", "runtime/v20/"),
    (E+"TiaMcpServer.V21.csproj", "TiaMcp.Engine.V21", "21", "runtime/v21/"),
]:
    tree = ET.fromstring(read(p))
    old = tree.findtext('.//AssemblyName')
    assert old == "TiaMcpServer"
    rows.append([link(p), ks, old+" → "+target, directory+target+".exe", tree.findtext('.//TargetFramework')])
table(["项目", "发布键", "AssemblyName", "4.0 安装 EXE", "框架不变"], rows)
table(["公共库/桌面项目", "当前目标框架（源码属性）"],
      [[link(p), ET.fromstring(read(p)).findtext('.//TargetFrameworks') or ET.fromstring(read(p)).findtext('.//TargetFramework')]
       for p in [L+"TiaMcp.Logic.csproj", S+"Gui/TiaOpenness.Gui.csproj", S+"Core/TiaOpenness.Core.csproj"]])
config_specs = [
    (L+"ModelContextProtocol/Builders/EcosystemFiles.cs", r'"(TIA_MCP_REPOSITORY_ROOT)"', "替换为 --bundle-root / TIA_MCP_BUNDLE_ROOT"),
    (E+"ModelContextProtocol/Tools/McpServer.Profile.cs", r"(TIA_MCP_PROFILE)", "lite/full 名称保留，名单改为 V4 数据"),
    (S+"Gui/Configuration/ClientProfiles.cs", r'"(tia-portal(?:-vm)?)"', "server key 保留，command/args 改用新产品表"),
    (E+"Cli/McpConfigInstaller.cs", r'"(mcpServers|servers|tia-portal)"', "JSON/TOML 根及 server key 保留，command/args 更新"),
    (F+"HostOptions.cs", r'"(--worker-exe|--tia-release|--tia-version|--tia-portal-location)"', "精确版本与显式 worker 输入继续支持"),
    (E+"ModelContextProtocol/Tools/EcosystemTools.cs", r'"(TIA_MCP_PLC_TOOLS_PYTHON)"', "显式 Python 优先；缺省环境改到 LocalAppData"),
]
config_rows = []
for path, pattern, change in config_specs:
    values = sorted(set(re.findall(pattern, read(path))))
    assert values, path
    config_rows.append([link(path), ", ".join(tick(v) for v in values), change])
table(["配置来源", "当前键/变量（源码提取）", "4.0 处理"], config_rows)
end()

section("E1. 当前资源定位/私有默认值与目标政策（源码定位生成）")
layout_policies = [
    (L+"ModelContextProtocol/Builders/EcosystemFiles.cs", "RepositoryRoot", "R1 祖先桥接脚本探测、旧 root 变量", "严格 bundle-root；缺资源拒绝"),
    (E+"ModelContextProtocol/Tools/McpServer.Maintenance.cs", "FindInstallRoot", "R2 四层 delivery 探测", "已知安装根 + delivery 标记"),
    (E+"Cli/SpecLoader.cs", "FindBundleRoot", "R3 十二层 templates/tools 探测", "显式根/已知锚点；未解析 __BUNDLE__ 报错"),
    (E+"Siemens/EngineRouter.cs", "FindSiblingExe", "R7 bin/bin-v20/v数字候选", "版本目录表 + 精确新产品名"),
    (E+"Cli/McpConfigInstaller.cs", "FindSiblingExe", "目标引擎缺失时使用自身", "缺版本引擎报 RESOURCE_UNAVAILABLE"),
    (S+"Gui/ConfigurationPage.cs", "FindBundleRoot", "R11 向祖先寻找包标记", "显式根或已知锚点"),
    (S+"Gui/Configuration/ConfigCore.cs", "TiaMcpServer.exe", "根无标记仍保留候选", "严格根校验、新产品目录"),
    (S+"Gui/Configuration/UpdateCheck.cs", "FindResource", "解析失败仍拼传入根", "严格资源解析，worktree 更新保护保留"),
    (S+"Client/BridgeClient.cs", "BundleLayout", "R13 相对开发 Debug/Release 猜测", "仅正式相邻部署/已知开发锚点/显式 bridgeExePath"),
    (S+"Core/Abstractions/SessionFactoryLoader.cs", "TiaOpenness.Openness", "R14 当前 Studio adapter 路径", "仍由 G3/J 验收控制，不随布局变更切换"),
    (S+"Launcher/Launcher.cs", "TiaOpenness.exe", "R12 根启动器目标", "正式根 TiaOpenness.exe 启动 runtime/studio/TiaOpenness.exe"),
    (SH+"DataLocations.cs", "TIA_MCP_DATA_DIRECTORY", "显式数据根或 bundle/data；不可写时按用途回退用户目录", "沿用数据根政策；logs 按发布键/studio 分组"),
    (SH+"InvocationJournal.cs", "DiagnosticsDirectory", "data/diagnostics 调用日志；10 MiB + 一份 previous", "P6-45 读取；P6-46 配置保留与时间窗口，审计另存 logs/audit"),
    (E+"Program.cs", "DiagLogPathLocal", "主日志经 DataLocations；启动日志仍在安装目录", "数据根 logs/<releaseKey>；只读安装沿用用户目录回退"),
    (S+"Gui/App.xaml.cs", ".crash.log", "Studio 安装目录崩溃日志", "数据根 logs/studio；只读安装沿用用户目录回退"),
    (E+"ModelContextProtocol/Tools/EcosystemTools.cs", "ecosystem-python", "包根下私有 Python 缺省", "显式解释器或 LocalAppData 环境"),
    (E+"Cli/ReportBuilders.cs", "GetWorkspaceRoot", "TMP_EXPORT/src/cwd 探测", "显式 workspace/fixture 根"),
    (E+"Cli/HmiTemplateBuilder.cs", "TIA_MCP_AI_PACK", "私有 HMI 模板默认输入", "显式模板路径"),
    (L+"ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs", "TMP_EXPORT", "私有套件夹具探测", "显式 fixture 根，workspaceRoot 不猜测"),
    (E+"ModelContextProtocol/Tools/OnlineToolPolicy.cs", "WithAutoOffline", "错误文本触发下线再执行", "D1/L5 后 OFFLINE_REQUIRED，不重试"),
    (E+"Siemens/Services/OnlineDownloadService.cs", "ApplyConfiguration", "配置失败/候选路线继续", "D1/L5 后显式路线，失败/未知即停止"),
]
policy_rows = []
for path, needle, current, target in layout_policies:
    lines = read(path).splitlines()
    line = next(i for i,l in enumerate(lines, 1) if needle in l)
    policy_rows.append([link(path)+":"+str(line)+" "+tick(needle), current, target])
table(["当前位置/定位词", "当前事实", "4.0 目标"], policy_rows)
end()

section("E. 布局、构建、打包、校验、Studio 和文档修改位置（生成扫描）")
# Scan tracked first-party text, including tests/metadata needing regeneration.
# Exclude this proposal to avoid self-referential line churn and historical data.
SCAN_TERMS = {
    "产品": r"TiaMcpServer(?:\.exe|\.dll|\.exe\.config|\.deps\.json|\.runtimeconfig\.json|[\"'<])|TiaMcpConfigurator",
    "根定位": r"TIA_MCP_REPOSITORY_ROOT|FindBundleRoot|FindInstallRoot|FindSiblingExe|BundleLayout|RepositoryRoot",
    "写入/工作区": r"ecosystem-python|DiagLogPathLocal|startup\.log|\.crash\.log|GetWorkspaceRoot|TMP_EXPORT|TIA_MCP_AI_PACK",
}
scan = []
excluded = ("manifest/history/", "reference/siemens-openness/", "docs/development/phase6-review.md", "scripts/generate/Generate-Phase6Plan.py")
for f in sorted(files):
    if f.startswith(excluded) or f == "CHANGELOG.md" or f.startswith("third_party/"): continue
    if pathlib.PurePosixPath(f).suffix.lower() not in {".cs", ".csproj", ".props", ".targets", ".ps1", ".psm1", ".py", ".md", ".json", ".yml", ".yaml", ".slnx", ".config", ".gitignore", ".bat", ".cmd", ".sh", ".toml", ".xml", ".xaml"}: continue
    content = read(f)
    hits = {kind: [str(i) for i,l in enumerate(content.splitlines(), 1) if re.search(pattern, l)] for kind,pattern in SCAN_TERMS.items()}
    hits = {k:v for k,v in hits.items() if v}
    if not hits: continue
    treatment = "修改引用并回归"
    if f.startswith("manifest/") or f.endswith("ToolUsageData.json") or f.endswith("tool-matrix.md"): treatment = "仅运行所属生成器更新；历史契约归档，不手改哈希"
    elif f.startswith(("docs/releases/", "docs/archive/", "docs/development/evidence/")): treatment = "历史证据只读保留，不作为 V4 改写目标"
    elif f.startswith("docs/development/"): treatment = "更新现行说明；历史阶段证据保留并注明被 V4 决策取代"
    scan.append([link(f, f), "; ".join(k+":"+",".join(v) for k,v in hits.items()), treatment])
assert any("Package-Release.py" in r[0] for r in scan)
assert any("Validate-Bundle.ps1" in r[0] for r in scan)
assert any("ClientProfiles.cs" in r[0] or "ConfigCore.cs" in r[0] for r in scan)
table(["文件", "类别:全部命中行", "实施方式"], scan)
out.append(f"共 {len(scan)} 个候选文件。扫描覆盖 git ls-files 中第一方文本的产品基名、根解析及写入/工作区定位词；历史发布记录、第三方资料和本页自身不作改写目标。间接引用由每个路径任务的构建、布局矩阵和必需文件清单验收补足，不能把文本命中当成自动替换授权。\n")
end()

section("F. V20/V21 lite 数据提案（每项均有现有调用示例）")
table(["4.0 名称", "当前示例入口", "选择理由", "版本"],
      [[tick(r["name"]), tick(r["currentName"]), r["reason"], "20, 21"] for r in lite_proposal["releases"]["21"]])
assert lite_proposal["releases"]["20"] == lite_proposal["releases"]["21"]
out.append("数据文件：" + link("src/Logic/ModelContextProtocol/ToolProfiles.resx", "ToolProfiles.resx") + "。Catalog JSON 按 contractVersion、releaseKey 记录 V4/current/source 名称、profiles 和 arguments；参数示例取自 reference/tool-examples/calls.json，按当前契约转换并逐版验证。Foundation 继续不设 lite。\n")
end()

section("G. 完整引擎契约迁移任务的工具文件所有权")
table(["任务", "工具源文件", "当前注册入口数"],
      [[task, "<br>".join(link(p) for p in sorted(owners) if owners[p] == task),
        sum(owners[p] == task for p,m in source_tools.values())] for task in MIGRATION_GROUPS])
out.append("每个完整引擎注册入口恰有一个文件所有者；同 stem 的 Service 路径在 H 表展开。本领域独占规则随该任务，公共 src/Engine/Siemens/Portal 与基础设施由 P6-24 串行集成。Foundation 由 P6-08 单独负责；公共 DTO 与项目文件不归并行领域任务编辑。\n")
end()

section("H. 全部阶段 6 任务的当前路径与所有权")
table(["任务", "当前源码/文档/验证入口（仓库根相对路径）"],
      [[f'<a id="phase6-path-{task.lower()}"></a>{task}', "<br>".join(link(p, p) for p in TASK_PATHS[task])] for task in sorted(TASK_PATHS)])
out.append("共 48 项。目录项是所有权定位范围，不是整目录修改授权；每份实施说明仍须列出精确文件。03–06 只在 src/Logic/V4 与所属测试目录新增本族 DTO/转换/测试，表内现有 parser 是只读依据。07、24 串行接线公共文件，08–23 仅改 G 表工具、对应 Service 和独占规则，共享原语留给 27–34 串行政策任务。\n")
out.append("44 拥有引擎/Foundation 执行前审批门、src/Shared 内新增的当前用户命名管道协议、src/Studio/Core 与 Gui 的审批服务/视图/设置及 V4 拒绝详情；45 拥有调用日志脱敏投影与 Core 读取器/Gui 调用面板，不改变原生日志调用顺序；46 拥有 src/Shared 内新增审计写入/校验、InvocationJournal 保留策略、CLI/Studio 校验入口；47 拥有现有 doctor 的复用适配、Gui 体检视图与诊断包。四项的新增文件由各自任务固定名称，交叉文件按依赖串行集成。48 拥有把 RenderPlcVisualDiff 的梯形图布局/SVG 抽成共享逻辑、八版单块出图与图册工具（完整引擎与 Foundation 同一实现）及工作台入口；RenderPlcVisualDiff 输出不变。\n")
out.append("35/41 的新快照及旧基线归档位置按正文第 7 节；40 的 ToolUsageData/tool-matrix、41 的 manifest 产物只运行所属生成器。P6-43 仅更新新发布说明，不改历史发布事实。\n")
end()

section("I. 已合并的第 I 步、V4 类型与原生验收边界")
table(["任务", "当前服务 / 原语", "再生成的静态证据", "原生验收"],
      [[task.upper(), "<br>".join(link(E+"Siemens/Services/"+s+"Service.cs") for s in services.split()) + "<br>" + link(A+"Native/"+primitive),
        link("docs/development/evidence/"+task+"-native-evidence.json", "accepted: true（静态）"), "NOT RUN"]
       for task, (services, primitive) in step_i.items()])
out.append("五项已合并；共享原语清单从 " + link(SH+"TiaSharedAdapterPaths.props") + " 的 shared-native/*.props 提取，开关默认 false。静态 accepted 不等于 L5，G3/J 与发布门槛不变。\n")
table(["已完成项目", "当前文件"], [["P6-02：未接线的 V4 信封/错误/分页/批次/计划与单一序列化校验", "<br>".join(link(p) for p in sorted(files) if p.startswith(L+"V4/") and p.endswith(".cs"))],
      ["D334：源码目录迁移完成；产品名/运行目录仍待 36–39", link("docs/development/repository-layout.md")]])
out.append("台账已核对：" + "、".join("P6-"+f for f in behavior_families) + "，八族全部 NOT RUN；P6-PRODUCT 也为 NOT RUN。未运行任何原生调用。\n")
end()

def self_test():
    validate_parameter_transition("Keep", "Keep", "Keep", {"valuesJson": "string"}, {"valuesJson"}, False, {})
    validate_parameter_transition("Keep", "Keep", "Keep", {"values": "AttributeMap<Scalar>"}, {"valuesJson"}, True, {"valuesJson": "AttributeMap<Scalar>"})
    validate_parameter_transition("Old", "New", "New", {"spec": "UdtSpec"}, {"specJson"}, True, {"specJson": "UdtSpec"})
    validate_parameter_transition("ManagePlcCertificate", "ManagePlcCertificate", "ManagePlcCertificate",
                                  {"subjectAlternativeNames": "SubjectAlternativeName[]"}, {"subjectAlternativeNamesJson"}, True,
                                  {"subjectAlternativeNamesJson": "SubjectAlternativeName[]"})
    validate_parameter_transition("ManageSafetyFunction", "ManageSafetyFunction", "ManageSafetyFunction",
                                  {"properties": "AttributeMap<Scalar>", "signals": "string[]"}, {"propertiesJson"}, True,
                                  {"propertiesJson": "AttributeMap<Scalar>"})
    for actual, v4 in (({"valuesJson": "string"}, True), ({"values": "string"}, True), ({"values": "AttributeMap<Scalar>"}, False)):
        try: validate_parameter_transition("Keep", "Keep", "Keep", actual, {"valuesJson"}, v4, {"valuesJson": "AttributeMap<Scalar>"})
        except AssertionError: pass
        else: raise AssertionError("typed transition unexpectedly passed")
    rejected = 0
    for mutate in (
        lambda m: m.pop(names[0]),
        lambda m: m.update({names[0]: m[names[1]]}),
        lambda m: m.update({names[0]: "GetJsonResult"}),
        lambda m: m.update({"CompileSoftware": m["CompileAndDiagnosePlc"]}),
    ):
        broken = dict(renames)
        mutate(broken)
        try: validate_mapping(broken, tools)
        except AssertionError: rejected += 1
        else: raise AssertionError("negative mapping check unexpectedly passed")
    assert rejected == 4
    for k in keys:
        assert set(target_tools[k]) == {renames[n] for n in tools[k]}
    for n,ps in typed.items():
        for p in ps: assert shape(n,p)
    missing = dict(TASK_PATHS)
    missing.pop("P6-47")
    stale = dict(TASK_PATHS)
    stale["P6-44"] = ["tools/tiaportal-mcp/src/TiaMcpServer/Program.cs"]
    for broken in (missing, stale):
        try: validate_inventory(broken)
        except AssertionError: rejected += 1
        else: raise AssertionError("negative inventory check unexpectedly passed")
    assert rejected == 6
    print("Self-check: 6 negative cases rejected; 8 release mappings, typed coverage, lite examples, 48 task paths, 5 static native proofs and 8 NOT RUN behavior families passed.")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='compare generated text without writing (ignore checkout CRLF)')
    parser.add_argument('--self-test', action='store_true', help='exercise coverage and invalid-map rejection')
    args = parser.parse_args()
    if args.self_test: self_test()
    doc = root / 'docs/development/phase6-review.md'
    content = doc.read_text(encoding='utf-8')
    begin, endmark = '<!-- phase6-generated:start -->', '<!-- phase6-generated:end -->'
    a = content.index(begin) + len(begin)
    b = content.index(endmark, a)
    generated = content[:a] + '\n\n' + '\n'.join(out) + '\n' + content[b:]
    outputs = {doc: generated, root / RESOURCE: resource_text()}
    for path, value in outputs.items():
        expected = value.encode('utf-8')
        if args.check:
            assert path.read_bytes().replace(b'\r\n', b'\n') == expected, 'stale generated output: ' + str(path.relative_to(root))
        else:
            path.write_bytes(expected)
    print(f"{'Checked' if args.check else 'Generated'}: {len(names)} current names, {len(set(renames.values()))} V4 names, {sum(totals.values())} typed inputs, {len(lite_proposal['releases']['21'])} lite tools per full release, {len(scan)} layout files.")

if __name__ == '__main__': main()
