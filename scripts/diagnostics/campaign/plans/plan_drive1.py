# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; DRV = "MCP_S120"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
AX = dict(devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]))
plan = [
    S("ReadDriveObjects", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), includeTelegrams=True, includeFunctions=True, includeTechnologyExtensions=True, includeDcc=True, expect="any", keys=["records"]),
    S("ReadDriveParameters", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, driveObjectIndex=0, source="offline", namesJson=J(["r2", "p1082"]), numbersJson="[]", includeBits=False, includeEnumValues=False, offset=0, limit=10, includeValue=True, expect="any", keys=["records"]),
    S("ManageDriveTelegrams", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, driveObjectIndex=0, action="read", telegramType="", telegramNumber=0, inputSize=0, outputSize=0, direction="", size=0, keepOriginalAddress=False, softwarePath="", expect="any", keys=["records"]),
    S("ManageDriveFunctions", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, driveObjectIndex=0, action="read", valueJson="null", dryRun=True, expect="any"),
    S("ManageDriveSecurity", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, driveObjectIndex=0, action="read", password="", dryRun=True, expect="any"),
    S("ManageTechnologyExtensions", action="read", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, driveObjectIndex=0, identifier="", filePath="", confirmUninstallInUse=False, includeParameters=False, dryRun=True, expect="any", keys=["records"]),
    S("ManageDriveHardwareModule", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1", "MCP_MM"]), action="read", typeIdentifier="", positionNumber=-1, dryRun=True, expect="any", keys=["before"]),
    S("ManageDriveSafetyAcceptanceTest", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), action="read", identifier="", active=False, filePath="", fileOperation="", dryRun=True, expect="any"),
    S("ManageStartdriveParameter", devicePathJson=J([DRV]), itemPathJson=J(["驱动轴_1"]), driveObjectNumber=0, parameter="p1082", action="read", valueJson="null", dryRun=True, driveObjectIndex=0, expect="any"),
    # DCC on the V5.2 axis
    S("ReadDccCharts", **AX, driveObjectNumber=0, driveObjectIndex=-1, chartPath="", maxDepth=2, includeBlocks=True, includePins=False, includeLibraries=False, expect="any", keys=["records"]),
    S("ManageDccChart", **AX, driveObjectNumber=0, chartName="MCP_Chart", action="create", dryRun=False, expect="any", keys=["createdName"]),
    S("ManageDccBlock", **AX, chartPath="MCP_Chart", blockName="", action="create", blockType="ADD", dryRun=False, expect="any", keys=["createdName"]),
    S("ReadDccObject", **AX, driveObjectNumber=0, objectPathJson=J([{"property": "Charts", "name": "MCP_Chart"}, {"property": "Blocks", "name": "add_1"}]), offset=0, limit=20, driveObjectIndex=-1, expect="any"),
    S("ManageDccPin", **AX, chartPath="MCP_Chart", blockName="add_1", pinName="X1", action="update", propertiesJson=J({"Value": 2.5}), dryRun=False, expect="any", note="2.7.45: Single refusal; 2.7.46 converts", keys=["after"]),
    S("ManageDccBlock", **AX, chartPath="MCP_Chart", blockName="add_1", action="update", sequenceIndex=0, dryRun=False, expect="any", note="2.7.45 refuses sequenceIndex-only"),
    S("ManageDccChart", **AX, driveObjectNumber=0, chartName="MCP_Chart", action="delete", confirmDelete=True, dryRun=False, expect="any", keys=["verifiedAbsent"]),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_drive1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
