using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    // Phase 6 ⑥-③ (2.7.42): typed Safety Validation Assistant option package (V21 only; every tool answers NotSupportedOnVersion on
    // V20). Activation tests live under "Cross-device functions > Safety Activation Tests" and may be organised in user groups, which
    // groupPathJson names outermost first ([] = root).
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadSafetyActivationTests"), Description("[L2][Safety][READ] Typed Safety Validation Assistant read (Project.GetService<SafetyValidationAssistant>): activation tests of the root or of the group at groupPathJson (name, author, evaluation device name, OverallState NotTested/Succeeded/Failed, safety function count; includeSafetyFunctions adds the safety functions with test name / description / TestState / trace configuration, includeConditions their conditions), the subgroups at that level and - at the root - the evaluation devices offered by DeviceQuery.EvaluationDevices(). With name one activation test is read in full including AvailableDevices() and TestValidity.CheckValidity(). Offset / limit pagination. V21 only; nothing changed.")]
        public static ResponseMessage ReadSafetyActivationTests(
            string groupPathJson="[]",
            string name="",
            [Description("includeSafetyFunctions: true also returns the safety functions.")] bool includeSafetyFunctions=false,
            [Description("includeConditions: true also returns the conditions.")] bool includeConditions=false,
            int offset=0,
            int limit=100)
            => Portal.ReadSafetyActivationTests(groupPathJson,name,includeSafetyFunctions,includeConditions,offset,limit);
        [McpServerTool(Name="ManageSafetyActivationTest"), Description("[L2][Safety][WRITE] One activation test of the Safety Validation Assistant (groupPathJson selects the user group, [] = root): read (scalars + AvailableDevices), create (ActivationTestComposition.Create(name, evaluation DeviceItem named by evaluationDeviceName from DeviceQuery.EvaluationDevices)), createFromTest (CreateFrom(existing test named by sourceName), then renamed to name), createFromMasterCopy (CreateFrom(MasterCopy at masterCopyPath in the project library or the open global library libraryName)), rename / setAuthor (newValue), changeEvaluationDevice (ChangeEvaluationDevice with a device from DeviceQuery.EvaluationDevices - a drive from AvailableDevices is refused natively as 'not a valid evaluation device'), checkValidity (TestValidity.CheckValidity: state, error count, messages), generateReport (ActivationTestPrintout.Generate -> new .xlsx at filePath), export (Export(FileInfo, ExportOptions None|WithDefaults|WithReadOnly[, DocumentInfoOptions ExportSetting|InstalledProducts|CreatedTimeStamp|All], flags joined with |) -> new XML file), import (ActivationTestComposition.Import(file, importOptions None|Override|Rename|SkipInactiveCultures|ActivateInactiveCultures); the file names the tests, name is not used), delete (confirmDelete). Output files are verified by size / SHA-256. Real project (2.7.42, evaluation device +S1-K1): create / createFromTest (generated name 'Safety Activation Test_1', renamed; author not copied) / rename / setAuthor / export / import Rename (-> 'MCP_TMP_AT_R_1') and None (native 'composition already contains an object with the same Name') / generateReport (.xlsx) / delete verified. V21 only; default preview; no automatic save.")]
        public static ResponseMessage ManageSafetyActivationTest(
            string name,
            [Description("action: the operation to perform - read | create | createFromTest | createFromMasterCopy | rename | setAuthor | changeEvaluationDevice | checkValidity | generateReport | export | import | delete.")] string action="read",
            string groupPathJson="[]",
            [Description("evaluationDeviceName: exact name of the evaluation device (F-CPU).")] string evaluationDeviceName="",
            [Description("sourceName: exact name of the source object to copy from.")] string sourceName="",
            string masterCopyPath="",
            string libraryName="",
            [Description("newValue: the new value for the action (rename / setAuthor / changeEvaluationDevice).")] string newValue="",
            string filePath="",
            [Description("exportOptions: Openness export option name for the export action ('' = default).")] string exportOptions="",
            [Description("documentInfoOptions: Openness document-info option name for the export action ('' = default).")] string documentInfoOptions="",
            string importOptions="",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageSafetyActivationTest(name,action,groupPathJson,evaluationDeviceName,sourceName,masterCopyPath,libraryName,newValue,filePath,exportOptions,documentInfoOptions,importOptions,confirmDelete,dryRun);
        [McpServerTool(Name="ManageSafetyActivationTestGroup"), Description("[L2][Safety][WRITE] Activation test user groups of the Safety Validation Assistant (ActivationTestGroups, nested through ActivationTestUserGroup.Groups; groupPathJson = parent group, [] = root): read (all groups at that level, or one group with its activation tests when name is given), create (ActivationTestUserGroupComposition.Create(name)), createFromMasterCopy (CreateFrom(MasterCopy at masterCopyPath / libraryName)), rename (newName), delete (confirmDelete; nonempty groups are refused). V21 only; default preview; no automatic save.")]
        public static ResponseMessage ManageSafetyActivationTestGroup(
            [Description("action: the operation to perform - read | create | createFromMasterCopy | rename | delete.")] string action="read",
            string groupPathJson="[]",
            string name="",
            string masterCopyPath="",
            string libraryName="",
            string newName="",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageSafetyActivationTestGroup(action,groupPathJson,name,masterCopyPath,libraryName,newName,confirmDelete,dryRun);
        [McpServerTool(Name="ManageSafetyFunction"), Description("[L2][Safety][WRITE] Safety functions (test cases) of one activation test (activationTest + groupPathJson): read (all, or one by name - the generated SafetyFunction.Name or a unique TestName - with conditions and trace configuration: IsTraced, PretriggerTime, RecordingDuration, Signals), create (SafetyFunctionComposition.Create() then propertiesJson {testName, description}), createFrom (CreateFrom(source named by sourceName)), update (propertiesJson {testName, description}; official: attributes are editable only while the function has no test result), resetTestResult (ResetTestResult()), checkValidity / checkTraceValidity (TestValidity.CheckValidity on the function / its TraceConfiguration), setTrace (propertiesJson {isTraced, pretriggerTime, recordingDuration, signals [..]} -> TraceConfiguration.IsTraced + SetAttribute), export (Export(FileInfo, ExportOptions[, DocumentInfoOptions]) -> new XML file), import (SafetyFunctionComposition.Import(file, importOptions None|Override|SkipInactiveCultures|ActivateInactiveCultures)), delete (confirmDelete). V21 only; default preview; no automatic save.")]
        public static ResponseMessage ManageSafetyFunction(
            [Description("activationTest: exact name of the activation test.")] string activationTest,
            [Description("action: the operation to perform - read | create | createFrom | update | resetTestResult | checkValidity | setTrace | checkTraceValidity | export | import | delete.")] string action="read",
            string groupPathJson="[]",
            string name="",
            [Description("sourceName: exact name of the source object to copy from.")] string sourceName="",
            string propertiesJson="{}",
            string filePath="",
            [Description("exportOptions: Openness export option name for the export action ('' = default).")] string exportOptions="",
            [Description("documentInfoOptions: Openness document-info option name for the export action ('' = default).")] string documentInfoOptions="",
            string importOptions="",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageSafetyFunction(activationTest,action,groupPathJson,name,sourceName,propertiesJson,filePath,exportOptions,documentInfoOptions,importOptions,confirmDelete,dryRun);
        [McpServerTool(Name="ManageSafetyFunctionCondition"), Description("[L2][Safety][WRITE] Conditions of one safety function (activationTest + safetyFunction + groupPathJson), addressed by index (position in SafetyFunction.Conditions): read (all, or one), create (ConditionComposition.Create(DeviceItem named by deviceName from ActivationTest.AvailableDevices(), signalUsage OperatingMode|InputCondition|Response, signalName) then propertiesJson), update (propertiesJson {comment, deviceName, signalName, signalUsage, initialInput, executedInput, response}; the three inputs take true/false or FALSE|TRUE|NotRelevant, and the inputs owned by the signal usage - OperatingMode: executedInput, InputCondition: initialInput + executedInput, Response: response - refuse NotRelevant up front, because TIA refuses it natively after the descriptive fields were already written), checkValidity (TestValidity.CheckValidity on the condition; 2.7.42 real project: took TIA Portal V21 down once right after such a half-refused update - prefer the function-level check), delete. Real project (2.7.42, F-CPU + G120C conditions): create / update / delete and the ConditionValue readback verified. V21 only; default preview; no automatic save.")]
        public static ResponseMessage ManageSafetyFunctionCondition(
            [Description("activationTest: exact name of the activation test.")] string activationTest,
            [Description("safetyFunction: exact name of the safety function.")] string safetyFunction,
            [Description("action: the operation to perform - read | create | update | checkValidity | delete.")] string action="read",
            string groupPathJson="[]",
            [Description("index: 0-based index of the condition.")] int index=-1,
            [Description("deviceName: exact device name.")] string deviceName="",
            [Description("signalUsage: OperatingMode | InputCondition | Response.")] string signalUsage="",
            [Description("signalName: exact signal name.")] string signalName="",
            string propertiesJson="{}",
            bool dryRun=true)
            => Portal.ManageSafetyFunctionCondition(activationTest,safetyFunction,action,groupPathJson,index,deviceName,signalUsage,signalName,propertiesJson,dryRun);
    }
}
