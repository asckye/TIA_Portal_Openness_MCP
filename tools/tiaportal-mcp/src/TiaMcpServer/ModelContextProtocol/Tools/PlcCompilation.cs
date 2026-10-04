using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.CompilerDiagnostics;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class PlcCompilation
    {
        internal static ResponseCompileDiagnose CompileAndDiagnoseCore(string softwarePath, string password)
        {
            try
            {
                var compileWatch = System.Diagnostics.Stopwatch.StartNew();
                var result = EngineServices.Get<Siemens.Portal>().CompileSoftware(softwarePath, password);

                // CollectCompilerMessages 逐条收集诊断，拿不到的记进 CollectFailures，
                // 让调用方区分没有明细与诊断收集不完整。
                var compileMs = compileWatch.ElapsedMilliseconds;
                var collected = CollectCompilerMessages(result.Messages);
                var summary = collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount);
                summary["compileElapsedMs"] = compileMs;
                summary["softwarePath"] = softwarePath;
                summary["timestamp"] = DateTime.Now;
                var raw = collected.Raw;
                var errs = collected.Errors;
                var warns = collected.Warnings;
                var info = collected.Info;

                if (collected.CollectFailures.Count > 0)
                {
                    // 放进 info 让人/模型直接看见，别只藏在 meta 里。
                    info = new List<string>(info);
                    foreach (var f in collected.CollectFailures)
                        info.Add("State=Information; Description=[诊断收集不完整] " + f);
                }

                return new ResponseCompileDiagnose
                {
                    Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; root counts and diagnostics scopes are in Meta.",
                    State = summary["effectiveState"]!.ToString(),
                    ErrorCount = collected.HasError && result.ErrorCount == 0 ? (int?)null : result.ErrorCount,
                    WarningCount = collected.HasWarning && result.WarningCount == 0 ? (int?)null : result.WarningCount,
                    Errors = errs,
                    Warnings = warns,
                    Info = info,
                    RawMessages = raw,
                    Meta = summary
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        internal static ResponseCompile BuildCompileResponse(string softwarePath, object result)
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

        internal static int ReadIntProperty(object value, string propertyName)
        {
            var raw = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (raw is int i) return i;
            return int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0;
        }
    }
}
