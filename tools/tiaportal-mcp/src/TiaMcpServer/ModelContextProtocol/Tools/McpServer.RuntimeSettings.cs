using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ReadUnifiedRuntimeSettings"), Description("[L2][HMI-Unified][READ]Read HMI Runtime startup screen and supported root runtime settings. Exact expectedProject and softwarePath required. fieldsJson is a string array, default [\"StartScreen\",\"ScreenResolution\"]. Returns per-field type/value, public setter availability and exact enum names in capabilities. Coverage is the requested root fields only; nested settings, Windows auto-start and Runtime Manager are not covered. No save, compile, download or runtime restart.")]
        public static ResponseMessage ReadUnifiedRuntimeSettings(
            string softwarePath,
            string expectedProject,
            [Description("fieldsJson: JSON array of field names to read ('[]' = all).")] string fieldsJson = "[\"StartScreen\",\"ScreenResolution\"]")
            => Portal.ReadUnifiedRuntimeSettings(softwarePath, expectedProject, fieldsJson);
        [McpServerTool(Name = "UpdateUnifiedRuntimeSettings"), Description("[L2][HMI-Unified][WRITE]Update supported root HMI Runtime settings using public Openness setters. changesJson is an object, e.g. {\"StartScreen\":\"/Group/Main\",\"ScreenResolution\":\"SR_1920X1080\"}; use exact enum names from ReadUnifiedRuntimeSettings. StartScreen MUST be a full URI-escaped screen path: validates existence and unique name in the selected HMI before writing the API's name value. Default dryRun=true returns before/proposed/token. Apply identical changes with dryRun=false and expectedToken. Validates all requested fields before any write and verifies final readback. On failure inspect appliedFields/mayHaveChanged; multi-field writes are not atomic. No automatic rollback, save, compile, download, runtime restart or close. Unsupported/nested fields are rejected.")]
        public static ResponseMessage UpdateUnifiedRuntimeSettings(
            string softwarePath,
            string expectedProject,
            [Description("changesJson: JSON object field name -> new value.")] string changesJson,
            bool dryRun = true,
            string expectedToken = "")
            => Portal.UpdateUnifiedRuntimeSettings(softwarePath, expectedProject, changesJson, dryRun, expectedToken);
    }
}
