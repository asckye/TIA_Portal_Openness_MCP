using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class UnifiedMultilingualTextTests
    {
        public sealed class Language { public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en-US"); }
        public sealed class TextItem
        {
            public Language Language { get; set; } = new Language();
            private string text = "original";
            public bool IgnoreWrite;
            public bool FailRead;
            public Func<string,string>? Normalize;
            public Action? OnWrite;
            public string Text { get => FailRead ? throw new InvalidOperationException("readback failed") : text; set { if (!IgnoreWrite) text = Normalize?.Invoke(value) ?? value; OnWrite?.Invoke(); } }
        }
        public sealed class MultilingualText { public List<TextItem> Items { get; } = new List<TextItem>(); }
        public sealed class Control
        {
            public MultilingualText Text { get; } = new MultilingualText();
            public MultilingualText AlternateText { get; } = new MultilingualText();
            public MultilingualText ToolTipText { get; } = new MultilingualText();
        }
        private static TextItem Row(string culture) => new TextItem { Language = new Language { Culture = CultureInfo.GetCultureInfo(culture) } };
        private static bool Fails(Action action)
        {
            try { action(); return false; } catch (InvalidOperationException) { return true; }
        }
        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== Unified multilingual language selection, clear, readback and bridge status ==");
            foreach (var property in new[] { "Text", "AlternateText", "ToolTipText" })
            {
                var control = new Control();
                var values = (MultilingualText)typeof(Control).GetProperty(property)!.GetValue(control)!;
                var zh = Row("zh-CN"); var en = Row("en-US"); var de = Row("de-DE");
                values.Items.AddRange(new[] { en, zh, de });
                zh.Normalize = s => s == "" ? "<body><p/></body>" : s;
                var cleared = UnifiedMultilingualText.WriteDetailed(control, property, "", "zh-CN");
                check(cleared["verified"]!.GetValue<bool>() && cleared["actualRawText"]!.ToString() == "<body><p/></body>" && en.Text == "original", property + " observed TIA empty normalization accepted");
                foreach (var raw in new[] { "<body><p> </p></body>", "<body><p>&nbsp;</p></body>", "<body><p><br/></p></body>", "<body><p></p></body>" })
                {
                    zh.Normalize = _ => raw;
                    check(!UnifiedMultilingualText.WriteDetailed(control, property, "", "zh-CN")["verified"]!.GetValue<bool>(), property + " unobserved empty equivalence rejected: " + raw);
                }
                zh.Normalize = _ => "<body><p/></body>";
                check(!UnifiedMultilingualText.WriteDetailed(control, property, " ", "zh-CN")["verified"]!.GetValue<bool>(), property + " whitespace is not clear");
                check(!UnifiedMultilingualText.WriteDetailed(control, property, "<body><p></p></body>", "zh-CN")["verified"]!.GetValue<bool>(), property + " explicit HTML compares exactly");
                zh.Normalize = null; zh.IgnoreWrite = true;
                check(!UnifiedMultilingualText.WriteDetailed(control, property, "different", "zh-CN")["verified"]!.GetValue<bool>(), property + " ignored setter still detected");
                zh.IgnoreWrite = false; zh.OnWrite = () => en.Text = "changed";
                var corrupted = UnifiedMultilingualText.WriteDetailed(control, property, "new", "zh-CN");
                check(!corrupted["verified"]!.GetValue<bool>() && corrupted["setterCompleted"]!.GetValue<bool>() && corrupted["mayHaveChanged"]!.GetValue<bool>() && corrupted["after"] != null, property + " failure retains actual before/after");
                zh.OnWrite = () => throw new InvalidOperationException("after setter");
                var threw = UnifiedMultilingualText.WriteDetailed(control, property, "written then throw", "zh-CN");
                check(!threw["verified"]!.GetValue<bool>() && threw["state"]!.ToString()=="ReadBack" && threw["after"]!=null, property + " throwing setter still read back");
                zh.OnWrite = () => zh.FailRead = true;
                var unknown = UnifiedMultilingualText.WriteDetailed(control, property, "unknown", "zh-CN");
                check(unknown["state"]!.ToString()=="Unknown" && unknown["mayHaveChanged"]!.GetValue<bool>() && unknown["after"]==null, property + " readback failure explicitly unknown");
            }
            foreach (var property in new[] { "Text", "AlternateText", "ToolTipText" })
            foreach (var order in new[] { new[] { "en-US", "de-DE", "zh-CN" }, new[] { "zh-CN", "en-US", "de-DE" }, new[] { "de-DE", "zh-CN", "en-US" } })
            {
                var control = new Control();
                var texts = (MultilingualText)typeof(Control).GetProperty(property)!.GetValue(control)!;
                foreach (var language in order) texts.Items.Add(Row(language));
                var en = texts.Items.Find(x => x.Language.Culture.Name == "en-US")!;
                var zh = texts.Items.Find(x => x.Language.Culture.Name == "zh-CN")!;
                var de = texts.Items.Find(x => x.Language.Culture.Name == "de-DE")!;
                var result = UnifiedMultilingualText.Write(control, property, "中文 & <文本>", "zh-CN");
                check(zh.Text == "<body><p>中文 &amp; &lt;文本&gt;</p></body>" && en.Text == "original" && de.Text == "original", property + " exact language independent of order");
                check(JsonNode.Parse(result.ToJsonString()) is JsonArray array && array.Count == 3, property + " serializes CultureInfo safely");
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "bad", "fr-FR")) && en.Text == "original", property + " missing language does not fall back");
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "bad", "invalid-culture")), property + " invalid culture rejected");
                foreach (var language in order)
                {
                    UnifiedMultilingualText.Write(control, property, language, language);
                    UnifiedMultilingualText.Write(control, property, language, language);
                    check(texts.Items.Find(x => x.Language.Culture.Name == language)!.Text == "<body><p>" + language + "</p></body>", property + " each language and idempotence");
                }
                en.Text = "original";
                UnifiedMultilingualText.Write(control, property, "", "zh-CN");
                check(zh.Text == "" && en.Text == "original", property + " clear only target");
                check(UnifiedMultilingualText.Read(texts).ToJsonString().Contains("\"text\":\"\""), property + " empty survives serialization");
                UnifiedMultilingualText.Write(control, property, "  ", "zh-CN");
                check(zh.Text == "<body><p>  </p></body>", property + " whitespace preserved");
                UnifiedMultilingualText.Write(control, property, "<body><p></p></body>", "zh-CN");
                check(zh.Text == "<body><p></p></body>", property + " empty HTML preserved");
                UnifiedMultilingualText.Write(control, property, "<body><p>HTML</p></body>", "zh-CN");
                check(zh.Text == "<body><p>HTML</p></body>", property + " preserve HTML");
                zh.IgnoreWrite = true;
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "lost", "zh-CN")), property + " ignored write fails readback");
                zh.IgnoreWrite = false;
                zh.OnWrite = () => en.Text = "corrupted";
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "side effect", "zh-CN")), property + " non-target mutation detected");
                zh.OnWrite = null;
                texts.Items.Add(Row("zh-CN"));
                var previous = zh.Text;
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "ambiguous", "zh-CN")) && previous == zh.Text, property + " duplicate fails before mutation");
                check(Fails(() => UnifiedMultilingualText.Read(texts)), property + " duplicate readback rejected");
                texts.Items.Clear();
                check(Fails(() => UnifiedMultilingualText.Write(control, property, "empty", "zh-CN")), property + " empty collection rejected");
            }
            check(!UnifiedMultilingualText.ReadRequest(new JsonObject(), out _), "omitted text is no-op");
            check(UnifiedMultilingualText.ReadRequest(new JsonObject { ["text"] = "" }, out var empty) && empty == "", "explicit empty is a write");
            check(Fails(() => UnifiedMultilingualText.ReadRequest(new JsonObject { ["text"] = null }, out _)), "null rejected");
            check(Fails(() => UnifiedMultilingualText.ReadRequest(new JsonObject { ["text"] = 1 }, out _)), "non-string rejected");
            var malformed = new MultilingualText(); malformed.Items.Add(new TextItem { Language = null! });
            check(Fails(() => UnifiedMultilingualText.Read(malformed)), "missing Language.Culture.Name rejected");
            var failed = ToolBridgeStatus.Create(true, new JsonObject { ["success"] = false });
            check(failed["bridgeSuccess"]!.GetValue<bool>() && !failed["operationSuccess"]!.GetValue<bool>() && !failed["success"]!.GetValue<bool>(), "bridge success must not hide business failure");
            check(ToolBridgeStatus.Create(true, new JsonObject { ["success"] = true })["success"]!.GetValue<bool>(), "business success propagated");
            check(ToolBridgeStatus.Create(true)["success"] == null, "unknown business result is not success");
            check(!ToolBridgeStatus.Create(false)["success"]!.GetValue<bool>(), "bridge failure is failure");
            foreach (var success in new[] { true, false })
            {
                var direct = McpServer.ProbeResult(success);
                var bridged = McpServer.CallTool("ProbeResult", new JsonObject { ["success"] = success }.ToJsonString());
                check(direct.Meta!["success"]!.GetValue<bool>() == success && bridged.Meta!["operationSuccess"]!.GetValue<bool>() == success, "direct/CallTool business result parity");
                check(JsonNode.Parse(bridged.Message!)!["Meta"]!["success"]!.GetValue<bool>() == success, "bridge retains payload");
            }
            foreach (var request in new[] { ("ProbeResult", "{}"), ("ProbeResult", "[]"), ("ProbeResult", "invalid"), ("ProbeResult", "{\"success\":\"bad\"}"), ("ProbeThrow", "{}"), ("ProbeCycle", "{}"), ("unknown", "{}") })
            {
                var response = McpServer.CallTool(request.Item1, request.Item2);
                check(!response.Meta!["bridgeSuccess"]!.GetValue<bool>() && !response.Meta["success"]!.GetValue<bool>(), "CallTool parameter/reflection/serialization failure: " + request);
            }
        }
    }
}
