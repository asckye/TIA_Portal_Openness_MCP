using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class RuntimeSettingsTools
    {
        private readonly RuntimeSettingsService _runtimeSettings;

        public RuntimeSettingsTools(RuntimeSettingsService service) => _runtimeSettings = service;

        [McpServerTool(Name = "GetUnifiedRuntimeSettings"),
                Description("[L2][HMI-Unified][READ] Read supported root scalars and nested runtime settings using exact dotted names, e.g. " +
                "OpcUaServerRuntimeSettings.MaxSessionCount. Select 1..12 unique fields. V20+: Telemetry, UPSS and tag settings; V21: " +
                "TagOptimizationActive and exclusive operation. Exact expectedProject and softwarePath required. fields is a string array, default " +
                "[\"StartScreen\",\"ScreenResolution\"]. Returns per-field type/value, public setter availability and exact enum names in capabilities. " +
                "Coverage is the requested fields only; LanguageAndFonts, Windows auto-start and Runtime Manager are not covered. No save, compile, " +
                "download or runtime restart. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ReadUnifiedRuntimeSettingsV4(
            string softwarePath,
            [Description("Exact project NAME, e.g. ExampleProject; a file path is invalid. Match Project.Name, not " +
                "expectedProjectFile.")] string expectedProject,
            [Description("fields: structured string array input.")] string[] fields = null!)
            => RuntimeToolContract.Run("GetUnifiedRuntimeSettings", true, true, () =>
            {
                return ReadUnifiedRuntimeSettings(softwarePath, expectedProject, RuntimeToolContract
                        .Names(fields, "fields", true, new[] { "StartScreen", "ScreenResolution" }));
            });

        public ResponseMessage ReadUnifiedRuntimeSettings(string softwarePath,
                [Description("Exact project NAME, e.g. ExampleProject; a file path is invalid. Match Project.Name, not " +
                "expectedProjectFile.")] string expectedProject,
                string fieldsJson = "[\"StartScreen\",\"ScreenResolution\"]")
            => _runtimeSettings.ReadUnifiedRuntimeSettings(softwarePath, expectedProject, fieldsJson);
        [McpServerTool(Name = "SetUnifiedRuntimeSettings"),
                Description("[L2][HMI-Unified][WRITE] Update 1..12 supported root scalars or exact dotted nested fields using public leaf setters, " +
                "e.g. MaxLoginRuntimeSettings.MaxLoginErrors. Nested objects are never replaced. V20+: Telemetry, UPSS and tag settings; V21: " +
                "TagOptimizationActive and exclusive operation. LanguageAndFonts, Windows auto-start and Runtime Manager are not covered. " +
                "changes is an object, e.g. " +
                "{\"StartScreen\":\"/Group/Main\",\"ScreenResolution\":\"SR_1920X1080\"}; use exact enum names from GetUnifiedRuntimeSettings. StartScreen " +
                "MUST be a full readable screen path (old encoded input accepted): validates existence and unique name in the selected HMI before writing " +
                    "the API's name value. "
                    +
                "Default dryRun=true returns before/proposed/token and planHash. Every execution requires previewToken, expectedPlanHash and " +
                "expectedProjectFile from the same preview. Integers must be JSON integers in the CLR type range; enum names are exact. " +
                "Validates all fields before any write and verifies final readback. Captured values are restored on failure; inspect rollbackCompleted " +
                "and cleanupRequired. Multi-field writes are not atomic. No save, compile, download, runtime restart or close. " +
                "Unsupported fields are rejected. Native " +
                "behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedRuntimeSettingsV4(
            string softwarePath,
            [Description("Exact project NAME, e.g. ExampleProject; a file path is invalid. Match Project.Name, not " +
                "expectedProjectFile.")] string expectedProject,
            [Description("changes: structured AttributeMap<Scalar> input.")] AttributeMap<Scalar> changes,
            bool dryRun = true,
            string previewToken = "", string expectedPlanHash = "", string expectedProjectFile = "")
            => McpServer.RunNativeWritePreview("SetUnifiedRuntimeSettings", dryRun,
                new { softwarePath, expectedProject, changes },
                () => Update(softwarePath, expectedProject, changes, true, previewToken),
                () => Update(softwarePath, expectedProject, changes, false, previewToken), expectedPlanHash, expectedProjectFile);

        private CallToolResult Update(string softwarePath, string expectedProject, AttributeMap<Scalar> changes, bool dryRun, string previewToken)
            => RuntimeToolContract.Run("SetUnifiedRuntimeSettings", dryRun, true, () =>
            {
                return UpdateUnifiedRuntimeSettings(softwarePath, expectedProject, RuntimeToolContract
                        .Json(changes, "changes", new InputContract<AttributeMap<Scalar>>(InputSchema.Map(InputSchema.Scalar()), new InputBudget())), dryRun,
                        previewToken);
            });

        public ResponseMessage UpdateUnifiedRuntimeSettings(string softwarePath,
                [Description("Exact project NAME, e.g. ExampleProject; a file path is invalid. Match Project.Name, not " +
                "expectedProjectFile.")] string expectedProject,
                string changesJson, bool dryRun = true, string expectedToken = "")
            => _runtimeSettings.UpdateUnifiedRuntimeSettings(softwarePath, expectedProject, changesJson, dryRun, expectedToken);
    }
}
