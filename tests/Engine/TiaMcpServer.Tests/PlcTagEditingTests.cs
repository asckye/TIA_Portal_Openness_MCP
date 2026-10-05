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

            // 2.7.47 lenient binding: object/array for a *Json string, string numbers / booleans, enum casing retry
            var M = typeof(TiaMcpServer.ModelContextProtocol.McpServer);
            check(TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonNode.Parse("[\"PLC_1\"]")!, typeof(string))!.GetValue<string>() == "[\"PLC_1\"]", "bridge: array for a *Json string parameter becomes its JSON text");
            check(TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create("42")!, typeof(int))!.GetValue<long>() == 42 && TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create("true")!, typeof(bool))!.GetValue<bool>() && TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create(1)!, typeof(bool))!.GetValue<bool>(), "bridge: string number / string boolean / 0-1 boolean coerced");
            check(TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create(4.6)!, typeof(string))!.GetValue<string>() == "4.6" && TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create("x")!, typeof(string)) == null && TiaMcpServer.ModelContextProtocol.McpServer.CoerceArgument(JsonValue.Create("abc")!, typeof(int)) == null, "bridge: number for a string takes its text; fitting or unparsable values untouched");
            var ps = typeof(PlcTagEditingTests).GetMethod(nameof(EnumSample), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetParameters();
            var call = new object?[] { "PLC", "Export" };
            check(TiaMcpServer.ModelContextProtocol.McpServer.TryCanonicalizeEnumArgument("action must be one of: export/import (case-sensitive).", ps, call, out var en, out var ev) && en == "action" && ev == "export" && (string)call[1]! == "export", "bridge: case-insensitive enum retry rewrites the argument");
            check(!TiaMcpServer.ModelContextProtocol.McpServer.TryCanonicalizeEnumArgument("action must be one of: export/import (case-sensitive).", ps, new object?[] { "PLC", "delete" }, out _, out _), "bridge: no retry when the value is not an alternative");
        }

        private static void EnumSample(string softwarePath, string action) { }

        private sealed class IMcpServer { }
    }
}
