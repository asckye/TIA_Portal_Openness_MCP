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

        public ResponseMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath,
            string screenName,
            uint width = 0,
            uint height = 0, bool dryRun = true)
        {
            try
            {
                return _service.EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height, dryRun);
            }
            catch (Exception ex) when (ex is ArgumentException or TiaMcp.Adapters.Contracts.AdapterPreconditionException)
            {
                throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error ensuring HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string itemType = "Button",
            int left = 0,
            int top = 0,
            uint width = 120,
            uint height = 40,
            string text = "", bool dryRun = true)
        {
            try
            {
                return _service.EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text, dryRun);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error ensuring HMI screen item '{itemName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage ReadUnifiedHmiTexts(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string textProperty = "Text")
            => _service.ReadUnifiedHmiTexts(hmiSoftwarePath, screenName, itemName, textProperty);

        public ResponseMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath,
            string screenName,
            string designJson,
            bool strict = true, bool dryRun = true)
        {
            try
            {
                var design = ScreenDesign(screenName, JsonNode.Parse(designJson)!.AsObject());
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design.ToJsonString(), strict, dryRun);
            }
            catch (ArgumentException ex)
            {
                var refusal = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(ex.Message, ex.ParamName, isArgument: true, innerException: ex);
                throw LegacyFailure(refusal, refusal.Message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error applying Unified HMI design to '{screenName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage ApplyUnifiedHmiTheme(string hmiSoftwarePath,
            string screenName,
            string themeJson, bool dryRun = true)
        {
            try
            {
                var design = ScreenDesign(screenName, HmiDesignBuilder.BuildUnifiedHmiThemeDesignJson(themeJson).Data!);
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design.ToJsonString(), dryRun: dryRun);
            }
            catch (ArgumentException ex)
            {
                var refusal = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(ex.Message, ex.ParamName, isArgument: true, innerException: ex);
                throw LegacyFailure(refusal, refusal.Message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error applying Unified HMI theme: {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage ApplyUnifiedHmiLayout(string hmiSoftwarePath,
            string screenName,
            string layoutJson, bool dryRun = true)
        {
            try
            {
                var design = ScreenDesign(screenName, HmiDesignBuilder.BuildUnifiedHmiLayoutDesignJson(layoutJson).Data!);
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design.ToJsonString(), dryRun: dryRun);
            }
            catch (ArgumentException ex)
            {
                var refusal = new TiaMcp.Adapters.Contracts.AdapterPreconditionException(ex.Message, ex.ParamName, isArgument: true, innerException: ex);
                throw LegacyFailure(refusal, refusal.Message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error applying Unified HMI layout: {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        private static JsonObject ScreenDesign(string screenName, JsonObject built)
        {
            var screen = built["screen"]?.DeepClone() as JsonObject ?? new JsonObject();
            if (screen["Name"] is JsonNode name)
            {
                string target = screenName.StartsWith("/", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(screenName.Substring(screenName.LastIndexOf('/') + 1)) : screenName;
                if (!string.Equals(name.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("design.screen.name must name the target screen; renaming is refused");
                screen.Remove("Name");
            }
            return new JsonObject { ["screen"] = screen, ["items"] = built["items"]?.DeepClone() ?? new JsonArray() };
        }
    }
}
