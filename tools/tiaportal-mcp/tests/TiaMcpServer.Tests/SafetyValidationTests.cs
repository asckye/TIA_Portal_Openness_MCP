using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-③ (2.7.42) pure logic: Safety Validation Assistant group paths, activation test / group / safety function / condition
    // requests, export / import option flags and condition values.
    internal static class SafetyValidationTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            // ---- catalogues (pinned member by member by SafetyValidationShapeChecks) ----
            check(SafetyValidationLogic.ExportOptions.SequenceEqual(new[] { "None", "WithDefaults", "WithReadOnly" }) && SafetyValidationLogic.DocumentInfoOptions.Length == 5 && SafetyValidationLogic.ImportOptions.Length == 4
                && SafetyValidationLogic.ActivationTestImportOptions.SequenceEqual(new[] { "None", "Override", "Rename", "SkipInactiveCultures", "ActivateInactiveCultures" }) && SafetyValidationLogic.SignalUsages.Length == 3 && SafetyValidationLogic.TestStates.Length == 3
                && SafetyValidationLogic.ConditionValues.SequenceEqual(new[] { "FALSE", "TRUE", "NotRelevant" }) && SafetyValidationLogic.ValidationStates.SequenceEqual(new[] { "Okay", "Error" }), "safety validation: enum catalogues");
            check(SafetyValidationLogic.ActivationTestActions.Length == 12 && SafetyValidationLogic.GroupActions.Length == 5 && SafetyValidationLogic.SafetyFunctionActions.Length == 11 && SafetyValidationLogic.ConditionActions.Length == 5, "safety validation: action catalogues");

            // ---- group path / flags ----
            check(SafetyValidationLogic.ParseGroupPath("").Length == 0 && SafetyValidationLogic.ParseGroupPath("[]").Length == 0 && SafetyValidationLogic.ParseGroupPath("[\"Line 1\",\"Cell A\"]").SequenceEqual(new[] { "Line 1", "Cell A" }), "safety validation: group paths");
            check(Fails<ArgumentException>(() => SafetyValidationLogic.ParseGroupPath("{}")) && Fails<ArgumentException>(() => SafetyValidationLogic.ParseGroupPath("[\" x\"]")) && Fails<ArgumentException>(() => SafetyValidationLogic.ParseGroupPath("[\"\"]")), "safety validation: bad group paths refused");
            check(SafetyValidationLogic.ParseFlags("", SafetyValidationLogic.ExportOptions, "exportOptions").SequenceEqual(new[] { "None" }) && SafetyValidationLogic.ParseFlags("WithDefaults|WithReadOnly", SafetyValidationLogic.ExportOptions, "exportOptions").Length == 2, "safety validation: export flags joined with |");
            check(Fails<ArgumentException>(() => SafetyValidationLogic.ParseFlags("WithDefaults|WithDefaults", SafetyValidationLogic.ExportOptions, "exportOptions")) && Fails<ArgumentException>(() => SafetyValidationLogic.ParseFlags("All", SafetyValidationLogic.ExportOptions, "exportOptions")), "safety validation: duplicate / unknown flags refused");

            // ---- activation tests ----
            SafetyValidationLogic.ActivationTestRequest A(string action, string name = "T1", string device = "", string source = "", string master = "", string value = "", string file = "", string export = "", string document = "", string import = "", bool confirm = false, bool dryRun = true)
                => SafetyValidationLogic.ValidateActivationTestRequest(action, name, device, source, master, value, file, export, document, import, confirm, dryRun);
            check(!A("read").Writes && !A("checkValidity").Writes && !A("create", device: "PLC_1").Writes && A("create", device: "PLC_1", dryRun: false).Writes, "safety validation tests: read / validity / preview never write");
            var export = A("export", file: "C:/out/T1.xml", export: "WithDefaults|WithReadOnly", document: "ExportSetting|CreatedTimeStamp", dryRun: false);
            check(export.WritesFile && !export.Writes && export.ExportFlags.Length == 2 && export.DocumentFlags.SequenceEqual(new[] { "ExportSetting", "CreatedTimeStamp" }), "safety validation tests: export writes a file, not the project");
            check(A("generateReport", file: "C:/out/T1.xlsx", dryRun: false).WritesFile && A("import", name: "", file: "C:/in/T.xml", import: "Rename", dryRun: false).ImportOption == "Rename" && A("import", name: "", file: "C:/in/T.xml").ImportOption == "None", "safety validation tests: report / import options");
            check(A("createFromTest", source: "T0", dryRun: false).Writes && A("createFromMasterCopy", master: "Folder/Copy", dryRun: false).Writes && A("rename", value: "T2", dryRun: false).Writes && A("setAuthor", value: "me", dryRun: false).Writes && A("changeEvaluationDevice", device: "PLC_2", dryRun: false).Writes, "safety validation tests: write actions");
            check(A("delete", confirm: true, dryRun: false).Writes && !A("delete").Writes, "safety validation tests: delete with confirmation");
            check(Fails<ArgumentException>(() => A("delete", dryRun: false)), "safety validation tests: delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => A("create")) && Fails<ArgumentException>(() => A("createFromTest")) && Fails<ArgumentException>(() => A("rename")) && Fails<ArgumentException>(() => A("export")) && Fails<ArgumentException>(() => A("import", name: "T1", file: "C:/in/T.xml")), "safety validation tests: missing arguments refused");
            check(Fails<ArgumentException>(() => A("read", device: "PLC_1")) && Fails<ArgumentException>(() => A("read", export: "None")) && Fails<ArgumentException>(() => A("export", file: "C:/o.xml", import: "Override")) && Fails<ArgumentException>(() => A("import", name: "", file: "C:/i.xml", import: "Merge")), "safety validation tests: stray arguments refused");
            check(Fails<ArgumentException>(() => A("run")) && Fails<ArgumentException>(() => A("read", name: "")), "safety validation tests: unknown action / empty name refused");

            // ---- groups ----
            check(!SafetyValidationLogic.ValidateGroupRequest("read", "", "", "", false, true).Writes && SafetyValidationLogic.ValidateGroupRequest("create", "G1", "", "", false, false).Writes && SafetyValidationLogic.ValidateGroupRequest("rename", "G1", "", "G2", false, false).Writes
                && SafetyValidationLogic.ValidateGroupRequest("createFromMasterCopy", "G1", "Copies/G", "", false, false).Writes && SafetyValidationLogic.ValidateGroupRequest("delete", "G1", "", "", true, false).Writes, "safety validation groups: actions");
            check(Fails<ArgumentException>(() => SafetyValidationLogic.ValidateGroupRequest("create", "", "", "", false, true)) && Fails<ArgumentException>(() => SafetyValidationLogic.ValidateGroupRequest("rename", "G1", "", "", false, true)) && Fails<ArgumentException>(() => SafetyValidationLogic.ValidateGroupRequest("delete", "G1", "", "", false, false)) && Fails<ArgumentException>(() => SafetyValidationLogic.ValidateGroupRequest("read", "G1", "", "G2", false, true)), "safety validation groups: gates");

            // ---- safety functions ----
            SafetyValidationLogic.SafetyFunctionRequest F(string action, string name = "SF_1", string source = "", string props = "{}", string file = "", string export = "", string document = "", string import = "", bool confirm = false, bool dryRun = true)
                => SafetyValidationLogic.ValidateSafetyFunctionRequest(action, name, source, props, file, export, document, import, confirm, dryRun);
            check(!F("read", name: "").Writes && !F("checkValidity").Writes && !F("checkTraceValidity").Writes && F("create", name: "", props: "{\"testName\":\"STO\",\"description\":\"d\"}", dryRun: false).Writes && F("createFrom", name: "", source: "SF_1", dryRun: false).Writes, "safety functions: read / validity / create");
            check(F("update", props: "{\"testName\":\"STO2\"}", dryRun: false).Writes && F("resetTestResult", dryRun: false).Writes && F("setTrace", props: "{\"isTraced\":true,\"pretriggerTime\":2000,\"recordingDuration\":5000,\"signals\":[\"Controlmode\"]}", dryRun: false).Writes, "safety functions: update / reset / trace");
            check(F("export", file: "C:/out/sf.xml", dryRun: false).WritesFile && F("import", name: "", file: "C:/in/sf.xml", import: "SkipInactiveCultures", dryRun: false).ImportOption == "SkipInactiveCultures" && F("delete", confirm: true, dryRun: false).Writes, "safety functions: export / import / delete");
            check(Fails<ArgumentException>(() => F("update")) && Fails<ArgumentException>(() => F("update", props: "{\"name\":\"x\"}")) && Fails<ArgumentException>(() => F("setTrace", props: "{\"testName\":\"x\"}")) && Fails<ArgumentException>(() => F("read", props: "{\"testName\":\"x\"}")), "safety functions: property key gates");
            check(Fails<ArgumentException>(() => F("createFrom", name: "")) && Fails<ArgumentException>(() => F("delete", dryRun: false)) && Fails<ArgumentException>(() => F("import", name: "", file: "C:/i.xml", import: "Rename")) && Fails<ArgumentException>(() => F("update", name: "", props: "{\"testName\":\"x\"}")), "safety functions: missing arguments refused");

            // ---- conditions ----
            SafetyValidationLogic.ConditionRequest C(string action, int index = -1, string device = "", string usage = "", string signal = "", string props = "{}", bool dryRun = true)
                => SafetyValidationLogic.ValidateConditionRequest(action, index, device, usage, signal, props, dryRun);
            check(!C("read").Writes && !C("read", 0).Writes && !C("checkValidity", 0).Writes && C("create", device: "PLC_1", usage: "InputCondition", signal: "E-Stop", props: "{\"initialInput\":true,\"executedInput\":false}", dryRun: false).Writes, "conditions: read / validity / create");
            check(C("update", 1, props: "{\"comment\":\"c\",\"response\":\"TRUE\",\"signalUsage\":\"Response\"}", dryRun: false).Writes && C("delete", 2, dryRun: false).Writes, "conditions: update / delete by index");
            check(SafetyValidationLogic.ParseConditionValue(JsonValue.Create(true), "response").Flag == true && SafetyValidationLogic.ParseConditionValue(JsonValue.Create("NotRelevant"), "response").EnumName == "NotRelevant", "conditions: bool and ConditionValue names");
            check(Fails<ArgumentException>(() => SafetyValidationLogic.ParseConditionValue(JsonValue.Create(1), "response")) && Fails<ArgumentException>(() => SafetyValidationLogic.ParseConditionValue(JsonValue.Create("true"), "response")), "conditions: other value shapes refused");
            check(Fails<ArgumentException>(() => C("create", 0, "PLC_1", "Response", "S")) && Fails<ArgumentException>(() => C("create", device: "PLC_1", usage: "Trigger", signal: "S")) && Fails<ArgumentException>(() => C("update", props: "{\"comment\":\"c\"}")) && Fails<ArgumentException>(() => C("update", 0)), "conditions: index / usage / property gates");
            check(Fails<ArgumentException>(() => C("update", 0, props: "{\"name\":\"x\"}")) && Fails<ArgumentException>(() => C("read", 0, device: "PLC_1")) && Fails<ArgumentException>(() => C("delete", -2)), "conditions: stray arguments refused");
            // 2.7.43: NotRelevant on an input owned by the signal usage is refused before anything is written (TIA refused it natively
            // after Comment / SignalName were already written; the following condition-level CheckValidity took TIA V21 down)
            check(SafetyValidationLogic.OwnedInputs("OperatingMode").SequenceEqual(new[] { "executedInput" }) && SafetyValidationLogic.OwnedInputs("InputCondition").SequenceEqual(new[] { "initialInput", "executedInput" }) && SafetyValidationLogic.OwnedInputs("Response").SequenceEqual(new[] { "response" }), "conditions: owned inputs per signal usage");
            check(Fails<ArgumentException>(() => C("create", device: "PLC_1", usage: "InputCondition", signal: "S", props: "{\"executedInput\":\"NotRelevant\"}")) && Fails<ArgumentException>(() => C("create", device: "PLC_1", usage: "Response", signal: "S", props: "{\"response\":\"NotRelevant\"}")), "conditions: NotRelevant on an owned input refused at create");
            check(!Fails<ArgumentException>(() => C("create", device: "PLC_1", usage: "InputCondition", signal: "S", props: "{\"response\":\"NotRelevant\"}")) && !Fails<ArgumentException>(() => C("update", 0, props: "{\"executedInput\":\"NotRelevant\"}")), "conditions: NotRelevant allowed on inputs the usage does not own (update without usage is checked by the portal)");
            check(Fails<ArgumentException>(() => C("update", 0, props: "{\"signalUsage\":\"OperatingMode\",\"executedInput\":\"NotRelevant\"}")) && !Fails<ArgumentException>(() => C("update", 0, props: "{\"signalUsage\":\"OperatingMode\",\"executedInput\":true}")), "conditions: update with a new usage is checked against that usage");
            check(Fails<ArgumentException>(() => SafetyValidationLogic.ValidateConditionValues("InputCondition", (JsonObject)JsonNode.Parse("{\"initialInput\":\"NotRelevant\"}")!)) && !Fails<ArgumentException>(() => SafetyValidationLogic.ValidateConditionValues("OperatingMode", (JsonObject)JsonNode.Parse("{\"initialInput\":\"NotRelevant\"}")!)), "conditions: ValidateConditionValues per usage");
        }
    }
}
