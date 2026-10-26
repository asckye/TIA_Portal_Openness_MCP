using System;
using System.IO;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    /// <summary>
    /// issue #36：写 Unified HMI 的 JS 事件脚本后强制调 SyntaxCheck()，在 TIA V21 上会偶发
    /// 抛 NonRecoverableException 并带走整个 Portal 进程，刚写进去的脚本随之丢失。
    ///
    /// 修法有两半，这里两半都盯：
    /// 1) SyntaxCheck 改成默认不跑 —— 默认值一旦被改回 true，崩溃就原样回来，而且
    ///    离线用例一条都不会红（真崩要有博途才复现），所以只能盯源码里的签名。
    /// 2) 进程级致命错不许再被当成普通失败吞掉 —— 这条有真实现可以喂输入。
    /// </summary>
    internal static class UnifiedScriptSyntaxCheckTests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            RunClassifierTests(check);
            RunDefaultOffContractTests(check, skip);
        }

        private static void RunClassifierTests(Action<bool, string> check)
        {
            // 进程已经没了的几种形状，都必须认出来。
            check(PortalFailureClassifier.IsPortalProcessLost(
                    new FakeNonRecoverableException("TIA Portal terminated unexpectedly.")),
                "Openness 的 NonRecoverableException 要认成「进程没了」");

            check(PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException("outer",
                        new FakeNonRecoverableException("inner"))),
                "包在 InnerException 里的 NonRecoverable 也要认出来");

            // A type name quoted in prose is not evidence that the process was lost.
            check(!PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException(
                        "Siemens.Engineering.NonRecoverableException: the engineering process has exited")),
                "A quoted native type name must not invalidate the session");

            check(PortalFailureClassifier.IsPortalProcessLost(
                    new System.Runtime.InteropServices.COMException("RPC server unavailable")),
                "进程没了以后的 COMException 要认成「进程没了」");

            // [反向哨兵] 普通业务失败绝不能被误判成「进程没了」——误判的代价是让调用方
            // 以为整个会话作废、去做重连重做，而其实只要修一处参数重试即可。
            // 少了这两条，「无脑 return true」的坏实现能通过上面全部用例。
            check(!PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException("Screen item 'Btn_Start' not found on screen 'Main'.")),
                "[反向哨兵] 找不到画面元素只是普通失败，不是进程没了");

            check(!PortalFailureClassifier.IsPortalProcessLost(
                    new ArgumentException("eventType 'Pressed' is not a valid HmiButtonEventType value.")),
                "[反向哨兵] 参数错只是普通失败，不是进程没了");

            // [哨兵] null 不许崩：分类器本身坏掉不该把调用方一起带走。
            check(!PortalFailureClassifier.IsPortalProcessLost(null),
                "[哨兵] null 返回 false 且不抛");
            string? observed = null;
            try
            {
                PortalFailureClassifier.ProcessLostObserved = reason => observed = reason;
                check(PortalFailureClassifier.IsPortalProcessLost(new FakeCommunicationObjectFaultedException()), "Faulted channel is terminal");
                check(observed != null, "Worker hook sees a caught native channel fault");
                observed = null;
                check(!PortalFailureClassifier.IsPortalProcessLost(new ArgumentException()) && observed == null, "Ordinary error does not invalidate worker");
                check(!PortalFailureClassifier.IsPortalProcessLost(new ThrowingMessageException()), "Fault classification tolerates an unreadable remote exception message");
                PortalFailureClassifier.ProcessLostObserved = _ => throw new IOException("diagnostics unavailable");
                check(PortalFailureClassifier.IsPortalProcessLost(new FakeNonRecoverableException("lost")), "Diagnostic hook failure does not hide terminal fault");
            }
            finally { PortalFailureClassifier.ProcessLostObserved = null; }
        }

        private static void RunDefaultOffContractTests(Action<bool, string> check, Action<string, string> skip)
        {
            // 这两个文件依赖 Siemens.Engineering / MCP SDK，链不进这个套件，只能读源码盯形状。
            // Unified HMI 族由 UnifiedHmiService / UnifiedHmiTools 提供。
            var portalSource = FindSource(Path.Combine("Adapters", "Native", "Hmi", "UnifiedHmiAdapter.cs"));
            var toolSource = FindSource(Path.Combine("Shared", "UnifiedHmiTools.cs"));
            var policySource = FindSource(Path.Combine("Adapters", "Native", "Hmi", "HmiExchangePolicy.cs"));

            if (portalSource == null || toolSource == null || policySource == null)
            {
                // 找不到源码就明确跳过并计数，不冒充通过 —— 「少跑了一批」和「全过了」
                // 必须长得不一样，否则换台机器跑就是个永远不响的报警器。
                skip("SyntaxCheck 默认关闭的源码形状", "找不到引擎源码目录（只在仓内跑得了）");
                return;
            }

            var portal = File.ReadAllText(portalSource);
            var tool = File.ReadAllText(toolSource);
            var policy = File.ReadAllText(policySource);

            // 签名钉在方法声明本身上，别的方法凑巧带同样的尾巴不算数。async 不传就不改（P8-81e F6）。
            const string portalMethod = "public HmiMessage SetUnifiedHmiButtonEventScriptCode(";
            check(Declaration(portal, portalMethod).EndsWith(
                    "string? globalDefinitionAreaScriptCode = null, bool? async = null, bool syntaxCheck = false)", StringComparison.Ordinal),
                "Portal.SetUnifiedHmiButtonEventScriptCode 的 syntaxCheck 必须默认 false（issue #36）");

            check(Declaration(tool, "public CallToolResult SetUnifiedHmiButtonEventScriptCodeV4(").EndsWith(
                    "bool syntaxCheck = false)", StringComparison.Ordinal),
                "MCP 工具暴露的 syntaxCheck 必须默认 false（issue #36）");

            // 默认关闭时绝不能发 syntaxErrorCount：调用方读到「没有这个键」必须理解成
            // 「没查」。发一个 0 出去，等于把「没做检查」谎报成「检查通过」。
            check(portal.Contains("meta[\"syntaxCheckStatus\"] = \"skipped\"") &&
                  portal.Contains("syntaxCheckSkippedReason"),
                "跳过 SyntaxCheck 时必须显式标注 skipped 与原因");

            // ScriptCode 没写进去就该直接失败，不该先拿一个会弄崩 V21 的调用去检查
            // 一份根本不存在的脚本。这里盯的是两段代码的先后顺序：写入经 HmiExchangePolicy
            // 预检公开可写属性，写后回读不一致即抛错，全部发生在 SyntaxCheck 之前。
            var methodAt = portal.IndexOf(portalMethod, StringComparison.Ordinal);
            var writeAt = methodAt < 0 ? -1 : portal.IndexOf(
                "HmiExchangePolicy.UpdateScript(script, scriptCode ?? string.Empty, globalDefinitionAreaScriptCode, async,", methodAt, StringComparison.Ordinal);
            var checkAt = methodAt < 0 ? -1 : portal.IndexOf("meta[\"syntaxCheckRequested\"]", methodAt, StringComparison.Ordinal);
            var update = Member(policy, "internal static void UpdateScript(");
            var write = Member(policy, "internal static void WriteScalars(");
            check(writeAt > 0 && checkAt > 0 && writeAt < checkAt
                  && update.Contains("[\"ScriptCode\"] = code", StringComparison.Ordinal)
                  && update.Contains("WriteScalars(script, changes, beforeWrite);", StringComparison.Ordinal)
                  && write.Contains("property?.SetMethod?.IsPublic != true || property.GetMethod?.IsPublic != true", StringComparison.Ordinal)
                  && write.Contains("throw new NotSupportedException(\"Public writable property unavailable: \"", StringComparison.Ordinal)
                  && write.Contains("change.Property.SetValue(target, change.Value);", StringComparison.Ordinal)
                  && write.Contains("if (!Equals(change.Property.GetValue(target), change.Value))", StringComparison.Ordinal)
                  && write.Contains("throw new InvalidOperationException(\"Property readback differs: \"", StringComparison.Ordinal),
                "ScriptCode 写失败的判断必须排在 SyntaxCheck 之前");
        }

        /// <summary>从方法名起取到参数表的右括号（其后是方法体或表达式体）；找不到返回空串。</summary>
        private static string Declaration(string source, string start)
        {
            var at = source.IndexOf(start, StringComparison.Ordinal);
            if (at < 0) return string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(source.Substring(at), @"^.*?\)(?=\s*(?:\{|=>))",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            return match.Success ? match.Value : string.Empty;
        }

        /// <summary>从成员声明取到下一个 internal static 成员之前；找不到返回空串。</summary>
        private static string Member(string source, string start)
        {
            var at = source.IndexOf(start, StringComparison.Ordinal);
            if (at < 0) return string.Empty;
            var next = source.IndexOf("internal static", at + start.Length, StringComparison.Ordinal);
            return next < 0 ? source.Substring(at) : source.Substring(at, next - at);
        }

        /// <summary>从测试程序集所在目录往上爬找引擎源码；找不到返回 null，由调用方明确跳过。</summary>
        private static string? FindSource(string relative)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 10 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "src", relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            return null;
        }

        /// <summary>
        /// 冒充 Openness 的致命异常：这个套件不引用 Siemens.Engineering（引用了就得装博途才编得动），
        /// 而分类器本来就按类型名判断，所以类名必须和真家伙一致。
        /// </summary>
        private sealed class FakeNonRecoverableException : Exception
        {
            internal FakeNonRecoverableException(string message) : base(message)
            {
            }
        }
        private sealed class FakeCommunicationObjectFaultedException : Exception { }
        private sealed class ThrowingMessageException : Exception { public override string Message => throw new InvalidOperationException("remote exception unavailable"); }
    }
}
