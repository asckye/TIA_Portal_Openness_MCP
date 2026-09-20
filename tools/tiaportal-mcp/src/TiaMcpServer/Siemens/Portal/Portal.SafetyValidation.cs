using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.SafetyValidationLogic;
#if !TIA_V20
using Siemens.Engineering.SafetyValidation;
#endif

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): typed Safety Validation Assistant option package (V21 Siemens.Engineering.SafetyValidation.dll; the
    // namespace does not exist on V20, so every tool answers NotSupportedOnVersion there). Official entry: Project.GetService<
    // SafetyValidationAssistant>() -> ActivationTests / ActivationTestGroups (nested user groups) and the DeviceQuery service listing
    // the evaluation devices; ActivationTest -> SafetyFunctions -> Conditions / TraceConfiguration; TestValidity.CheckValidity() and
    // ActivationTestPrintout.Generate(.xlsx) are services of the respective objects.
    public partial class Portal
    {
#if TIA_V20
        private static PortalException SafetyValidationUnavailable() => new PortalException(PortalErrorCode.NotSupportedOnVersion, "Siemens.Engineering.SafetyValidation (Safety Validation Assistant) is a V21 addition; absent on V20.");
        public ResponseMessage ReadSafetyActivationTests(string groupPathJson = "[]", string name = "", bool includeSafetyFunctions = false, bool includeConditions = false, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadSafetyActivationTests", meta => { Logic.ParseGroupPath(groupPathJson); HardwareServicesLogic.ValidatePagination(offset, limit); throw SafetyValidationUnavailable(); });
        public ResponseMessage ManageSafetyActivationTest(string name, string action = "read", string groupPathJson = "[]", string evaluationDeviceName = "", string sourceName = "", string masterCopyPath = "", string libraryName = "", string newValue = "", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyActivationTest", meta => { Logic.ValidateActivationTestRequest(action, name, evaluationDeviceName, sourceName, masterCopyPath, newValue, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun); throw SafetyValidationUnavailable(); });
        public ResponseMessage ManageSafetyActivationTestGroup(string action = "read", string groupPathJson = "[]", string name = "", string masterCopyPath = "", string libraryName = "", string newName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyActivationTestGroup", meta => { Logic.ValidateGroupRequest(action, name, masterCopyPath, newName, confirmDelete, dryRun); throw SafetyValidationUnavailable(); });
        public ResponseMessage ManageSafetyFunction(string activationTest, string action = "read", string groupPathJson = "[]", string name = "", string sourceName = "", string propertiesJson = "{}", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyFunction", meta => { Logic.ValidateSafetyFunctionRequest(action, name, sourceName, propertiesJson, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun); throw SafetyValidationUnavailable(); });
        public ResponseMessage ManageSafetyFunctionCondition(string activationTest, string safetyFunction, string action = "read", string groupPathJson = "[]", int index = -1, string deviceName = "", string signalUsage = "", string signalName = "", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageSafetyFunctionCondition", meta => { Logic.ValidateConditionRequest(action, index, deviceName, signalUsage, signalName, propertiesJson, dryRun); throw SafetyValidationUnavailable(); });
#else
        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private SafetyValidationAssistant RequireSafetyValidationAssistant()
            => _project!.GetService<SafetyValidationAssistant>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyValidationAssistant is not provided by this project (Safety Validation Assistant not installed / licensed, or no F-CPU).");
        // groupPathJson walks ActivationTestGroups -> ActivationTestUserGroup.Groups; an empty path is the assistant root.
        private static (ActivationTestComposition Tests, ActivationTestUserGroupComposition Groups, ActivationTestUserGroup? Group) ExactActivationTestScope(SafetyValidationAssistant sva, string[] groupPath)
        {
            ActivationTestComposition tests = sva.ActivationTests; ActivationTestUserGroupComposition groups = sva.ActivationTestGroups; ActivationTestUserGroup? group = null;
            foreach (var part in groupPath)
            {
                group = groups.Find(part) ?? throw new PortalException(PortalErrorCode.NotFound, "Activation test group not found: " + string.Join("/", groupPath) + " (missing '" + part + "').");
                tests = group.ActivationTests; groups = group.Groups;
            }
            return (tests, groups, group);
        }
        private static ActivationTest ExactActivationTest(ActivationTestComposition tests, string name)
            => tests.Find(name) ?? throw new PortalException(PortalErrorCode.NotFound, "Activation test not found: " + name);
        // SafetyFunction.Name is generated and read-only; TestName is the editable title. Name matches first, then an unambiguous TestName.
        private static SafetyFunction ExactSafetyFunction(ActivationTest test, string name)
        {
            var all = EngineeringGroupOperations.Items(test.SafetyFunctions).Cast<SafetyFunction>().ToArray();
            var byName = all.FirstOrDefault(f => f.Name == name); if (byName != null) return byName;
            var byTest = all.Where(f => { try { return f.TestName == name; } catch { return false; } }).Take(2).ToArray();
            if (byTest.Length == 1) return byTest[0];
            throw new PortalException(PortalErrorCode.NotFound, byTest.Length == 0 ? "Safety function not found in activation test '" + test.Name + "': " + name + " (Name or unique TestName)." : "TestName '" + name + "' is ambiguous in activation test '" + test.Name + "'; use the generated Name.");
        }
        private static DeviceItem ExactEvaluationDevice(IList<DeviceItem> devices, string name, string parameter)
        {
            var matches = devices.Where(d => d.Name == name).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            throw new PortalException(PortalErrorCode.NotFound, matches.Length == 0 ? parameter + " '" + name + "' is not among the " + devices.Count + " device item(s) offered by the assistant: " + string.Join(", ", devices.Take(20).Select(d => d.Name)) : parameter + " '" + name + "' is ambiguous among the offered device items.");
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static JsonObject DeviceItemRef(DeviceItem item) => new JsonObject { ["name"] = item.Name, ["ownerPath"] = HardwareOwnerPath(item) };
        private static JsonObject ValidationRow(TestValidationResult result)
        {
            var row = new JsonObject();
            Safe(row, "state", () => result.State.ToString()); Safe(row, "errorCount", () => (long)result.ErrorCount);
            Safe(row, "messages", () => new JsonArray(EngineeringGroupOperations.Items(result.Messages).Take(500).Select(m => EngineeringScalarProperties.Json(m)).ToArray()));
            return row;
        }
        private static JsonObject CheckValidity(IEngineeringServiceProvider owner)
        {
            var validity = owner.GetService<TestValidity>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TestValidity service not provided by " + owner.GetType().Name + ".");
            return ValidationRow(validity.CheckValidity());
        }
        private static JsonObject ConditionRow(Condition c, int index)
        {
            var row = new JsonObject { ["index"] = index };
            Safe(row, "deviceName", () => c.DeviceName); Safe(row, "signalUsage", () => c.SignalUsage.ToString()); Safe(row, "signalName", () => c.SignalName); Safe(row, "comment", () => c.Comment);
            Safe(row, "initialInput", () => EngineeringScalarProperties.Json(c.InitialInput)); Safe(row, "executedInput", () => EngineeringScalarProperties.Json(c.ExecutedInput)); Safe(row, "response", () => EngineeringScalarProperties.Json(c.Response));
            return row;
        }
        private static JsonObject TraceRow(TraceConfiguration trace)
        {
            var row = new JsonObject();
            Safe(row, "isTraced", () => trace.IsTraced);
            foreach (var attribute in new[] { "PretriggerTime", "RecordingDuration", "Signals" })
                Safe(row, char.ToLowerInvariant(attribute[0]) + attribute.Substring(1), () => { var v = trace.GetAttribute(attribute); return v is System.Collections.IEnumerable list && v is not string ? new JsonArray(EngineeringGroupOperations.Items(list).Take(200).Select(x => EngineeringScalarProperties.Json(x)).ToArray()) : EngineeringScalarProperties.Json(v); });
            Safe(row, "attributeNames", () => new JsonArray(trace.GetAttributeInfos().Take(50).Select(i => (JsonNode)i.Name).ToArray()));
            return row;
        }
        private static JsonObject SafetyFunctionRow(SafetyFunction f, bool conditions)
        {
            var row = new JsonObject { ["name"] = f.Name };
            Safe(row, "testName", () => f.TestName); Safe(row, "description", () => f.Description); Safe(row, "testState", () => f.TestState.ToString());
            Safe(row, "conditionCount", () => f.Conditions.Count); Safe(row, "trace", () => TraceRow(f.TraceConfiguration));
            if (conditions) Safe(row, "conditions", () => new JsonArray(EngineeringGroupOperations.Items(f.Conditions).Cast<Condition>().Select((c, i) => (JsonNode)ConditionRow(c, i)).ToArray()));
            return row;
        }
        private static JsonObject ActivationTestRow(ActivationTest t, bool functions, bool conditions)
        {
            var row = new JsonObject { ["name"] = t.Name };
            Safe(row, "author", () => t.Author); Safe(row, "evaluationDeviceName", () => t.EvaluationDeviceName); Safe(row, "overallState", () => t.OverallState.ToString());
            Safe(row, "safetyFunctionCount", () => t.SafetyFunctions.Count);
            if (functions) Safe(row, "safetyFunctions", () => new JsonArray(EngineeringGroupOperations.Items(t.SafetyFunctions).Cast<SafetyFunction>().Select(f => (JsonNode)SafetyFunctionRow(f, conditions)).ToArray()));
            return row;
        }
        private static JsonObject GroupRow(ActivationTestUserGroup g)
        {
            var row = new JsonObject { ["name"] = g.Name };
            Safe(row, "activationTestCount", () => g.ActivationTests.Count); Safe(row, "groupCount", () => g.Groups.Count);
            return row;
        }
        private static void ExportFile(FileInfo file, string[] exportFlags, string[] documentFlags, Action<FileInfo, ExportOptions> plain, Action<FileInfo, ExportOptions, DocumentInfoOptions> withDocument, JsonObject meta)
        {
            var export = (ExportOptions)Enum.Parse(typeof(ExportOptions), string.Join(",", exportFlags));
            if (documentFlags.Length == 1 && documentFlags[0] == "None") { meta["nativeSignature"] = "Export(FileInfo, ExportOptions." + export + ")"; plain(file, export); }
            else { var document = (DocumentInfoOptions)Enum.Parse(typeof(DocumentInfoOptions), string.Join(",", documentFlags)); meta["nativeSignature"] = "Export(FileInfo, ExportOptions." + export + ", DocumentInfoOptions." + document + ")"; withDocument(file, export, document); }
            meta["file"] = NativeFileOutput.Verify(file);
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadSafetyActivationTests(string groupPathJson = "[]", string name = "", bool includeSafetyFunctions = false, bool includeConditions = false, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadSafetyActivationTests", meta =>
            {
                var path = Logic.ParseGroupPath(groupPathJson); HardwareServicesLogic.ValidatePagination(offset, limit);
                var sva = RequireSafetyValidationAssistant();
                var scope = ExactActivationTestScope(sva, path);
                meta["groupPath"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray());
                if (path.Length == 0) Safe(meta, "evaluationDevices", () => { var query = sva.GetService<DeviceQuery>(); return query == null ? null : new JsonArray(query.EvaluationDevices().Select(d => (JsonNode)DeviceItemRef(d)).ToArray()); });
                Safe(meta, "groups", () => new JsonArray(EngineeringGroupOperations.Items(scope.Groups).Cast<ActivationTestUserGroup>().Select(g => (JsonNode)GroupRow(g)).ToArray()));
                if (!string.IsNullOrEmpty(name))
                {
                    var test = ExactActivationTest(scope.Tests, name);
                    var row = ActivationTestRow(test, includeSafetyFunctions, includeConditions);
                    Safe(row, "availableDevices", () => new JsonArray(test.AvailableDevices().Select(d => (JsonNode)DeviceItemRef(d)).ToArray()));
                    Safe(row, "validity", () => CheckValidity(test));
                    meta["records"] = new JsonArray(row); meta["expectedCount"] = 1; meta["actualCount"] = 1;
                    return "Activation test read (ActivationTest scalars, available devices, TestValidity.CheckValidity" + (includeSafetyFunctions ? ", safety functions" : "") + "); nothing changed.";
                }
                var all = EngineeringGroupOperations.Items(scope.Tests).Cast<ActivationTest>().Select(t => (JsonNode)ActivationTestRow(t, includeSafetyFunctions, includeConditions)).ToArray();
                Page(all, offset, limit, meta);
                return "Activation tests of the " + (path.Length == 0 ? "Safety Validation Assistant root" : "group " + string.Join("/", path)) + " read; nothing changed.";
            });

        public ResponseMessage ManageSafetyActivationTest(string name, string action = "read", string groupPathJson = "[]", string evaluationDeviceName = "", string sourceName = "", string masterCopyPath = "", string libraryName = "", string newValue = "", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyActivationTest", meta =>
            {
                var r = Logic.ValidateActivationTestRequest(action, name, evaluationDeviceName, sourceName, masterCopyPath, newValue, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun);
                var path = Logic.ParseGroupPath(groupPathJson);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var sva = RequireSafetyValidationAssistant();
                var scope = ExactActivationTestScope(sva, path);
                meta["groupPath"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray()); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                ActivationTest? existing = action == "import" ? null : scope.Tests.Find(name);
                if ((action == "create" || action == "createFromTest" || action == "createFromMasterCopy") && existing != null) throw new PortalException(PortalErrorCode.InvalidState, "Activation test already exists: " + name);
                if (existing == null && action != "import" && action != "create" && action != "createFromTest" && action != "createFromMasterCopy") throw new PortalException(PortalErrorCode.NotFound, "Activation test not found: " + name);
                if (existing != null) meta["before"] = ActivationTestRow(existing, false, false);
                if (action == "read") { Safe(meta, "availableDevices", () => new JsonArray(existing!.AvailableDevices().Select(d => (JsonNode)DeviceItemRef(d)).ToArray())); return "Activation test read; nothing changed."; }
                if (action == "checkValidity") { meta["validity"] = CheckValidity(existing!); return "Activation test validity checked (TestValidity.CheckValidity); nothing changed."; }
                DeviceItem? device = null;
                if (action == "create") device = ExactEvaluationDevice(sva.GetService<DeviceQuery>()?.EvaluationDevices() ?? new List<DeviceItem>(), evaluationDeviceName, "evaluationDeviceName");
                if (action == "changeEvaluationDevice") device = ExactEvaluationDevice(sva.GetService<DeviceQuery>()?.EvaluationDevices() ?? new List<DeviceItem>(), evaluationDeviceName, "evaluationDeviceName");   // 2.7.42 real project: a drive from AvailableDevices is refused natively ("not a valid evaluation device")
                if (device != null) meta["evaluationDevice"] = DeviceItemRef(device);
                ActivationTest? source = action == "createFromTest" ? ExactActivationTest(scope.Tests, sourceName) : null;
                MasterCopy? masterCopy = action == "createFromMasterCopy" ? ExactMasterCopy(libraryName, masterCopyPath) : null;
                FileInfo? file = null;
                if (action == "import") file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath");
                if (action == "export" || action == "generateReport") file = NativeFileOutput.Plan(filePath);
                if (action == "generateReport") { var printout = existing!.GetService<ActivationTestPrintout>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ActivationTestPrintout service not provided by this activation test."); if (dryRun) return "Activation test report preview (ActivationTestPrintout.Generate writes an .xlsx); nothing written."; meta["mayHaveWrittenFiles"] = true; printout.Generate(file!); meta["file"] = NativeFileOutput.Verify(file!); return "Activation test report generated (ActivationTestPrintout.Generate) and verified by size / SHA-256; project unchanged."; }
                if (action == "export") { if (dryRun) return "Activation test export preview (ActivationTest.Export XML); nothing written."; meta["mayHaveWrittenFiles"] = true; ExportFile(file!, r.ExportFlags, r.DocumentFlags, (f, o) => existing!.Export(f, o), (f, o, d) => existing!.Export(f, o, d), meta); return "Activation test exported and verified by size / SHA-256; project unchanged."; }
                if (dryRun) return "Activation test " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create": meta["nativeSignature"] = "ActivationTestComposition.Create(string, DeviceItem)"; existing = scope.Tests.Create(name, device!); break;
                    case "createFromTest": meta["nativeSignature"] = "ActivationTestComposition.CreateFrom(ActivationTest)"; existing = scope.Tests.CreateFrom(source!); meta["generatedName"] = existing.Name; if (existing.Name != name) { existing.Name = name; meta["renamedTo"] = name; } break;
                    case "createFromMasterCopy": meta["nativeSignature"] = "ActivationTestComposition.CreateFrom(MasterCopy)"; existing = scope.Tests.CreateFrom(masterCopy!); meta["generatedName"] = existing.Name; if (existing.Name != name) { existing.Name = name; meta["renamedTo"] = name; } break;
                    case "rename": meta["nativeSignature"] = "ActivationTest.Name = value"; existing!.Name = newValue; break;
                    case "setAuthor": meta["nativeSignature"] = "ActivationTest.Author = value"; existing!.Author = newValue; break;
                    case "changeEvaluationDevice": meta["nativeSignature"] = "ActivationTest.ChangeEvaluationDevice(DeviceItem)"; existing!.ChangeEvaluationDevice(device!); break;
                    case "import":
                        meta["nativeSignature"] = "ActivationTestComposition.Import(FileInfo, ActivationTestImportOptions." + r.ImportOption + ")";
                        var imported = scope.Tests.Import(file!, (ActivationTestImportOptions)Enum.Parse(typeof(ActivationTestImportOptions), r.ImportOption));
                        meta["imported"] = new JsonArray(imported.Select(t => (JsonNode)ActivationTestRow(t, false, false)).ToArray()); meta["importedCount"] = imported.Count;
                        return "Activation tests imported (" + imported.Count + "); no automatic save.";
                    case "delete": meta["nativeSignature"] = "ActivationTest.Delete()"; existing!.Delete(); meta["verifiedAbsent"] = scope.Tests.Find(name) == null; return "Activation test deleted and verified absent; no automatic save.";
                }
                var after = scope.Tests.Find(action == "rename" ? newValue : name) ?? existing;
                meta["after"] = ActivationTestRow(after!, false, false);
                return "Activation test " + action + " executed and read back; no automatic save.";
            });

        public ResponseMessage ManageSafetyActivationTestGroup(string action = "read", string groupPathJson = "[]", string name = "", string masterCopyPath = "", string libraryName = "", string newName = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyActivationTestGroup", meta =>
            {
                var r = Logic.ValidateGroupRequest(action, name, masterCopyPath, newName, confirmDelete, dryRun);
                var path = Logic.ParseGroupPath(groupPathJson);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var sva = RequireSafetyValidationAssistant();
                var scope = ExactActivationTestScope(sva, path);
                meta["groupPath"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray()); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read" && string.IsNullOrEmpty(name)) { meta["records"] = new JsonArray(EngineeringGroupOperations.Items(scope.Groups).Cast<ActivationTestUserGroup>().Select(g => (JsonNode)GroupRow(g)).ToArray()); return "Activation test groups read; nothing changed."; }
                ActivationTestUserGroup? existing = scope.Groups.Find(name);
                if ((action == "create" || action == "createFromMasterCopy") && existing != null) throw new PortalException(PortalErrorCode.InvalidState, "Activation test group already exists: " + name);
                if (existing == null && action != "create" && action != "createFromMasterCopy") throw new PortalException(PortalErrorCode.NotFound, "Activation test group not found: " + name);
                if (existing != null) meta["before"] = GroupRow(existing);
                if (action == "read") { Safe(meta, "activationTests", () => new JsonArray(EngineeringGroupOperations.Items(existing!.ActivationTests).Cast<ActivationTest>().Select(t => (JsonNode)ActivationTestRow(t, false, false)).ToArray())); return "Activation test group read; nothing changed."; }
                if (action == "delete" && existing != null && (existing.ActivationTests.Count > 0 || existing.Groups.Count > 0)) throw new PortalException(PortalErrorCode.InvalidState, "Group '" + name + "' still holds " + existing.ActivationTests.Count + " activation test(s) and " + existing.Groups.Count + " subgroup(s); delete or move them first (nonempty group deletion refused).");
                MasterCopy? masterCopy = action == "createFromMasterCopy" ? ExactMasterCopy(libraryName, masterCopyPath) : null;
                if (dryRun) return "Activation test group " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create": meta["nativeSignature"] = "ActivationTestUserGroupComposition.Create(string)"; existing = scope.Groups.Create(name); break;
                    case "createFromMasterCopy": meta["nativeSignature"] = "ActivationTestUserGroupComposition.CreateFrom(MasterCopy)"; existing = scope.Groups.CreateFrom(masterCopy!); meta["generatedName"] = existing.Name; if (existing.Name != name) { existing.Name = name; meta["renamedTo"] = name; } break;
                    case "rename": meta["nativeSignature"] = "ActivationTestUserGroup.Name = value"; existing!.Name = newName; break;
                    case "delete": meta["nativeSignature"] = "ActivationTestUserGroup.Delete()"; existing!.Delete(); meta["verifiedAbsent"] = scope.Groups.Find(name) == null; return "Activation test group deleted and verified absent; no automatic save.";
                }
                var after = scope.Groups.Find(action == "rename" ? newName : name) ?? existing;
                meta["after"] = GroupRow(after!);
                return "Activation test group " + action + " executed and read back; no automatic save.";
            });

        public ResponseMessage ManageSafetyFunction(string activationTest, string action = "read", string groupPathJson = "[]", string name = "", string sourceName = "", string propertiesJson = "{}", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSafetyFunction", meta =>
            {
                var r = Logic.ValidateSafetyFunctionRequest(action, name, sourceName, propertiesJson, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun);
                var path = Logic.ParseGroupPath(groupPathJson);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var sva = RequireSafetyValidationAssistant();
                var test = ExactActivationTest(ExactActivationTestScope(sva, path).Tests, activationTest);
                SafetyFunctionComposition functions = test.SafetyFunctions;
                meta["activationTest"] = test.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action == "read" && string.IsNullOrEmpty(name)) { meta["records"] = new JsonArray(EngineeringGroupOperations.Items(functions).Cast<SafetyFunction>().Select(f => (JsonNode)SafetyFunctionRow(f, true)).ToArray()); return "Safety functions of the activation test read (with conditions and trace configuration); nothing changed."; }
                SafetyFunction? existing = action == "create" || action == "createFrom" || action == "import" ? null : ExactSafetyFunction(test, name);
                if (existing != null) meta["before"] = SafetyFunctionRow(existing, true);
                if (action == "read") return "Safety function read; nothing changed.";
                if (action == "checkValidity") { meta["validity"] = CheckValidity(existing!); return "Safety function validity checked (TestValidity.CheckValidity); nothing changed."; }
                if (action == "checkTraceValidity") { meta["validity"] = CheckValidity(existing!.TraceConfiguration); return "Trace configuration validity checked (TestValidity.CheckValidity); nothing changed."; }
                SafetyFunction? source = action == "createFrom" ? ExactSafetyFunction(test, sourceName) : null;
                FileInfo? file = null;
                if (action == "import") file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath");
                if (action == "export") file = NativeFileOutput.Plan(filePath);
                if (action == "export") { if (dryRun) return "Safety function export preview (SafetyFunction.Export XML); nothing written."; meta["mayHaveWrittenFiles"] = true; ExportFile(file!, r.ExportFlags, r.DocumentFlags, (f, o) => existing!.Export(f, o), (f, o, d) => existing!.Export(f, o, d), meta); return "Safety function exported and verified by size / SHA-256; project unchanged."; }
                if (dryRun) return "Safety function " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                void ApplyProperties(SafetyFunction f)
                {
                    if (r.Properties["testName"] is JsonValue testName) f.TestName = testName.GetValue<string>();
                    if (r.Properties["description"] is JsonValue description) f.Description = description.GetValue<string>();
                }
                switch (action)
                {
                    case "create": meta["nativeSignature"] = "SafetyFunctionComposition.Create()"; existing = functions.Create(); ApplyProperties(existing); meta["createdName"] = existing.Name; break;
                    case "createFrom": meta["nativeSignature"] = "SafetyFunctionComposition.CreateFrom(SafetyFunction)"; existing = functions.CreateFrom(source!); meta["createdName"] = existing.Name; break;
                    case "update": meta["nativeSignature"] = "SafetyFunction.TestName / Description"; ApplyProperties(existing!); break;
                    case "resetTestResult": meta["nativeSignature"] = "SafetyFunction.ResetTestResult()"; existing!.ResetTestResult(); break;
                    case "setTrace":
                        meta["nativeSignature"] = "TraceConfiguration.IsTraced / SetAttribute(PretriggerTime | RecordingDuration | Signals)";
                        var trace = existing!.TraceConfiguration;
                        if (r.Properties["isTraced"] is JsonValue traced) trace.IsTraced = traced.GetValue<bool>();
                        if (r.Properties["pretriggerTime"] is JsonValue pre) trace.SetAttribute("PretriggerTime", EngineeringScalarProperties.ConvertValue(pre, trace.GetAttribute("PretriggerTime")?.GetType() ?? typeof(int)));
                        if (r.Properties["recordingDuration"] is JsonValue duration) trace.SetAttribute("RecordingDuration", EngineeringScalarProperties.ConvertValue(duration, trace.GetAttribute("RecordingDuration")?.GetType() ?? typeof(int)));
                        if (r.Properties["signals"] is JsonArray signals) trace.SetAttribute("Signals", signals.Select(s => s?.GetValue<string>() ?? "").ToList());
                        break;
                    case "import":
                        meta["nativeSignature"] = "SafetyFunctionComposition.Import(FileInfo, ImportOptions." + r.ImportOption + ")";
                        var imported = functions.Import(file!, (ImportOptions)Enum.Parse(typeof(ImportOptions), r.ImportOption));
                        meta["imported"] = new JsonArray(imported.Select(f => (JsonNode)SafetyFunctionRow(f, false)).ToArray()); meta["importedCount"] = imported.Count;
                        return "Safety functions imported (" + imported.Count + "); no automatic save.";
                    case "delete": meta["nativeSignature"] = "SafetyFunction.Delete()"; var deletedName = existing!.Name; existing.Delete(); meta["verifiedAbsent"] = !EngineeringGroupOperations.Items(test.SafetyFunctions).Cast<SafetyFunction>().Any(f => f.Name == deletedName); return "Safety function deleted and verified absent; no automatic save.";
                }
                meta["after"] = SafetyFunctionRow(ExactSafetyFunction(test, existing!.Name), true);
                return "Safety function " + action + " executed and read back; no automatic save.";
            });

        public ResponseMessage ManageSafetyFunctionCondition(string activationTest, string safetyFunction, string action = "read", string groupPathJson = "[]", int index = -1, string deviceName = "", string signalUsage = "", string signalName = "", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageSafetyFunctionCondition", meta =>
            {
                var r = Logic.ValidateConditionRequest(action, index, deviceName, signalUsage, signalName, propertiesJson, dryRun);
                var path = Logic.ParseGroupPath(groupPathJson);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var sva = RequireSafetyValidationAssistant();
                var test = ExactActivationTest(ExactActivationTestScope(sva, path).Tests, activationTest);
                var function = ExactSafetyFunction(test, safetyFunction);
                ConditionComposition conditions = function.Conditions;
                meta["activationTest"] = test.Name; meta["safetyFunction"] = function.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                Condition[] All() => EngineeringGroupOperations.Items(function.Conditions).Cast<Condition>().ToArray();
                if (action == "read" && index < 0) { meta["records"] = new JsonArray(All().Select((c, i) => (JsonNode)ConditionRow(c, i)).ToArray()); return "Conditions of the safety function read; nothing changed."; }
                Condition? existing = null;
                if (action != "create") { var all = All(); if (index >= all.Length) throw new PortalException(PortalErrorCode.NotFound, "index " + index + " out of range: the safety function has " + all.Length + " condition(s)."); existing = all[index]; meta["before"] = ConditionRow(existing, index); }
                if (action == "read") return "Condition read; nothing changed.";
                if (action == "checkValidity") { meta["validity"] = CheckValidity(existing!); return "Condition validity checked (TestValidity.CheckValidity); nothing changed. 2.7.42 real project: this call took TIA Portal V21 down once, right after an update that TIA had refused half-way - check the function-level validity first."; }
                if (action == "update" && r.Properties["signalUsage"] == null) Logic.ValidateConditionValues(existing!.SignalUsage.ToString(), r.Properties);
                DeviceItem? device = action == "create" ? ExactEvaluationDevice(test.AvailableDevices(), deviceName, "deviceName") : null;
                if (device != null) meta["device"] = DeviceItemRef(device);
                if (dryRun) return "Condition " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                object ConditionValueOf(string key, object? current)
                {
                    var parsed = Logic.ParseConditionValue(r.Properties[key], key);
                    if (parsed.EnumName != null) return Enum.Parse(typeof(ConditionValue), parsed.EnumName);
                    return current is ConditionValue ? Enum.Parse(typeof(ConditionValue), parsed.Flag == true ? "TRUE" : "FALSE") : parsed.Flag!.Value;
                }
                void ApplyProperties(Condition c)
                {
                    // usage first, then the inputs it owns, then the descriptive fields - a refused input write leaves nothing half done
                    if (r.Properties["signalUsage"] is JsonValue usage) c.SignalUsage = (SignalUsage)Enum.Parse(typeof(SignalUsage), usage.GetValue<string>());
                    if (r.Properties.ContainsKey("initialInput")) c.InitialInput = ConditionValueOf("initialInput", c.InitialInput);
                    if (r.Properties.ContainsKey("executedInput")) c.ExecutedInput = ConditionValueOf("executedInput", c.ExecutedInput);
                    if (r.Properties.ContainsKey("response")) c.Response = ConditionValueOf("response", c.Response);
                    if (r.Properties["comment"] is JsonValue comment) c.Comment = comment.GetValue<string>();
                    if (r.Properties["deviceName"] is JsonValue dev) c.DeviceName = dev.GetValue<string>();
                    if (r.Properties["signalName"] is JsonValue sig) c.SignalName = sig.GetValue<string>();
                }
                switch (action)
                {
                    case "create":
                        meta["nativeSignature"] = "ConditionComposition.Create(DeviceItem, SignalUsage." + signalUsage + ", string)";
                        existing = conditions.Create(device!, (SignalUsage)Enum.Parse(typeof(SignalUsage), signalUsage), signalName); ApplyProperties(existing);
                        var created = All(); index = Array.IndexOf(created, existing); if (index < 0) index = created.Length - 1; meta["createdIndex"] = index; break;
                    case "update": meta["nativeSignature"] = "Condition.Comment / DeviceName / SignalName / SignalUsage / InitialInput / ExecutedInput / Response"; ApplyProperties(existing!); break;
                    case "delete": meta["nativeSignature"] = "Condition.Delete()"; int before = All().Length; existing!.Delete(); meta["verifiedAbsent"] = All().Length == before - 1; return "Condition deleted (count decreased by one); no automatic save.";
                }
                var fresh = All(); meta["after"] = index < fresh.Length ? ConditionRow(fresh[index], index) : null;
                return "Condition " + action + " executed and read back; no automatic save.";
            });
#endif
    }
}
