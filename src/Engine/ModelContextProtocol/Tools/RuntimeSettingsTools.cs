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

        [McpServerTool(Name = "GetUnifiedRuntimeSettings"), Description("[L2][HMI-Unified][READ]Read HMI Runtime startup screen and supported root runtime settings. Exact expectedProject and softwarePath required. fields is a string array, default [\"StartScreen\",\"ScreenResolution\"]. Returns per-field type/value, public setter availability and exact enum names in capabilities. Coverage is the requested root fields only; nested settings, Windows auto-start and Runtime Manager are not covered. No save, compile, download or runtime restart. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ReadUnifiedRuntimeSettingsV4(
            string softwarePath,
            string expectedProject,
            [Description("fields: structured string array input.")] string[] fields = null!)
            => RuntimeToolContract.Run("GetUnifiedRuntimeSettings", true, true, () =>
            {
                return ReadUnifiedRuntimeSettings(softwarePath, expectedProject, RuntimeToolContract.Names(fields, "fields", true, new[] { "StartScreen", "ScreenResolution" }));
            });

        public ResponseMessage ReadUnifiedRuntimeSettings(string softwarePath, string expectedProject, string fieldsJson = "[\"StartScreen\",\"ScreenResolution\"]")
            => _runtimeSettings.ReadUnifiedRuntimeSettings(softwarePath, expectedProject, fieldsJson);
        [McpServerTool(Name = "SetUnifiedRuntimeSettings"), Description("[L2][HMI-Unified][WRITE]Update supported root HMI Runtime settings using public Openness setters. changes is an object, e.g. {\"StartScreen\":\"/Group/Main\",\"ScreenResolution\":\"SR_1920X1080\"}; use exact enum names from GetUnifiedRuntimeSettings. StartScreen MUST be a full URI-escaped screen path: validates existence and unique name in the selected HMI before writing the API's name value. Default dryRun=true returns before/proposed/token. Apply identical changes with dryRun=false and expectedToken. Validates all requested fields before any write and verifies final readback. On failure inspect appliedFields/mayHaveChanged; multi-field writes are not atomic. No automatic rollback, save, compile, download, runtime restart or close. Unsupported/nested fields are rejected. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedRuntimeSettingsV4(
            string softwarePath,
            string expectedProject,
            [Description("changes: structured AttributeMap<Scalar> input.")] AttributeMap<Scalar> changes,
            bool dryRun = true,
            string expectedToken = "")
            => RuntimeToolContract.Run("SetUnifiedRuntimeSettings", dryRun, true, () =>
            {
                return UpdateUnifiedRuntimeSettings(softwarePath, expectedProject, RuntimeToolContract.Json(changes, "changes", new InputContract<AttributeMap<Scalar>>(InputSchema.Map(InputSchema.Scalar()), new InputBudget())), dryRun, expectedToken);
            });

        public ResponseMessage UpdateUnifiedRuntimeSettings(string softwarePath, string expectedProject, string changesJson, bool dryRun = true, string expectedToken = "")
            => _runtimeSettings.UpdateUnifiedRuntimeSettings(softwarePath, expectedProject, changesJson, dryRun, expectedToken);
    }
}
