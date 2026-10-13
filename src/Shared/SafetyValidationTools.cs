using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    // Typed Safety Validation Assistant option package (V21 only; every tool answers NotSupportedOnVersion on
    // V20). Activation tests live under "Cross-device functions > Safety Activation Tests" and may be organised in user groups, which
    // groupPathJson names outermost first ([] = root).
    [McpServerToolType]
    internal sealed class SafetyValidationTools
    {
        private readonly SafetyValidationService _service;

        public SafetyValidationTools(SafetyValidationService service) => _service = service;

        [McpServerTool(Name="ListSafetyActivationTests"), Description("[L2][Safety][READ] Typed Safety Validation Assistant read (Project.GetService<SafetyValidationAssistant>): activation tests of the root or of the group at groupPath (name, author, evaluation device name, OverallState NotTested/Succeeded/Failed, safety function count; includeSafetyFunctions adds the safety functions with test name / description / TestState / trace configuration, includeConditions their conditions), the subgroups at that level and - at the root - the evaluation devices offered by DeviceQuery.EvaluationDevices(). With name one activation test is read in full including AvailableDevices() and TestValidity.CheckValidity(). Offset / limit pagination. V21 only; nothing changed.")]
        public CallToolResult ListSafetyActivationTests(
            string[] groupPath=null!,
            string name="",
            [Description("includeSafetyFunctions: true also returns the safety functions.")] bool includeSafetyFunctions=false,
            [Description("includeConditions: true also returns the conditions.")] bool includeConditions=false,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("ListSafetyActivationTests", true, false,
                () => _service.ReadSafetyActivationTests(SecurityToolContract.Path(groupPath),name,includeSafetyFunctions,includeConditions,offset,limit));
        [McpServerTool(Name="ManageSafetyActivationTest"), Description("[L2][Safety][WRITE] One activation test of the Safety Validation Assistant (groupPath selects the user group, [] = root): read (scalars + AvailableDevices), create (ActivationTestComposition.Create(name, evaluation DeviceItem named by evaluationDeviceName from DeviceQuery.EvaluationDevices)), createFromTest (CreateFrom(existing test named by sourceName), then renamed to name), createFromMasterCopy (CreateFrom(MasterCopy at masterCopyPath in the project library or the open global library libraryName)), rename / setAuthor (newValue), changeEvaluationDevice (ChangeEvaluationDevice with a device from DeviceQuery.EvaluationDevices - a drive from AvailableDevices is refused natively as 'not a valid evaluation device'), checkValidity (TestValidity.CheckValidity: state, error count, messages), generateReport (ActivationTestPrintout.Generate -> new .xlsx at filePath), export (Export(FileInfo, ExportOptions None|WithDefaults|WithReadOnly[, DocumentInfoOptions ExportSetting|InstalledProducts|CreatedTimeStamp|All], flags joined with |) -> new XML file), import (ActivationTestComposition.Import(file, importOptions None|Override|Rename|SkipInactiveCultures|ActivateInactiveCultures); the file names the tests, name is not used), delete (confirmDelete). Output files are verified by size / SHA-256. Real project (2.7.42, evaluation device +S1-K1): create / createFromTest (generated name 'Safety Activation Test_1', renamed; author not copied) / rename / setAuthor / export / import Rename (-> 'MCP_TMP_AT_R_1') and None (native 'composition already contains an object with the same Name') / generateReport (.xlsx) / delete verified. V21 only; default preview; no automatic save.")]
        public CallToolResult ManageSafetyActivationTest(
            string name,
            [Description("action: the operation to perform - read | create | createFromTest | createFromMasterCopy | rename | setAuthor | changeEvaluationDevice | checkValidity | generateReport | export | import | delete.")] string action="read",
            string[] groupPath=null!,
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
            => SecurityToolContract.Invoke("ManageSafetyActivationTest", dryRun || action == "read" || action == "checkValidity", action == "export" || action == "import" || action == "generateReport",
                () => _service.ManageSafetyActivationTest(name,action,SecurityToolContract.Path(groupPath),evaluationDeviceName,sourceName,masterCopyPath,libraryName,newValue,filePath,exportOptions,documentInfoOptions,importOptions,confirmDelete,dryRun));
        [McpServerTool(Name="ManageSafetyActivationTestGroup"), Description("[L2][Safety][WRITE] Activation test user groups of the Safety Validation Assistant (ActivationTestGroups, nested through ActivationTestUserGroup.Groups; groupPath = parent group, [] = root): read (all groups at that level, or one group with its activation tests when name is given), create (ActivationTestUserGroupComposition.Create(name)), createFromMasterCopy (CreateFrom(MasterCopy at masterCopyPath / libraryName)), rename (newName), delete (confirmDelete; nonempty groups are refused). V21 only; default preview; no automatic save.")]
        public CallToolResult ManageSafetyActivationTestGroup(
            [Description("action: the operation to perform - read | create | createFromMasterCopy | rename | delete.")] string action="read",
            string[] groupPath=null!,
            string name="",
            string masterCopyPath="",
            string libraryName="",
            string newName="",
            bool confirmDelete=false,
            bool dryRun=true)
            => SecurityToolContract.Invoke("ManageSafetyActivationTestGroup", dryRun || action == "read", false,
                () => _service.ManageSafetyActivationTestGroup(action,SecurityToolContract.Path(groupPath),name,masterCopyPath,libraryName,newName,confirmDelete,dryRun));
        [McpServerTool(Name="ManageSafetyFunction"), Description("[L2][Safety][WRITE] Safety functions (test cases) of one activation test (activationTest + groupPath): read (all, or one by name - the generated SafetyFunction.Name or a unique TestName - with conditions and trace configuration: IsTraced, PretriggerTime, RecordingDuration, Signals), create (SafetyFunctionComposition.Create() then properties {testName, description}), createFrom (CreateFrom(source named by sourceName)), update (properties {testName, description}; official: attributes are editable only while the function has no test result), resetTestResult (ResetTestResult()), checkValidity / checkTraceValidity (TestValidity.CheckValidity on the function / its TraceConfiguration), setTrace (properties {isTraced, pretriggerTime, recordingDuration} + optional signals string[] -> TraceConfiguration.IsTraced + SetAttribute), export (Export(FileInfo, ExportOptions[, DocumentInfoOptions]) -> new XML file), import (SafetyFunctionComposition.Import(file, importOptions None|Override|SkipInactiveCultures|ActivateInactiveCultures)), delete (confirmDelete). V21 only; default preview; no automatic save.")]
        public CallToolResult ManageSafetyFunction(
            [Description("activationTest: exact name of the activation test.")] string activationTest,
            [Description("action: the operation to perform - read | create | createFrom | update | resetTestResult | checkValidity | setTrace | checkTraceValidity | export | import | delete.")] string action="read",
            string[] groupPath=null!,
            string name="",
            [Description("sourceName: exact name of the source object to copy from.")] string sourceName="",
            AttributeMap<Scalar> properties=null!,
            string filePath="",
            [Description("exportOptions: Openness export option name for the export action ('' = default).")] string exportOptions="",
            [Description("documentInfoOptions: Openness document-info option name for the export action ('' = default).")] string documentInfoOptions="",
            string importOptions="",
            bool confirmDelete=false,
            bool dryRun=true,
            [Description("signals: exact trace signal names, accepted only by setTrace. Omission preserves the current list; an empty array clears it.")] string[] signals=null!)
        {
            bool current = action == "export" || action == "import";
            if (signals != null && action != "setTrace") return SecurityToolContract.Reject("ManageSafetyFunction", "signals", current);
            if (properties != null && properties.ContainsKey("signals")) return SecurityToolContract.Reject("ManageSafetyFunction", "properties", current);
            return SecurityToolContract.Invoke("ManageSafetyFunction", dryRun || action == "read" || action == "checkValidity" || action == "checkTraceValidity", current,
                () => _service.ManageSafetyFunction(activationTest,action,SecurityToolContract.Path(groupPath),name,sourceName,SecurityToolContract.TraceProperties(properties!,signals),filePath,exportOptions,documentInfoOptions,importOptions,confirmDelete,dryRun));
        }
        [McpServerTool(Name="ManageSafetyFunctionCondition"), Description("[L2][Safety][WRITE] Conditions of one safety function (activationTest + safetyFunction + groupPath), addressed by index (position in SafetyFunction.Conditions): read (all, or one), create (ConditionComposition.Create(DeviceItem named by deviceName from ActivationTest.AvailableDevices(), signalUsage OperatingMode|InputCondition|Response, signalName) then properties), update (properties {comment, deviceName, signalName, signalUsage, initialInput, executedInput, response}; the three inputs take true/false or FALSE|TRUE|NotRelevant, and the inputs owned by the signal usage - OperatingMode: executedInput, InputCondition: initialInput + executedInput, Response: response - refuse NotRelevant up front, because TIA refuses it natively after the descriptive fields were already written), checkValidity (TestValidity.CheckValidity on the condition; 2.7.42 real project: took TIA Portal V21 down once right after such a half-refused update - prefer the function-level check), delete. Real project (2.7.42, F-CPU + G120C conditions): create / update / delete and the ConditionValue readback verified. V21 only; default preview; no automatic save.")]
        public CallToolResult ManageSafetyFunctionCondition(
            [Description("activationTest: exact name of the activation test.")] string activationTest,
            [Description("safetyFunction: exact name of the safety function.")] string safetyFunction,
            [Description("action: the operation to perform - read | create | update | checkValidity | delete.")] string action="read",
            string[] groupPath=null!,
            [Description("index: 0-based index of the condition.")] int index=-1,
            [Description("deviceName: exact device name.")] string deviceName="",
            [Description("signalUsage: OperatingMode | InputCondition | Response.")] string signalUsage="",
            [Description("signalName: exact signal name.")] string signalName="",
            AttributeMap<Scalar> properties=null!,
            bool dryRun=true)
            => SecurityToolContract.Invoke("ManageSafetyFunctionCondition", dryRun || action == "read" || action == "checkValidity", false,
                () => _service.ManageSafetyFunctionCondition(activationTest,safetyFunction,action,SecurityToolContract.Path(groupPath),index,deviceName,signalUsage,signalName,SecurityToolContract.Map(properties),dryRun));
    }
}
