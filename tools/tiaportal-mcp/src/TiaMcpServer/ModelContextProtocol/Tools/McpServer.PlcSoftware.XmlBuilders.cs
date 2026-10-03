using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region plc software - XmlBuilders

        private static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
        {
            var ok = data["ok"]?.GetValue<bool>() == true;
            var xml = data["xml"]?.GetValue<string>();

            // Builders use one of two error shapes:
            //   PlcBuilderToolJson:        ["error"] = string?
            //   ClassicHmi*XmlBuilder:     ["errors"] = JsonArray of string
            string[]? errorList = null;
            if (data["errors"] is JsonArray errArr)
            {
                errorList = errArr.Where(e => e != null).Select(e => e!.GetValue<string>()).ToArray();
                if (errorList.Length == 0) errorList = null;
            }
            else
            {
                var singleError = data["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(singleError))
                    errorList = new[] { singleError! };
            }

            string[]? warningList = null;
            if (data["warnings"] is JsonArray warnArr)
            {
                warningList = warnArr.Where(w => w != null).Select(w => w!.GetValue<string>()).ToArray();
                if (warningList.Length == 0) warningList = null;
            }

            return new ResponseXmlBuild
            {
                Ok = ok,
                Message = ok ? successMessage : successMessage + " with validation findings",
                Data = data,
                Xml = xml,
                Errors = errorList,
                Warnings = warningList,
                Meta = ResponseMeta.Basic(ok, ("offlineOnly", true))
            };
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(name) ? "plc_artifact" : name.Trim())
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "plc_artifact" : safe;
        }

        private static ResponseCompile BuildCompileResponse(string softwarePath, object result)
        {
            var collected = new CompilerMessageCollectResult();
            try
            {
                var messagesValue = result.GetType().GetProperty("Messages")?.GetValue(result);
                collected = CollectCompilerMessages(messagesValue);
            }
            catch (Exception ex)
            {
                collected.CollectFailures.Add(ex.Message);
            }

            var state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            var errorCount = ReadIntProperty(result, "ErrorCount");
            var warningCount = ReadIntProperty(result, "WarningCount");
            var summary = collected.Summary(state, errorCount, warningCount);
            return new ResponseCompile
            {
                Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; see Meta for root counts and diagnostic scopes.",
                State = summary["effectiveState"]!.ToString(),
                ErrorCount = collected.HasError && errorCount == 0 ? (int?)null : errorCount,
                WarningCount = collected.HasWarning && warningCount == 0 ? (int?)null : warningCount,
                Messages = collected.Raw,
                Meta = summary
            };
        }

        private static int ReadIntProperty(object value, string propertyName)
        {
            var raw = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (raw is int i) return i;
            return int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0;
        }

        #endregion
    }
}
