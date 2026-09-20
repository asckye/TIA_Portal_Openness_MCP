using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;
using Siemens.Engineering.TestSuite.StyleGuide;
using Siemens.Engineering.TestSuite.SystemTest;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.TestSuiteLogic;
using AppLoadOptions = Siemens.Engineering.TestSuite.ApplicationTest.TCLoadOptions;
using SysLoadOptions = Siemens.Engineering.TestSuite.SystemTest.TCLoadOptions;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): typed TIA Portal Test Suite option package (Siemens.Engineering.TestSuite, identical on V20 / V21;
    // replaces the reflective 2.7.x adapter). Official entry: Project.GetService<TestSuiteService>() -> StyleGuideGroup.RuleSets,
    // ApplicationTestGroup.TestCases / ApplicationTestSets, SystemTestGroup.SystemTestCases; the executors are services of the three
    // system groups (RuleSetExecutor / TestCaseExecutor / SystemTestCaseExecutor) and answer TestResults with recursive messages.
    public partial class Portal
    {
        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private TestSuiteService RequireTestSuite()
            => _project!.GetService<TestSuiteService>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TestSuiteService is not provided by this project (TIA Portal Test Suite not installed or not licensed).");
        private static object TestSuiteCollection(TestSuiteService service, string category, string kind) => category switch
        {
            "styleGuide" => service.StyleGuideGroup.RuleSets,
            "application" => kind == "testSet" ? (object)service.ApplicationTestGroup.ApplicationTestSets : service.ApplicationTestGroup.TestCases,
            _ => service.SystemTestGroup.SystemTestCases
        };
        private static IEngineeringObject? FindTestSuiteItem(object collection, string name) => collection switch
        {
            RuleSetComposition c => c.Find(name),
            TestCaseComposition c => c.Find(name),
            SystemTestCaseComposition c => c.Find(name),
            ApplicationTestSetComposition c => c.Find(name),
            _ => null
        };
        private static IEngineeringObject ExactTestSuiteItem(object collection, string name, string label)
            => FindTestSuiteItem(collection, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + label + " not found: " + name);
        // Official style-guide scope objects: Project, DeviceGroups[x], the CPU DeviceItem, PlcBlockSystemGroup / PlcTagTableSystemGroup /
        // PlcTypeSystemGroup (or a user group below them) and PlcUnitProvider.UnitGroup.
        private IEngineeringObject ResolveStyleGuideScopeObject(Logic.ScopeEntry entry, JsonArray resolved)
        {
            IEngineeringObject result;
            switch (entry.Kind)
            {
                case "project": result = _project!; break;
                case "deviceGroup": result = _project!.DeviceGroups.Find(entry.Name) ?? throw new PortalException(PortalErrorCode.NotFound, "Device group not found: " + entry.Name); break;
                case "plc":
                    var container = ResolveSoftwareContainerUncached(entry.SoftwarePath) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + entry.SoftwarePath);
                    result = container.Parent as DeviceItem ?? throw new PortalException(PortalErrorCode.NotFound, "The software container of " + entry.SoftwarePath + " is not owned by a device item."); break;
                default:
                    var plc = ExactPlcForEngineering(entry.SoftwarePath, false);
                    if (entry.Kind == "units") result = RequireUnitProvider(plc).UnitGroup;
                    else
                    {
                        object root = entry.Kind == "blocks" ? plc.BlockGroup : entry.Kind == "tags" ? (object)plc.TagTableGroup : plc.TypeGroup;
                        result = (IEngineeringObject)(string.IsNullOrEmpty(entry.GroupPath) ? root : EngineeringGroupOperations.Group(root, entry.GroupPath));
                    }
                    break;
            }
            resolved.Add(new JsonObject { ["kind"] = entry.Kind, ["class"] = result.GetType().Name, ["name"] = EngineeringGroupOperations.Get(result, "Name")?.ToString() });
            return result;
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static JsonObject TestSuiteRow(IEngineeringObject item)
        {
            var row = new JsonObject { ["class"] = item.GetType().Name };
            switch (item)
            {
                case RuleSet rs: row["name"] = rs.Name; break;
                case TestCase tc: row["name"] = tc.Name; Safe(row, "scope", () => tc.GetScope()); break;
                case SystemTestCase stc: row["name"] = stc.Name; Safe(row, "opcUaServerAddress", () => stc.OPCUAServerAddress); Safe(row, "opcUaServerInterfaceType", () => stc.OPCUAServerInterfaceType.ToString()); Safe(row, "opcUaServerInterfaceFolderPath", () => stc.OPCUAServerInterfaceFolderPath?.FullName); break;
                case ApplicationTestSet ts: row["name"] = ts.Name; break;
                default: Safe(row, "name", () => EngineeringGroupOperations.Get(item, "Name")?.ToString()); break;
            }
            return row;
        }
        // Official example walks TestResults.Messages recursively; bounded to depth 12 / 2000 messages so a big style-guide run stays serialisable.
        private static JsonArray TestResultMessageRows(TestResultsMessageComposition messages, int depth, int[] visited)
        {
            var rows = new JsonArray();
            if (depth > 12) return rows;
            foreach (TestResultsMessage m in messages)
            {
                if (++visited[0] > 2000) { rows.Add(new JsonObject { ["truncated"] = true }); break; }
                var row = new JsonObject();
                Safe(row, "path", () => m.Path); Safe(row, "state", () => m.State.ToString()); Safe(row, "description", () => m.Description);
                Safe(row, "dateTimeUtc", () => m.DateTime.ToString("o")); Safe(row, "errorCount", () => m.ErrorCount); Safe(row, "warningCount", () => m.WarningCount);
                Safe(row, "messages", () => TestResultMessageRows(m.Messages, depth + 1, visited));
                rows.Add(row);
            }
            return rows;
        }
        private static JsonObject TestResultsRow(TestResults results)
        {
            var row = new JsonObject();
            Safe(row, "state", () => results.State.ToString()); Safe(row, "errorCount", () => results.ErrorCount); Safe(row, "warningCount", () => results.WarningCount);
            var visited = new[] { 0 }; Safe(row, "messages", () => TestResultMessageRows(results.Messages, 0, visited)); row["messageCount"] = visited[0];
            return row;
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadTestSuiteCases(string category, string name = "", int offset = 0, int limit = 100, string kind = "case")
            => RunHmiStepTool("ReadTestSuiteCases", meta =>
            {
                var k = Logic.ValidateReadRequest(category, kind, offset, limit);
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, k);
                meta["category"] = category; meta["kind"] = k; meta["collectionClass"] = collection.GetType().Name;
                if (category == "application") Safe(meta, "testSetCount", () => service.ApplicationTestGroup.ApplicationTestSets.Count);
                if (!string.IsNullOrEmpty(name))
                {
                    var item = ExactTestSuiteItem(collection, name, Logic.CategoryLabel(category));
                    meta["records"] = new JsonArray(TestSuiteRow(item)); meta["expectedCount"] = 1; meta["actualCount"] = 1;
                    return "Test Suite " + Logic.CategoryLabel(category) + " read (typed scalars and scope); no test executed.";
                }
                var all = EngineeringGroupOperations.Items(collection).Cast<IEngineeringObject>().Select(x => (JsonNode)TestSuiteRow(x)).ToArray();
                Page(all, offset, limit, meta);
                return "Test Suite " + (k == "testSet" ? "application test sets" : Logic.CategoryLabel(category) + "s") + " read; no test executed.";
            });

        public ResponseMessage ExchangeTestSuiteCase(string category, string action, string name, string filePath = "", string importOptions = "None", string loadOptions = "", bool dryRun = true, string kind = "case")
            => RunHmiStepTool("ExchangeTestSuiteCase", meta =>
            {
                var r = Logic.ValidateExchangeRequest(category, action, name, filePath, importOptions, loadOptions, kind, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["expectedName"] = name;
                var target = action == "import" || action == "importTestSets" ? FindTestSuiteItem(collection, name) : ExactTestSuiteItem(collection, name, Logic.CategoryLabel(category));
                if (target != null) meta["before"] = TestSuiteRow(target);
                FileInfo? file = null;
                if (action == "export") file = NativeFileOutput.Plan(filePath);
                if (action == "import" || action == "importTestSets") { file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["importOptions"] = r.ImportOption; meta["loadOptions"] = string.Join("|", r.LoadFlags); }
                if (dryRun) return "Test Suite definition " + action + " preview; import may contain multiple definitions and the native ImportOptions govern replacements. Nothing changed.";
                meta["mayHaveChanged"] = action != "export"; meta["mayHaveWrittenFiles"] = action == "export";
                var import = (ImportOptions)Enum.Parse(typeof(ImportOptions), r.ImportOption);
                switch (action)
                {
                    case "export":
                        bool saved = target switch { RuleSet rs => rs.SaveToFile(file!), TestCase tc => tc.SaveToFile(file!), SystemTestCase stc => stc.SaveToFile(file!), _ => throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SaveToFile is not available on " + target!.GetType().Name + ".") };
                        meta["nativeSignature"] = target!.GetType().Name + ".SaveToFile(FileInfo) -> " + saved; meta["nativeResult"] = saved; if (!saved) meta["operationSuccess"] = false;
                        meta["file"] = NativeFileOutput.Verify(file!);
                        return "Test Suite definition exported (native bool " + saved + "); the file is verified by size / SHA-256, not by content. Project unchanged.";
                    case "import":
                        IList<IEngineeringObject> imported;
                        switch (collection)
                        {
                            case RuleSetComposition rules: var rs = (RSLoadOptions)Enum.Parse(typeof(RSLoadOptions), string.Join(",", r.LoadFlags)); meta["nativeSignature"] = "RuleSetComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", RSLoadOptions." + rs + ")"; imported = rules.LoadFromFile(file!, import, rs).Cast<IEngineeringObject>().ToList(); break;
                            case TestCaseComposition cases: var tc = (AppLoadOptions)Enum.Parse(typeof(AppLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "TestCaseComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", TCLoadOptions." + tc + ")"; imported = cases.LoadFromFile(file!, import, tc).Cast<IEngineeringObject>().ToList(); break;
                            case SystemTestCaseComposition system: var st = (SysLoadOptions)Enum.Parse(typeof(SysLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "SystemTestCaseComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", TCLoadOptions." + st + ")"; imported = system.LoadFromFile(file!, import, st).Cast<IEngineeringObject>().ToList(); break;
                            default: throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "LoadFromFile is not available on " + collection.GetType().Name + ".");
                        }
                        meta["imported"] = new JsonArray(imported.Select(x => (JsonNode)TestSuiteRow(x)).ToArray()); meta["importedCount"] = imported.Count;
                        if (!string.IsNullOrEmpty(name)) meta["expectedPresenceVerified"] = FindTestSuiteItem(collection, name) != null;
                        return "Test Suite definitions imported (" + imported.Count + "); presence checks do not prove test semantics. No test run or automatic save.";
                    case "importTestSets":
                        var ts = (TSLoadOptions)Enum.Parse(typeof(TSLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "ApplicationTestSystemGroup.LoadFromFile(FileInfo, ImportOptions." + import + ", TSLoadOptions." + ts + ")";
                        var loaded = service.ApplicationTestGroup.LoadFromFile(file!, import, ts);
                        meta["imported"] = new JsonArray(loaded.Select(x => (JsonNode)TestSuiteRow(x)).ToArray()); meta["importedCount"] = loaded.Count;
                        meta["importedTestSets"] = loaded.Count(x => x is ApplicationTestSet); meta["importedTestCases"] = loaded.Count(x => x is TestCase);
                        return "Application test sets and their test cases imported (" + loaded.Count + " objects); no test run or automatic save.";
                    default:
                        switch (target) { case RuleSet rs: rs.Delete(); break; case TestCase tc: tc.Delete(); break; case SystemTestCase stc: stc.Delete(); break; case ApplicationTestSet set: set.Delete(); break; }
                        meta["nativeSignature"] = target!.GetType().Name + ".Delete()";
                        if (FindTestSuiteItem(collection, name) != null) throw new InvalidOperationException("Test definition still present after Delete(); deletion not verified.");
                        meta["expectedPresenceVerified"] = true; meta["verifiedAbsent"] = true;
                        return "Test Suite definition deleted and verified absent; no automatic save.";
                }
            });

        public ResponseMessage RunTestSuiteCase(string category, string name = "", bool confirmExternalExecution = false, bool dryRun = true, string namesJson = "[]", bool runAll = false, string kind = "case")
            => RunHmiStepTool("RunTestSuiteCase", meta =>
            {
                var r = Logic.ValidateRunRequest(category, name, namesJson, runAll, kind, confirmExternalExecution, dryRun);
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["dryRun"] = dryRun; meta["mayHaveExternalEffects"] = false; meta["runAll"] = runAll;
                var targets = r.Names.Select(n => ExactTestSuiteItem(collection, n, Logic.CategoryLabel(category))).ToArray();
                meta["before"] = new JsonArray(targets.Select(t => (JsonNode)TestSuiteRow(t)).ToArray());
                if (runAll) Safe(meta, "expectedCount", () => EngineeringGroupOperations.Items(collection).Count()); else meta["expectedCount"] = targets.Length;
                if (dryRun) return "Native test execution preview. Application / system tests may start a PLCSIM Advanced instance or talk to the configured OPC UA server; real execution needs confirmExternalExecution=true. No test executed.";
                meta["mayHaveExternalEffects"] = r.External;
                TestResults results;
                switch (category)
                {
                    case "styleGuide":
                        var ruleExecutor = service.StyleGuideGroup.GetService<RuleSetExecutor>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "RuleSetExecutor service not provided by StyleGuideGroup.");
                        if (runAll) { meta["nativeSignature"] = "RuleSetExecutor.Run(StyleGuideSystemGroup)"; results = ruleExecutor.Run(service.StyleGuideGroup); }
                        else if (targets.Length == 1) { meta["nativeSignature"] = "RuleSetExecutor.Run(RuleSet)"; results = ruleExecutor.Run((RuleSet)targets[0]); }
                        else { meta["nativeSignature"] = "RuleSetExecutor.Run(IEnumerable<RuleSet>)"; results = ruleExecutor.Run(targets.Cast<RuleSet>().ToList()); }
                        break;
                    case "application":
                        var caseExecutor = service.ApplicationTestGroup.GetService<TestCaseExecutor>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TestCaseExecutor service not provided by ApplicationTestGroup.");
                        if (runAll) { meta["nativeSignature"] = "TestCaseExecutor.Run(ApplicationTestSystemGroup)"; results = caseExecutor.Run(service.ApplicationTestGroup); }
                        else if (r.Kind == "testSet") { if (targets.Length == 1) { meta["nativeSignature"] = "TestCaseExecutor.Run(ApplicationTestSet)"; results = caseExecutor.Run((ApplicationTestSet)targets[0]); } else { meta["nativeSignature"] = "TestCaseExecutor.Run(IEnumerable<ApplicationTestSet>)"; results = caseExecutor.Run(targets.Cast<ApplicationTestSet>().ToList()); } }
                        else if (targets.Length == 1) { meta["nativeSignature"] = "TestCaseExecutor.Run(TestCase)"; results = caseExecutor.Run((TestCase)targets[0]); }
                        else { meta["nativeSignature"] = "TestCaseExecutor.Run(IEnumerable<TestCase>)"; results = caseExecutor.Run(targets.Cast<TestCase>().ToList()); }
                        break;
                    default:
                        var systemExecutor = service.SystemTestGroup.GetService<SystemTestCaseExecutor>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SystemTestCaseExecutor service not provided by SystemTestGroup.");
                        if (runAll) { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(SystemTestSystemGroup)"; results = systemExecutor.Run(service.SystemTestGroup); }
                        else if (targets.Length == 1) { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(SystemTestCase)"; results = systemExecutor.Run((SystemTestCase)targets[0]); }
                        else { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(IEnumerable<SystemTestCase>)"; results = systemExecutor.Run(targets.Cast<SystemTestCase>().ToList()); }
                        break;
                }
                var row = TestResultsRow(results); meta["result"] = row; meta["apiCallSuccess"] = true;
                var state = results.State.ToString(); meta["nativeState"] = state;
                bool passed = state == "Success" || state == "Information"; meta["testPassed"] = passed; meta["operationSuccess"] = passed;
                return "Native Test Suite execution returned TestResults (state " + state + ", " + results.ErrorCount + " error(s), " + results.WarningCount + " warning(s)); testPassed reflects the native state only. No automatic project save or Portal close.";
            });

        public ResponseMessage ManageTestSuiteCase(string category, string name, string action = "read", string kind = "case", string newName = "", string softwarePath = "", string instanceName = "", string executionMode = "", string opcUaServerAddress = "", string serverInterfaceType = "", string interfaceFolderPath = "", string updateOptions = "", string scopeJson = "[]", string targetName = "", string masterCopyPath = "", string libraryName = "", bool dryRun = true)
            => RunHmiStepTool("ManageTestSuiteCase", meta =>
            {
                var r = Logic.ValidateManageRequest(category, name, action, kind, newName, softwarePath, instanceName, executionMode, opcUaServerAddress, serverInterfaceType, interfaceFolderPath, updateOptions, scopeJson, targetName, masterCopyPath, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                IEngineeringObject? target = action == "createFromMasterCopy" ? (string.IsNullOrEmpty(name) ? null : FindTestSuiteItem(collection, name)) : ExactTestSuiteItem(collection, name, Logic.CategoryLabel(category));
                if (action == "createFromMasterCopy" && target != null) throw new PortalException(PortalErrorCode.InvalidState, Logic.CategoryLabel(category) + " already exists: " + name);
                if (target != null) meta["before"] = TestSuiteRow(target);
                if (action == "read") return "Test Suite " + Logic.CategoryLabel(category) + " read; nothing changed.";
                MasterCopy? masterCopy = action == "createFromMasterCopy" ? ExactMasterCopy(libraryName, masterCopyPath) : null;
                RuleSet? copyTarget = action == "copyScope" ? (RuleSet)ExactTestSuiteItem(collection, targetName, "rule set") : null;
                if (copyTarget != null) meta["target"] = TestSuiteRow(copyTarget);
                PlcSoftware? plc = action == "setScope" && category == "application" ? ExactPlcForEngineering(softwarePath, false) : null;
                var scopeObjects = new List<IEngineeringObject>(); var resolved = new JsonArray();
                if (action == "setScope" && category == "styleGuide") { foreach (var entry in r.Scope) scopeObjects.Add(ResolveStyleGuideScopeObject(entry, resolved)); meta["scopeObjects"] = resolved; }
                DirectoryInfo? folder = action == "setScope" && category == "system" && !string.IsNullOrEmpty(interfaceFolderPath) ? new DirectoryInfo(interfaceFolderPath) : null;
                if (folder != null && !folder.Exists) throw new DirectoryNotFoundException("interfaceFolderPath does not exist: " + interfaceFolderPath);
                if (action == "showInEditor")
                {
                    switch (target) { case RuleSet rs: rs.ShowInEditor(); break; case TestCase tc: tc.ShowInEditor(); break; case ApplicationTestSet set: set.ShowInEditor(); break; default: throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ShowInEditor is not available on " + target!.GetType().Name + "."); }
                    meta["nativeSignature"] = target!.GetType().Name + ".ShowInEditor()";
                    return "Opened in the TIA Portal editor (WithUserInterface sessions only); project unchanged.";
                }
                if (dryRun) return "Test Suite " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "rename":
                        switch (target) { case RuleSet rs: rs.Name = newName; break; case TestCase tc: tc.Name = newName; break; case SystemTestCase stc: stc.Name = newName; break; case ApplicationTestSet set: set.Name = newName; break; }
                        meta["nativeSignature"] = target!.GetType().Name + ".Name = value"; name = newName; break;
                    case "createFromMasterCopy":
                        switch (collection)
                        {
                            case RuleSetComposition rules: meta["nativeSignature"] = "RuleSetComposition.CreateFrom(MasterCopy)"; target = rules.CreateFrom(masterCopy!); break;
                            case TestCaseComposition cases: meta["nativeSignature"] = "TestCaseComposition.CreateFrom(MasterCopy)"; target = cases.CreateFrom(masterCopy!); break;
                            case SystemTestCaseComposition system: meta["nativeSignature"] = "SystemTestCaseComposition.CreateFrom(MasterCopy)"; target = system.CreateFrom(masterCopy!); break;
                        }
                        var generated = EngineeringGroupOperations.Get(target!, "Name")?.ToString() ?? ""; meta["generatedName"] = generated;
                        if (!string.IsNullOrEmpty(name) && generated != name) { switch (target) { case RuleSet rs: rs.Name = name; break; case TestCase tc: tc.Name = name; break; case SystemTestCase stc: stc.Name = name; break; } meta["renamedTo"] = name; }
                        else name = generated;
                        break;
                    case "copyScope":
                        meta["nativeSignature"] = "RuleSet.CopyScope(RuleSet)"; bool copied = ((RuleSet)target!).CopyScope(copyTarget!); meta["nativeResult"] = copied; if (!copied) meta["operationSuccess"] = false;
                        break;
                    case "setScope":
                        switch (target)
                        {
                            case RuleSet rs:
                                var option = (UpdateOptions)Enum.Parse(typeof(UpdateOptions), r.UpdateOption); meta["nativeSignature"] = "RuleSet.SetScope(UpdateOptions." + option + ", IEnumerable<IEngineeringObject>)";
                                bool set = rs.SetScope(option, scopeObjects); meta["nativeResult"] = set; if (!set) meta["operationSuccess"] = false; break;
                            case TestCase tc:
                                if (string.IsNullOrEmpty(r.ExecutionMode)) { meta["nativeSignature"] = "TestCase.SetScope(PlcSoftware)"; bool ok = tc.SetScope(plc!); meta["nativeResult"] = ok; if (!ok) meta["operationSuccess"] = false; }
                                else { var mode = (ExecutionMode)Enum.Parse(typeof(ExecutionMode), r.ExecutionMode); meta["nativeSignature"] = "TestCase.SetScope(PlcSoftware, string, ExecutionMode." + mode + ")"; tc.SetScope(plc!, instanceName, mode); }
                                break;
                            case SystemTestCase stc:
                                var iface = (ServerInterfaces)Enum.Parse(typeof(ServerInterfaces), r.ServerInterface);
                                if (folder == null) { meta["nativeSignature"] = "SystemTestCase.SetScope(string, ServerInterfaces." + iface + ")"; stc.SetScope(opcUaServerAddress, iface); }
                                else { meta["nativeSignature"] = "SystemTestCase.SetScope(string, ServerInterfaces." + iface + ", DirectoryInfo)"; stc.SetScope(opcUaServerAddress, iface, folder); }
                                break;
                        }
                        break;
                }
                var after = FindTestSuiteItem(collection, name) ?? target;
                meta["after"] = TestSuiteRow(after!);
                return "Test Suite " + action + " executed and read back; no test run or automatic save.";
            });
    }
}
