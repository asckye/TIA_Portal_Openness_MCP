namespace TiaMcpServer.ModelContextProtocol
{
    // One embedded source for initialization, on-demand guidance and preflight; no TIA calls.
    public static class StartdriveGuidance
    {
        public const string Topic = "startdrive-bico";
        public const string Precaution = @"Read GetAuthoringGuide('startdrive-bico') and the matching V20/V21 official example first. Preflight validates arguments/session only; it does not establish native crash safety. ManageStartdriveParameter action=read still reads native Value when dryRun=true; write previews also read the current Value. p2051[0] has a reported TIA crash (target details and native retest pending); historical G120C r2139 and unwired p840[0] reads also crashed. Do not automatically replay a reported failing read or expand its bits. On process/channel loss stop native calls, use GetState and ReadNativeInvocationLog, and preserve TIA/Windows logs. A missing RETURNED record does not prove causality.";
        public const string Text = @"STARTDRIVE / BICO CALLING GUIDE (V20/V21; native acceptance stated separately)

Scope: these parameter tools are exposed by the V20/V21 full engines. V14 SP1-V19 foundation catalogs do not currently expose them. Query the running engine; do not infer tool availability from an installed SDK. Offline tools read the project configuration, not the live drive's process values. ReadOnlineDriveParameters requires a separately intended online target.

1. GetState and the current project tree identify the intended project. Obtain exact devicePathJson and itemPathJson name arrays for the drive/control-unit host; never guess a device from a stale example. If the drive object is unknown, use ReadDriveObjects with includeTelegrams/includeFunctions/includeTechnologyExtensions/includeDcc=false. This is still a native query, not a proof that later parameter reads are safe. Use its driveObjectIndex when DriveObjectNumber cannot be retrieved; do not supply both a nonzero number and an index.
2. Read GetRecipe('startdrive-bico-read') and use FindTools('ManageStartdriveParameter'). The example below uses placeholders and p1070[0]; substitute the operator's actual intended parameter. Never use a known crash report as an automatic test target.
3. PreflightToolCall with the exact planned arguments validates names/types/session without calling TIA. READY means the call can bind, not that Siemens has accepted it. Then make one requested read, subject to the known-failure note below. CallTool argumentsJson is an argument object or a JSON string containing it; devicePathJson/itemPathJson themselves are strings containing JSON arrays for direct tools/call.

Example argument object (replace placeholders; this is not an instruction to execute):
{
  ""devicePathJson"": ""[\""<exact drive device>\""]"",
  ""itemPathJson"": ""[\""<exact drive/control unit item>\""]"",
  ""driveObjectNumber"": 0,
  ""driveObjectIndex"": 0,
  ""parameter"": ""p1070[0]"",
  ""action"": ""read"",
  ""dryRun"": true
}

Official API mapping: locate the sink through DriveObject.Parameters.Find(exactName), read its Value once, and identify a returned DriveParameter by its Name/ParameterText. This server uses ReadParameters only when the primary view has no matching entry. It does not automatically retry an exception in another view. The returned BICO reference describes the source connection; it does not read that source's live Value.

Result paths: meta.before.value.bicoSource is the source name; parameterText is its description. A scalar is under meta.before.value.value. Null remains null: the official example says an invalid connection or an Openness-inaccessible source can return null; do not invent a source or equate null with an unwired sink. The exact-read row contains name, parameterClass and value. Detailed properties require an explicit ReadDriveParameters request. For metadata inspection choose includeValue=false, includeBits=false and includeEnumValues=false; that omits Value but still invokes native metadata getters and does not guarantee crash safety.

Read GetAuthoringGuide('startdrive-bico') and the matching V20/V21 official example first. Preflight validates arguments/session only; it does not establish native crash safety. ManageStartdriveParameter action=read still reads native Value when dryRun=true; write previews also read the current Value. p2051[0] has a reported TIA crash (target details and native retest pending); historical G120C r2139 and unwired p840[0] reads also crashed. Do not automatically replay a reported failing read or expand its bits. On process/channel loss stop native calls, use GetState and ReadNativeInvocationLog, and preserve TIA/Windows logs. A missing RETURNED record does not prove causality.

dryRun controls writes, not reads. Never turn a requested read into action=write, reconnect automatically, or try parameter-name variants after an IPC failure. Preserve the engine version, TIA/Startdrive update, drive model/firmware, exact arguments, and nativeCallId BEFORE/RETURNED/THREW records. Existing tests verify binding, call routing and guidance delivery; they do not establish that p2051[0] no longer crashes the reported installation.

Official examples (select the engine's actual version):
V20: https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters
V21: https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters
";
        public static bool AppliesTo(string tool) => tool == "ManageStartdriveParameter"
            || tool == "ReadDriveParameters" || tool == "ReadOnlineDriveParameters";
    }
}
