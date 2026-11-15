using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class UnifiedHmiTools
    {

        public ResponseMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string tagName, bool dryRun = true)
        {
            try
            {
                return _service.BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName, dryRun);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error binding button '{buttonName}' to tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType, bool dryRun = true)
        {
            try
            {
                return _service.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType, dryRun);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error ensuring button event handler '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            int maxMembers = 200)
        {
            try
            {
                var res = _service.DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers);
                res.Meta ??= ResponseMeta.Unstamped(false, ("operationSuccess", false));
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["memberCount"] = res.Members?.Count() ?? 0;
                return res;
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error describing button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string scriptCode,
            string? globalDefinitionAreaScriptCode = null,
            bool? async = null,
            bool syntaxCheck = false)
        {
            try
            {
                return _service
                    .SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async,
                        syntaxCheck);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error setting button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage EnsureUnifiedHmiButtonAction(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string actionKind,
            string targetTag,
            bool syntaxCheck = false, bool dryRun = true)
        {
            try
            {
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, new[] { targetTag });
                var kind = recipe["recipeKind"]?.ToString() ?? "";
                var script = recipe["script"]?.ToString() ?? "";
                var allowed = new[] { "set-bit", "reset-bit", "toggle-bit" };
                if (!allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Only set-bit/reset-bit/toggle-bit recipes can be applied by this safe high-level tool.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected by safety policy.", Meta = recipe };
                }
                if (recipe["ok"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(script) || script
                        .IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Recipe has errors, empty script, or TODO placeholder.";
                    return new ResponseMessage {
                        Message = "Unified HMI button action rejected because the generated script is not directly applicable.", Meta = recipe };
                }

                if (dryRun)
                {
                    TiaMcp.Adapters.Contracts.EngineeringInputValidation.NameOrAbsolutePath(screenName);
                    recipe["dryRun"] = true; recipe["mayHaveChanged"] = false; recipe["operationSuccess"] = true;
                    recipe["plannedChanges"] = new JsonObject {
                        ["screenName"] = screenName, ["buttonName"] = buttonName, ["eventType"] = eventType, ["scriptCode"] = script, ["syntaxCheck"] =
                            syntaxCheck
                    };
                    return new ResponseMessage { Message = "Unified HMI button action preview; no native calls or writes.", Meta = recipe };
                }
                var applied = _service.ApplyUnifiedHmiButtonAction(hmiSoftwarePath, screenName, buttonName, eventType, script, syntaxCheck);
                if (applied.Meta != null) foreach (var pair in applied.Meta) recipe[pair.Key] = pair.Value?.DeepClone();
                recipe["applyStatus"] = applied.Meta?["operationSuccess"]?.GetValue<bool>() == true ? "applied" : "apply-failed";
                return new ResponseMessage
                {
                    Message = recipe["applyStatus"]?.ToString() == "applied"
                        ? "Unified HMI button action applied via generated recipe."
                        : "Unified HMI button action was not applied; inspect setMeta.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex,
                    $"Unexpected error applying Unified HMI button action '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }
    }
}
