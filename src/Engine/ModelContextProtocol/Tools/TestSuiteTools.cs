using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // Typed TIA Portal Test Suite option package (Project.GetService<TestSuiteService>; identical on V20 / V21).
    // category styleGuide (rule sets) / application (test cases, kind testSet = ApplicationTestSet) / system (system test cases).
    [McpServerToolType]
    internal sealed class TestSuiteTools
    {
        private readonly TestSuiteService _testSuite;

        public TestSuiteTools(TestSuiteService testSuite) => _testSuite = testSuite;

        [McpServerTool(Name="ListTestSuiteCases"), Description("[L2][Project][READ] Typed Test Suite read: category styleGuide (StyleGuideGroup.RuleSets), application (ApplicationTestGroup.TestCases, or kind testSet for ApplicationTestGroup.ApplicationTestSets) or system (SystemTestGroup.SystemTestCases). Rows carry the class and name plus the scope: TestCase.GetScope() (PLC name), SystemTestCase OPC UA server address / interface type (UserDefined | StandardSIMATIC | SiOMECompanionSpecification) / interface folder. Exact name or live offset pagination. NotSupported when the Test Suite package is missing; no test executed. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ListTestSuiteCases(
            [Description("category: styleGuide | application | system.")] string category,
            string name="",
            int offset=0,
            int limit=100,
            string kind="case")
            => OptionalPackageContract.Run("ListTestSuiteCases", false, () => _testSuite.ReadTestSuiteCases(category,name,offset,limit,kind), string.IsNullOrEmpty(name) ? offset : (int?)null, string.IsNullOrEmpty(name) ? limit : (int?)null);
        [McpServerTool(Name="ExchangeTestSuiteCase"), Description("[L2][Project][WRITE] Typed Test Suite definition exchange: export (RuleSet / TestCase / SystemTestCase.SaveToFile(FileInfo) -> new .xml / .tat / .tst file verified by size and SHA-256; native bool reported), import (RuleSetComposition / TestCaseComposition / SystemTestCaseComposition.LoadFromFile(file, importOptions None|Override|SkipInactiveCultures|ActivateInactiveCultures, loadOptions: style guide RSLoadOptions None|IgnorePropertyErrors|IgnoreMissingAttributes|SkipInvalidObjects joined with |, application / system TCLoadOptions None|IgnoreInvalidObject); the file may hold several definitions, name is optional and only verified afterwards), importTestSets (application only: ApplicationTestSystemGroup.LoadFromFile(file, importOptions, TSLoadOptions None|IgnoreInvalidObject) returns the imported test sets and test cases), delete (Delete() on the case or, with kind testSet, the ApplicationTestSet; verified absent). Default preview; no test execution or automatic save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeTestSuiteCase(
            [Description("category: styleGuide | application | system.")] string category,
            [Description("action: the operation to perform - import | export | delete | importTestSets.")] string action,
            string name,
            string filePath="",
            string importOptions="None",
            [Description("loadOptions: Openness load option name ('' = default).")] string loadOptions="",
            bool dryRun=true,
            string kind="case")
            => OptionalPackageContract.Run("ExchangeTestSuiteCase", !dryRun, () => _testSuite.ExchangeTestSuiteCase(category,action,name,filePath,importOptions,loadOptions,dryRun,kind));
        [McpServerTool(Name="RunTestSuiteCase"), Description("[L2][Project][EXECUTE] Typed Test Suite execution through the executor service of the category's system group: one definition (name), several (names) or the whole group (runAll) - RuleSetExecutor.Run(RuleSet | IEnumerable<RuleSet> | StyleGuideSystemGroup), TestCaseExecutor.Run(TestCase | IEnumerable<TestCase> | ApplicationTestSystemGroup, or with kind testSet ApplicationTestSet | IEnumerable<ApplicationTestSet>), SystemTestCaseExecutor.Run(SystemTestCase | IEnumerable<SystemTestCase> | SystemTestSystemGroup). Returns the typed TestResults (state Success|Information|Warning|Error, error / warning counts, recursive messages with path, state, description, UTC time) and testPassed. runAll on an empty group is refused (2.7.42 real project: the application executor answered a native NullReferenceException, the system executor 'No test case(s) in the selected project'; only the style-guide executor returned Success for an empty group). Default preview; application / system runs need confirmExternalExecution=true because they start a PLCSIM Advanced instance (SystemManaged) or use the configured instance / OPC UA server (official: SupportSimulationNotEnabledException / OpcUaServerAddressNotValidException / LicenseNotFoundException are the recoverable failures). No automatic save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult RunTestSuiteCase(
            [Description("category: styleGuide | application | system.")] string category,
            string name="",
            [Description("confirmExternalExecution: must be true to run against an externally managed PLCSIM instance.")] bool confirmExternalExecution=false,
            bool dryRun=true,
            [Description("names: Array of exact names (or a comma-separated list where the tool says so).")] string[] names = null!,
            [Description("runAll: true runs every test case of the category.")] bool runAll=false,
            string kind="case")
            => OptionalPackageContract.Run("RunTestSuiteCase", !dryRun, () => _testSuite.RunTestSuiteCase(category,name,confirmExternalExecution,dryRun,V4Json.Serialize(names ?? Array.Empty<string>()),runAll,kind));
        [McpServerTool(Name="ManageTestSuiteCase"), Description("[L2][Project][WRITE] Typed Test Suite definition management: read; rename (Name = newName); setScope - style guide: RuleSet.SetScope(updateOptions Add|Override, scope objects from scope [{kind project | deviceGroup (name) | plc (softwarePath -> CPU device item) | blocks | tags | types (softwarePath, optional groupPath below the system group) | units (softwarePath -> PlcUnitProvider.UnitGroup)}], native bool reported), application: TestCase.SetScope(PlcSoftware at softwarePath[, instanceName, executionMode SystemManagedPLCSIMInstance | ExternallyManagedPLCSIMInstance]; software controllers are refused natively), system: SystemTestCase.SetScope(opcUaServerAddress opc.tcp://server:port/path, serverInterfaceType UserDefined | StandardSIMATIC | SiOMECompanionSpecification[, interfaceFolderPath absolute directory]); copyScope (RuleSet.CopyScope(target rule set named by targetName)); createFromMasterCopy (RuleSetComposition / TestCaseComposition / SystemTestCaseComposition.CreateFrom(MasterCopy at masterCopyPath in the project library or the open global library libraryName), renamed to name when given); showInEditor (rule sets, test cases, test sets; WithUserInterface sessions). Default preview; readback after the change; no test run or automatic save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageTestSuiteCase(
            [Description("category: styleGuide | application | system.")] string category,
            string name,
            [Description("action: the operation to perform - read | rename | setScope | copyScope | createFromMasterCopy | showInEditor.")] string action="read",
            string kind="case",
            string newName="",
            string softwarePath="",
            [Description("instanceName: exact PLCSIM Advanced instance name.")] string instanceName="",
            [Description("executionMode: SystemManagedPLCSIMInstance | ExternallyManagedPLCSIMInstance.")] string executionMode="",
            [Description("opcUaServerAddress: OPC UA server endpoint URL.")] string opcUaServerAddress="",
            [Description("serverInterfaceType: UserDefined | StandardSIMATIC | SiOMECompanionSpecification.")] string serverInterfaceType="",
            [Description("interfaceFolderPath: folder path of the OPC UA server interface.")] string interfaceFolderPath="",
            [Description("updateOptions: update option name ('' = default).")] string updateOptions="",
            [Description("scope: Array of typed test scopes (see the tool description).")] TestScope[] scope = null!,
            [Description("targetName: exact name of the target object.")] string targetName="",
            string masterCopyPath="",
            string libraryName="",
            bool dryRun=true)
            => OptionalPackageContract.Run("ManageTestSuiteCase", action == "showInEditor" || (!dryRun && action != "read"), () => _testSuite.ManageTestSuiteCase(category,name,action,kind,newName,softwarePath,instanceName,executionMode,opcUaServerAddress,serverInterfaceType,interfaceFolderPath,updateOptions,V4Json.Serialize(scope ?? Array.Empty<TestScope>()),targetName,masterCopyPath,libraryName,dryRun));
    }
}
