using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // 2.7.46: ManagePlcTagDefinition request logic - the multilingual Comment is split off the scalar properties.
    internal static class PlcTagEditingTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            PlcTagEditingLogic.Request V(string action, string kind = "tag", string props = "{}", string dataType = "", string address = "", bool dryRun = true)
                => PlcTagEditingLogic.Validate(action, kind, "Tag_1", dataType, address, props, dryRun);

            check(!V("read").Writes && !V("update", props: "{\"Comment\":\"c\"}").Writes && V("update", props: "{\"Comment\":\"c\"}", dryRun: false).Writes, "plc tag: read / preview never write, real update writes");
            var r = V("update", props: "{\"Comment\":\"电机启动\",\"ExternalVisible\":false}", dryRun: false);
            check(r.Scalars.Count == 1 && r.Scalars.ContainsKey("ExternalVisible") && r.Comments.Length == 1 && r.Comments[0].Culture == null && r.Comments[0].Text == "电机启动", "plc tag: string Comment -> editing language, scalars kept apart");
            r = V("update", props: "{\"Comment\":{\"zh-CN\":\"文本\",\"en-US\":\"text\"}}", dryRun: false);
            check(r.Scalars.Count == 0 && r.Comments.Select(c => c.Culture).SequenceEqual(new[] { "zh-CN", "en-US" }) && r.Comments[1].Text == "text", "plc tag: object Comment -> per culture");
            r = V("create", "constant", "{\"Comment\":\"k\"}", "Int", "42", dryRun: false);
            check(r.Kind == "constant" && r.Writes && r.Comments.Length == 1, "plc constant: create with comment");
            check(Fails<ArgumentException>(() => V("update", props: "{}")), "plc tag: update without properties refused");
            check(Fails<ArgumentException>(() => V("update", props: "{\"Name\":\"x\"}")), "plc tag: rename refused");
            check(Fails<ArgumentException>(() => V("read", props: "{\"Comment\":\"c\"}")) && Fails<ArgumentException>(() => V("delete", props: "{\"Comment\":\"c\"}")), "plc tag: properties on read / delete refused");
            check(Fails<ArgumentException>(() => V("create", dataType: "Bool")), "plc tag: create without address refused");
            check(Fails<ArgumentException>(() => V("update", props: "{\"Comment\":5}")) && Fails<ArgumentException>(() => V("update", props: "{\"Comment\":{}}")) && Fails<ArgumentException>(() => V("update", props: "{\"Comment\":{\"zh-CN\":1}}")), "plc tag: non-string / empty / non-string-culture Comment refused");
            check(Fails<ArgumentException>(() => V("update", "value")) && Fails<ArgumentException>(() => V("rename")), "plc tag: unknown kind / action refused");
            check(Fails<ArgumentException>(() => PlcTagEditingLogic.Validate("read", "tag", "", "", "", "{}", true)), "plc tag: empty name refused");

            // 2.7.46 CallTool bridge binding rules (real project: ExportBlocks / ExportTypes unreachable, "" for keyword defaults, args as a JSON string)
            check(TiaMcpServer.ModelContextProtocol.McpServer.IsInfrastructureParameter(typeof(IMcpServer)) && !TiaMcpServer.ModelContextProtocol.McpServer.IsInfrastructureParameter(typeof(string)), "bridge: infrastructure parameter detection by name");
        }

        private sealed class IMcpServer { }
    }
}
