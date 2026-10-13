#if OPTION_TEST_SUITE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaOpenness.Shared;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;
using Siemens.Engineering.TestSuite.StyleGuide;
#if OPTION_SYSTEM_TEST
using Siemens.Engineering.TestSuite.SystemTest;
using SysLoadOptions = Siemens.Engineering.TestSuite.SystemTest.TCLoadOptions;
#endif
using NativeTestSuiteService = Siemens.Engineering.TestSuite.TestSuiteService;
using AppLoadOptions = Siemens.Engineering.TestSuite.ApplicationTest.TCLoadOptions;

namespace TiaMcp.Adapters.Native.Options;

internal sealed class TestSuiteAdapter
{
    private readonly OptionalProductSession _session;
    internal TestSuiteAdapter(OptionalProductSession session) => _session = session;
        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private NativeTestSuiteService RequireTestSuite()
            => _session.CurrentProject!.GetService<NativeTestSuiteService>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "TestSuiteService is not provided by this project (TIA Portal Test Suite not installed or not licensed).");
        private static object TestSuiteCollection(NativeTestSuiteService service, string category, string kind) => category switch
        {
            "styleGuide" => service.StyleGuideGroup.RuleSets,
            "application" =>
#if OPTION_TEST_SETS
                kind == "testSet" ? (object)service.ApplicationTestGroup.ApplicationTestSets :
#endif
                service.ApplicationTestGroup.TestCases,
#if OPTION_SYSTEM_TEST
            _ => service.SystemTestGroup.SystemTestCases
#else
            _ => throw new NotSupportedException("System Test requires V18 or later.")
#endif
        };
        private static IEngineeringObject? FindTestSuiteItem(object collection, string name) => collection switch
        {
            RuleSetComposition c => c.Find(name),
            TestCaseComposition c => c.Find(name),
#if OPTION_SYSTEM_TEST
            SystemTestCaseComposition c => c.Find(name),
#endif
#if OPTION_TEST_SETS
            ApplicationTestSetComposition c => c.Find(name),
#endif
            _ => null
        };
        private static IEngineeringObject ExactTestSuiteItem(object collection, string name, string label)
            => FindTestSuiteItem(collection, name) ?? throw new PlcSoftwareException("NotFound", "Exact " + label + " not found: " + name);
        // Official style-guide scope objects: Project, DeviceGroups[x], the CPU DeviceItem, PlcBlockSystemGroup / PlcTagTableSystemGroup /
        // PlcTypeSystemGroup (or a user group below them) and PlcUnitProvider.UnitGroup.
        private IEngineeringObject ResolveStyleGuideScopeObject(TestSuiteScope entry, HmiArray resolved)
        {
            IEngineeringObject result;
            switch (entry.Kind)
            {
                case "project": result = _session.CurrentProject!; break;
                case "deviceGroup": result = _session.CurrentProject!.DeviceGroups.Find(entry.Name) ?? throw new PlcSoftwareException("NotFound", "Device group not found: " + entry.Name); break;
                case "plc":
                    var container = _session.ResolveSoftwareContainerUncached(entry.SoftwarePath) ?? throw new PlcSoftwareException("NotFound", "Exact PLC software not found: " + entry.SoftwarePath);
                    result = container.Parent as DeviceItem ?? throw new PlcSoftwareException("NotFound", "The software container of " + entry.SoftwarePath + " is not owned by a device item."); break;
                default:
                    var plc = _session.ExactPlcForEngineering(entry.SoftwarePath, false);
                    if (entry.Kind == "units") result = _session.RequireUnitProvider(plc).UnitGroup;
                    else
                    {
                        object root = entry.Kind == "blocks" ? plc.BlockGroup : entry.Kind == "tags" ? (object)plc.TagTableGroup : plc.TypeGroup;
                        result = (IEngineeringObject)(string.IsNullOrEmpty(entry.GroupPath) ? root : PlcGroupOperations.Group(root, entry.GroupPath));
                    }
                    break;
            }
            resolved.Add(new HmiObject { ["kind"] = entry.Kind, ["class"] = result.GetType().Name, ["name"] = PlcGroupOperations.Get(result, "Name")?.ToString() });
            return result;
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static HmiObject TestSuiteRow(IEngineeringObject item)
        {
            var row = new HmiObject { ["class"] = item.GetType().Name };
            switch (item)
            {
                case RuleSet rs: row["name"] = rs.Name; break;
                case TestCase tc: row["name"] = tc.Name; OptionalProductEvidence.Safe(row, "scope", () => tc.GetScope()); break;
#if OPTION_SYSTEM_TEST
                case SystemTestCase stc: row["name"] = stc.Name;
#if OPTION_TEST_SCOPE
                    OptionalProductEvidence.Safe(row, "opcUaServerAddress", () => stc.OPCUAServerAddress); OptionalProductEvidence.Safe(row, "opcUaServerInterfaceType", () => stc.OPCUAServerInterfaceType.ToString()); OptionalProductEvidence.Safe(row, "opcUaServerInterfaceFolderPath", () => stc.OPCUAServerInterfaceFolderPath?.FullName); break;
#else
                    break;
#endif
#endif
#if OPTION_TEST_SETS
                case ApplicationTestSet ts: row["name"] = ts.Name; break;
#endif
                default: OptionalProductEvidence.Safe(row, "name", () => PlcGroupOperations.Get(item, "Name")?.ToString()); break;
            }
            return row;
        }
        // Official example walks TestResults.Messages recursively; bounded to depth 12 / 2000 messages so a big style-guide run stays serialisable.
        private static HmiArray TestResultMessageRows(TestResultsMessageComposition messages, int depth, int[] visited)
        {
            var rows = new HmiArray();
            if (depth > 12) { rows.Add(new HmiObject { ["truncated"] = true }); return rows; }
            foreach (TestResultsMessage m in messages)
            {
                if (++visited[0] > 2000) { rows.Add(new HmiObject { ["truncated"] = true }); break; }
                var row = new HmiObject();
                OptionalProductEvidence.Safe(row, "path", () => m.Path); OptionalProductEvidence.Safe(row, "state", () => m.State.ToString()); OptionalProductEvidence.Safe(row, "description", () => m.Description);
                OptionalProductEvidence.Safe(row, "dateTimeUtc", () => m.DateTime.ToString("o")); OptionalProductEvidence.Safe(row, "errorCount", () => m.ErrorCount); OptionalProductEvidence.Safe(row, "warningCount", () => m.WarningCount);
                OptionalProductEvidence.Safe(row, "messages", () => TestResultMessageRows(m.Messages, depth + 1, visited));
                rows.Add(row);
            }
            return rows;
        }
        private static HmiObject TestResultsRow(TestResults results)
        {
            var row = new HmiObject();
            OptionalProductEvidence.Safe(row, "state", () => results.State.ToString()); OptionalProductEvidence.Safe(row, "errorCount", () => results.ErrorCount); OptionalProductEvidence.Safe(row, "warningCount", () => results.WarningCount);
            var visited = new[] { 0 }; OptionalProductEvidence.Safe(row, "messages", () => TestResultMessageRows(results.Messages, 0, visited)); row["messageCount"] = visited[0];
            return row;
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public OptionalProductReply ReadTestSuiteCases(string category, string name = "", int offset = 0, int limit = 100, string kind = "case")
            => _session.RunHmiStepTool("ListTestSuiteCases", meta =>
            {
                try
                {
                var k = _session.Plan().Kind;
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, k);
                meta["category"] = category; meta["kind"] = k; meta["collectionClass"] = collection.GetType().Name;
#if OPTION_TEST_SETS
                if (category == "application") OptionalProductEvidence.Safe(meta, "testSetCount", () => service.ApplicationTestGroup.ApplicationTestSets.Count);
#endif
                if (!string.IsNullOrEmpty(name))
                {
                    var item = ExactTestSuiteItem(collection, name, OptionalProductEvidence.CategoryLabel(category));
                    meta["records"] = new HmiArray(TestSuiteRow(item)); meta["expectedCount"] = 1; meta["actualCount"] = 1;
                    return "Test Suite " + OptionalProductEvidence.CategoryLabel(category) + " read (typed scalars and scope); no test executed.";
                }
                var all = PlcGroupOperations.Items(collection).Cast<IEngineeringObject>().Select(x => (object?)TestSuiteRow(x)).ToArray();
                OptionalProductEvidence.Page(all, offset, limit, meta);
                return "Test Suite " + (k == "testSet" ? "application test sets" : OptionalProductEvidence.CategoryLabel(category) + "s") + " read; no test executed.";
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });

        public OptionalProductReply ExchangeTestSuiteCase(string category, string action, string name, string filePath = "", string importOptions = "None", string loadOptions = "", bool dryRun = true, string kind = "case")
            => _session.RunHmiStepTool("ExchangeTestSuiteCase", meta =>
            {
                try
                {
                var r = _session.Plan();
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["expectedName"] = name;
                var target = action == "import" || action == "importTestSets" ? FindTestSuiteItem(collection, name) : ExactTestSuiteItem(collection, name, OptionalProductEvidence.CategoryLabel(category));
                if (target != null) meta["before"] = TestSuiteRow(target);
                FileInfo? file = null;
                if (action == "export") file = OptionalProductFiles.Plan(filePath);
                if (action == "import" || action == "importTestSets") { file = HmiFiles.RequireExistingInputFile(filePath, "filePath"); meta["importOptions"] = r.ImportOption; meta["loadOptions"] = string.Join("|", r.LoadFlags); }
                if (dryRun) return "Test Suite definition " + action + " preview; import may contain multiple definitions and the native ImportOptions govern replacements. Nothing changed.";
                meta["mayHaveChanged"] = action != "export"; meta["mayHaveWrittenFiles"] = action == "export";
                var import = (ImportOptions)Enum.Parse(typeof(ImportOptions), r.ImportOption);
                switch (action)
                {
                    case "export":
                        bool saved = target switch { RuleSet rs => rs.SaveToFile(file!), TestCase tc => tc.SaveToFile(file!), 
#if OPTION_SYSTEM_TEST
SystemTestCase stc => stc.SaveToFile(file!),
#endif
_ => throw new PlcSoftwareException("NotSupportedOnVersion", "SaveToFile is not available on " + target!.GetType().Name + ".") };
                        meta["nativeSignature"] = target!.GetType().Name + ".SaveToFile(FileInfo) -> " + saved; meta["nativeResult"] = saved; if (!saved) meta["operationSuccess"] = false;
                        meta["file"] = OptionalProductFiles.Verify(file!);
                        return "Test Suite definition exported (native bool " + saved + "); the file is verified by size / SHA-256, not by content. Project unchanged.";
                    case "import":
                        IList<IEngineeringObject> imported;
                        switch (collection)
                        {
                            case RuleSetComposition rules: var rs = (RSLoadOptions)Enum.Parse(typeof(RSLoadOptions), string.Join(",", r.LoadFlags)); meta["nativeSignature"] = "RuleSetComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", RSLoadOptions." + rs + ")"; imported = rules.LoadFromFile(file!, import, rs).Cast<IEngineeringObject>().ToList(); break;
                            case TestCaseComposition cases: var tc = (AppLoadOptions)Enum.Parse(typeof(AppLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "TestCaseComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", TCLoadOptions." + tc + ")"; imported = cases.LoadFromFile(file!, import, tc).Cast<IEngineeringObject>().ToList(); break;
#if OPTION_SYSTEM_TEST
                            case SystemTestCaseComposition system: var st = (SysLoadOptions)Enum.Parse(typeof(SysLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "SystemTestCaseComposition.LoadFromFile(FileInfo, ImportOptions." + import + ", TCLoadOptions." + st + ")"; imported = system.LoadFromFile(file!, import, st).Cast<IEngineeringObject>().ToList(); break;
#endif
                            default: throw new PlcSoftwareException("NotSupportedOnVersion", "LoadFromFile is not available on " + collection.GetType().Name + ".");
                        }
                        meta["imported"] = new HmiArray(imported.Select(x => (object?)TestSuiteRow(x)).ToArray()); meta["importedCount"] = imported.Count;
                        if (!string.IsNullOrEmpty(name)) meta["expectedPresenceVerified"] = FindTestSuiteItem(collection, name) != null;
                        return "Test Suite definitions imported (" + imported.Count + "); presence checks do not prove test semantics. No test run or automatic save.";
#if OPTION_TEST_SETS
                    case "importTestSets":
                        var ts = (TSLoadOptions)Enum.Parse(typeof(TSLoadOptions), r.LoadFlags[0]); meta["nativeSignature"] = "ApplicationTestSystemGroup.LoadFromFile(FileInfo, ImportOptions." + import + ", TSLoadOptions." + ts + ")";
                        var loaded = service.ApplicationTestGroup.LoadFromFile(file!, import, ts);
                        meta["imported"] = new HmiArray(loaded.Select(x => (object?)TestSuiteRow(x)).ToArray()); meta["importedCount"] = loaded.Count;
                        meta["importedTestSets"] = loaded.Count(x => x is ApplicationTestSet); meta["importedTestCases"] = loaded.Count(x => x is TestCase);
                        return "Application test sets and their test cases imported (" + loaded.Count + " objects); no test run or automatic save.";
#endif
                    default:
                        switch (target) { case RuleSet rs: rs.Delete(); break; case TestCase tc: tc.Delete(); break; 
#if OPTION_SYSTEM_TEST
#if OPTION_TEST_SETS
case SystemTestCase stc: stc.Delete(); break;
#endif
#endif
 
#if OPTION_TEST_SETS
case ApplicationTestSet set: set.Delete(); break;
#endif
 }
                        meta["nativeSignature"] = target!.GetType().Name + ".Delete()";
                        if (FindTestSuiteItem(collection, name) != null) throw new InvalidOperationException("Test definition still present after Delete(); deletion not verified.");
                        meta["expectedPresenceVerified"] = true; meta["verifiedAbsent"] = true;
                        return "Test Suite definition deleted and verified absent; no automatic save.";
                }
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });

        public OptionalProductReply RunTestSuiteCase(string category, string name = "", bool confirmExternalExecution = false, bool dryRun = true, string namesJson = "[]", bool runAll = false, string kind = "case")
            => _session.RunHmiStepTool("RunTestSuiteCase", meta =>
            {
                try
                {
                var r = _session.Plan();
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["dryRun"] = dryRun; meta["mayHaveExternalEffects"] = false; meta["runAll"] = runAll;
                var targets = r.Names.Select(n => ExactTestSuiteItem(collection, n, OptionalProductEvidence.CategoryLabel(category))).ToArray();
                meta["before"] = new HmiArray(targets.Select(t => (object?)TestSuiteRow(t)).ToArray());
                if (runAll) OptionalProductEvidence.Safe(meta, "expectedCount", () => PlcGroupOperations.Items(collection).Count()); else meta["expectedCount"] = targets.Length;
                // TIA V21, 2026-09-20 (docs/reference/real-machine-ledger.md): on empty groups,
                // TestCaseExecutor.Run(ApplicationTestSystemGroup) threw a native NullReferenceException inside TIA and
                // SystemTestCaseExecutor.Run(SystemTestSystemGroup) answered "No test case(s) in the selected project" - only the style-guide executor
                // returns a Success result for an empty group. Nothing to run means nothing is asked of TIA.
                if (runAll && meta["expectedCount"] is int total && total == 0)
                    throw new PlcSoftwareException("InvalidState", "The " + category + " system group holds no " + OptionalProductEvidence.CategoryLabel(category) + "s; runAll is refused (TIA answers a native NullReferenceException for an empty application group and 'No test case(s) in the selected project' for an empty system group).");
                if (dryRun) return "Native test execution preview. Application / system tests may start a PLCSIM Advanced instance or talk to the configured OPC UA server; real execution needs confirmExternalExecution=true. No test executed.";
                meta["mayHaveExternalEffects"] = r.External;
                TestResults results;
                switch (category)
                {
                    case "styleGuide":
                        var ruleExecutor = service.StyleGuideGroup.GetService<RuleSetExecutor>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "RuleSetExecutor service not provided by StyleGuideGroup.");
                        if (runAll) { meta["nativeSignature"] = "RuleSetExecutor.Run(StyleGuideSystemGroup)"; meta["operationStarted"] = true; results = ruleExecutor.Run(service.StyleGuideGroup); }
                        else if (targets.Length == 1) { meta["nativeSignature"] = "RuleSetExecutor.Run(RuleSet)"; meta["operationStarted"] = true; results = ruleExecutor.Run((RuleSet)targets[0]); }
                        else { meta["nativeSignature"] = "RuleSetExecutor.Run(IEnumerable<RuleSet>)"; meta["operationStarted"] = true; results = ruleExecutor.Run(targets.Cast<RuleSet>().ToList()); }
                        break;
                    case "application":
                        var caseExecutor = service.ApplicationTestGroup.GetService<TestCaseExecutor>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "TestCaseExecutor service not provided by ApplicationTestGroup.");
                        if (runAll) { meta["nativeSignature"] = "TestCaseExecutor.Run(ApplicationTestSystemGroup)"; meta["operationStarted"] = true; results = caseExecutor.Run(service.ApplicationTestGroup); }
#if OPTION_TEST_SETS
                        else if (r.Kind == "testSet") { if (targets.Length == 1) { meta["nativeSignature"] = "TestCaseExecutor.Run(ApplicationTestSet)"; meta["operationStarted"] = true; results = caseExecutor.Run((ApplicationTestSet)targets[0]); } else { meta["nativeSignature"] = "TestCaseExecutor.Run(IEnumerable<ApplicationTestSet>)"; meta["operationStarted"] = true; results = caseExecutor.Run(targets.Cast<ApplicationTestSet>().ToList()); } }
#endif
                        else if (targets.Length == 1) { meta["nativeSignature"] = "TestCaseExecutor.Run(TestCase)"; meta["operationStarted"] = true; results = caseExecutor.Run((TestCase)targets[0]); }
                        else { meta["nativeSignature"] = "TestCaseExecutor.Run(IEnumerable<TestCase>)"; meta["operationStarted"] = true; results = caseExecutor.Run(targets.Cast<TestCase>().ToList()); }
                        break;
#if OPTION_SYSTEM_TEST
                    default:
                        var systemExecutor = service.SystemTestGroup.GetService<SystemTestCaseExecutor>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "SystemTestCaseExecutor service not provided by SystemTestGroup.");
                        if (runAll) { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(SystemTestSystemGroup)"; meta["operationStarted"] = true; results = systemExecutor.Run(service.SystemTestGroup); }
                        else if (targets.Length == 1) { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(SystemTestCase)"; meta["operationStarted"] = true; results = systemExecutor.Run((SystemTestCase)targets[0]); }
                        else { meta["nativeSignature"] = "SystemTestCaseExecutor.Run(IEnumerable<SystemTestCase>)"; meta["operationStarted"] = true; results = systemExecutor.Run(targets.Cast<SystemTestCase>().ToList()); }
                        break;
#else
                    default: throw new NotSupportedException("System Test requires V18 or later.");
#endif
                }
                var row = TestResultsRow(results); meta["result"] = row; meta["apiCallSuccess"] = true;
                var state = results.State.ToString(); OptionalProductEvidence.RecordNativeState(meta, state); meta["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.TestSuite;
                bool passed = TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.TestSuite, state); meta["testPassed"] = passed; meta["operationSuccess"] = passed;
                return "Native Test Suite execution returned TestResults (state " + state + ", " + results.ErrorCount + " error(s), " + results.WarningCount + " warning(s)); testPassed reflects the native state only. No automatic project save or Portal close.";
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });

        public OptionalProductReply ManageTestSuiteCase(string category, string name, string action = "read", string kind = "case", string newName = "", string softwarePath = "", string instanceName = "", string executionMode = "", string opcUaServerAddress = "", string serverInterfaceType = "", string interfaceFolderPath = "", string updateOptions = "", string scopeJson = "[]", string targetName = "", string masterCopyPath = "", string libraryName = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageTestSuiteCase", meta =>
            {
                try
                {
#if !OPTION_SYSTEM_TEST
                if (category == "system") throw new NotSupportedException("System Test requires V18 or later.");
#endif
#if !OPTION_TEST_SCOPE
                if (category == "system" && action == "createFromMasterCopy") throw new NotSupportedException("SystemTestCaseComposition.CreateFrom requires V19 or later.");
                if (action == "setScope" && (category == "system" || category == "application" && !string.IsNullOrEmpty(executionMode)))
                    throw new NotSupportedException("The selected scope overload requires V19 or later.");
#endif
                var r = _session.Plan();
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var service = RequireTestSuite();
                var collection = TestSuiteCollection(service, category, r.Kind);
                meta["category"] = category; meta["kind"] = r.Kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                IEngineeringObject? target = action == "createFromMasterCopy" ? (string.IsNullOrEmpty(name) ? null : FindTestSuiteItem(collection, name)) : ExactTestSuiteItem(collection, name, OptionalProductEvidence.CategoryLabel(category));
                if (action == "createFromMasterCopy" && target != null) throw new PlcSoftwareException("InvalidState", OptionalProductEvidence.CategoryLabel(category) + " already exists: " + name);
                if (target != null) meta["before"] = TestSuiteRow(target);
                if (action == "read") return "Test Suite " + OptionalProductEvidence.CategoryLabel(category) + " read; nothing changed.";
                MasterCopy? masterCopy = action == "createFromMasterCopy" ? _session.ExactMasterCopy(libraryName, masterCopyPath) : null;
                RuleSet? copyTarget = action == "copyScope" ? (RuleSet)ExactTestSuiteItem(collection, targetName, "rule set") : null;
                if (copyTarget != null) meta["target"] = TestSuiteRow(copyTarget);
                PlcSoftware? plc = action == "setScope" && category == "application" ? _session.ExactPlcForEngineering(softwarePath, false) : null;
                var scopeObjects = new List<IEngineeringObject>(); var resolved = new HmiArray();
                if (action == "setScope" && category == "styleGuide") { foreach (var entry in r.Scope) scopeObjects.Add(ResolveStyleGuideScopeObject(entry, resolved)); meta["scopeObjects"] = resolved; }
                DirectoryInfo? folder = action == "setScope" && category == "system" && !string.IsNullOrEmpty(interfaceFolderPath) ? new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(interfaceFolderPath)) : null;
                if (folder != null && !folder.Exists) throw new DirectoryNotFoundException("interfaceFolderPath does not exist: " + interfaceFolderPath);
                if (action == "showInEditor")
                {
                    meta["operationStarted"] = true;
                    switch (target) { case RuleSet rs: rs.ShowInEditor(); break; case TestCase tc: tc.ShowInEditor(); break; 
#if OPTION_TEST_SETS
case ApplicationTestSet set: set.ShowInEditor(); break;
#endif
 default: throw new PlcSoftwareException("NotSupportedOnVersion", "ShowInEditor is not available on " + target!.GetType().Name + "."); }
                    meta["nativeSignature"] = target!.GetType().Name + ".ShowInEditor()";
                    return "Opened in the TIA Portal editor (WithUserInterface sessions only); project unchanged.";
                }
                if (dryRun) return "Test Suite " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "rename":
                        switch (target) { case RuleSet rs: rs.Name = newName; break; case TestCase tc: tc.Name = newName; break; 
#if OPTION_SYSTEM_TEST
case SystemTestCase stc: stc.Name = newName; break;
#endif
 
#if OPTION_TEST_SETS
case ApplicationTestSet set: set.Name = newName; break;
#endif
 }
                        meta["nativeSignature"] = target!.GetType().Name + ".Name = value"; name = newName; break;
                    case "createFromMasterCopy":
                        switch (collection)
                        {
                            case RuleSetComposition rules: meta["nativeSignature"] = "RuleSetComposition.CreateFrom(MasterCopy)"; target = rules.CreateFrom(masterCopy!); break;
                            case TestCaseComposition cases: meta["nativeSignature"] = "TestCaseComposition.CreateFrom(MasterCopy)"; target = cases.CreateFrom(masterCopy!); break;
#if OPTION_SYSTEM_TEST
#if OPTION_TEST_SCOPE
                            case SystemTestCaseComposition system: meta["nativeSignature"] = "SystemTestCaseComposition.CreateFrom(MasterCopy)"; target = system.CreateFrom(masterCopy!); break;
#else
                            case SystemTestCaseComposition _: throw new NotSupportedException("SystemTestCaseComposition.CreateFrom requires V19 or later.");
#endif
#endif
                            default: throw new NotSupportedException("CreateFrom is unavailable for the selected Test Suite definition.");
                        }
                        var generated = PlcGroupOperations.Get(target!, "Name")?.ToString() ?? ""; meta["generatedName"] = generated;
                        if (!string.IsNullOrEmpty(name) && generated != name) { switch (target) { case RuleSet rs: rs.Name = name; break; case TestCase tc: tc.Name = name; break; 
#if OPTION_SYSTEM_TEST
case SystemTestCase stc: stc.Name = name; break;
#endif
 } meta["renamedTo"] = name; }
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
#if OPTION_TEST_SCOPE
                                else { var mode = (ExecutionMode)Enum.Parse(typeof(ExecutionMode), r.ExecutionMode); meta["nativeSignature"] = "TestCase.SetScope(PlcSoftware, string, ExecutionMode." + mode + ")"; tc.SetScope(plc!, instanceName, mode); }
#else
                                else throw new NotSupportedException("The selected scope overload requires V19 or later.");
#endif
                                break;
#if OPTION_SYSTEM_TEST
#if OPTION_TEST_SCOPE
                            case SystemTestCase stc:
                                var iface = (ServerInterfaces)Enum.Parse(typeof(ServerInterfaces), r.ServerInterface);
                                if (folder == null) { meta["nativeSignature"] = "SystemTestCase.SetScope(string, ServerInterfaces." + iface + ")"; stc.SetScope(opcUaServerAddress, iface); }
                                else { meta["nativeSignature"] = "SystemTestCase.SetScope(string, ServerInterfaces." + iface + ", DirectoryInfo)"; stc.SetScope(opcUaServerAddress, iface, folder); }
                                break;
#else
                            case SystemTestCase _: throw new NotSupportedException("SystemTestCase.SetScope requires V19 or later.");
#endif
#endif
                            default: throw new NotSupportedException("SetScope is unavailable for the selected Test Suite definition.");
                        }
                        break;
                }
                var after = FindTestSuiteItem(collection, name) ?? target;
                meta["after"] = TestSuiteRow(after!);
                return "Test Suite " + action + " executed and read back; no test run or automatic save.";
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });
}
#endif
