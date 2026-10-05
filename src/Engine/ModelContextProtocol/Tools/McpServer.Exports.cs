using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // ───────────────────────────────────────────────────────────────────────────
    //  大响应寄存的接线层 —— 判定与切片在零依赖的 ExportStore.cs（那份能单测）。
    //
    //  为什么包在注册处而不是往每个工具里加限长：
    //  一个个加，漏掉一个就是一次几万字符灌进上下文，而且新增工具必然会忘。包在
    //  注册处是**结构性**的 —— 工具进不了工具表就到不了模型手里，进了就必然过这
    //  一层。
    //
    //  保守原则：结果的形状只要不是「恰好一个文本块」，就**原样放行**不做任何处理。
    //  宁可漏掉一次瘦身，也不能把一个本来能用的响应改坏。
    //
    //  StructuredContent 必须一起换掉。只换文本块的话，带结构化输出的宿主拿到的
    //  仍是整份原文，上下文一点没省 —— 而你看着响应里的 truncated=true 会以为省了。
    //
    // ResponseMessage 使用 Message + Meta。导出工具失败时抛 McpException
    // （见 McpServer.Blocks.cs），成功才正常返回 ResponseMessage。
    // ───────────────────────────────────────────────────────────────────────────
    public static partial class McpServer
    {
        /// <summary>超过这个字符数就寄存并只回头部。0 或负数表示不限。</summary>
        public const int DefaultMaxResponseChars = 20000;

        // 分页工具自身不能被这一层处理：它们的输出本来就是按 offset 夹紧过的，
        // 再包一层只会套娃出一个永远翻不到底的句柄。
        //
        // 比较器必须是 OrdinalIgnoreCase，不能是 Ordinal：CallTool 的工具名映射建在
        // OrdinalIgnoreCase 上（McpServer.ToolBridge.cs 的 AllToolMethods），所以
        // CallTool(name="getexport") 会**成功派发**到 GetExport；这里若按大小写敏感匹配
        // 就漏判，正好给分页结果再套一层句柄。
        private static readonly HashSet<string> ExportToolNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports"
            };

        /// <summary>是不是分页工具自身。</summary>
        internal static bool IsExportTool(string? name) =>
            name != null && ExportToolNames.Contains(name);

        /// <summary>解析本次会话的阈值。TIA_MCP_MAX_RESPONSE_CHARS 覆盖默认值；
        /// 写不成数字就用默认值 —— 一个手滑的环境变量不该把限长悄悄关掉。</summary>
        public static int ResolvedMaxResponseChars()
        {
            var raw = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var v)) return v;
            return DefaultMaxResponseChars;
        }

        /// <summary>给每个工具包上大响应寄存层。签名固定：Program.cs / WrapTools 按这个形状调。</summary>
        public static IList<McpServerTool> WrapWithResponseGuard(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            var outList = new List<McpServerTool>(tools.Count);
            foreach (var t in tools)
            {
                if (t == null) continue;
                var name = t.ProtocolTool?.Name;
                outList.Add(name != null && ExportToolNames.Contains(name)
                    ? t
                    : new ResponseGuardTool(t));
            }
            return outList;
        }


    }

    internal sealed class ResponseGuardTool : McpServerTool
    {
        private readonly McpServerTool _inner;

        public ResponseGuardTool(McpServerTool inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override Tool ProtocolTool => _inner.ProtocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                string name = ProtocolTool?.Name ?? "(unknown)";
                string? forwarded = ForwardedToolName(name, request?.Params?.Arguments);
                // CallTool 转发到分页工具时同样要放行 —— 否则 GetExport 的那一页
                // 会被再寄存一次，模型拿到的是「一个句柄的句柄」，越翻越远。
                if (forwarded != null && McpServer.IsExportTool(forwarded)) return result;

                string target = DescribeTarget(request?.Params?.Arguments);
                if (forwarded != null) target = ("→" + forwarded + " " + target).Trim();
                return Shrink(result, forwarded ?? name, target);
            }
            catch /* swallow(fail-open-guard): failure to store or shrink a large response must preserve the original tool result */
            {
                // 瘦身失败绝不能吃掉本来正常的结果。
                return result;
            }
        }

        /// <summary>调用目标的一句话简述，只为 ListExports 里能认出「这是哪次调用留下的」。
        /// 闭源线里这个helper长在审计层上，本线没有审计层，就地放一份 —— 只有这里用得着，
        /// 挂到 McpServer 上反而会跟别的回流文件撞名。</summary>
        internal static string DescribeTarget(IReadOnlyDictionary<string, JsonElement>? args)
        {
            if (args == null || args.Count == 0) return "";
            string[] keys = {
                "blockPath", "blockName", "typeName", "path", "softwarePath",
                "softwareName", "deviceName", "tagTableName", "watchTableName", "screenName",
            };
            var parts = new List<string>();
            foreach (var k in keys)
            {
                if (!args.TryGetValue(k, out var v)) continue;
                string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(k + "=" + s);
                if (parts.Count >= 3) break;
            }
            return string.Join(" ", parts);
        }

        /// <summary>寄存的是工具响应的**完整文本**，而这个引擎的每个工具都返回
        /// <c>ResponseMessage{Message, Meta}</c>，所以文本几乎总是一层 JSON 信封。
        /// 落盘时把信封剥掉，写里面的 message —— 落盘的意义就是给人用：
        /// 不剥的话「把截断的表存成 IO.csv」得到的是 <c>{"message":"..."}</c>，
        /// Excel 打开一片乱码，而模型正是照工具描述这么干的。
        ///
        /// 只在**确实是这个形状**时才剥（顶层 JSON 对象 + 字符串 message），
        /// 其它一律原样写 —— 猜错了把内容改坏，比多一层信封糟得多。</summary>
        internal static string UnwrapPayload(string content, out bool unwrapped)
        {
            unwrapped = false;
            if (string.IsNullOrEmpty(content)) return content;
            var t = content.TrimStart();
            if (t.Length == 0 || t[0] != '{') return content;
            try
            {
                var node = JsonNode.Parse(content);
                if (node is not JsonObject obj) return content;
                if (!obj.TryGetPropertyValue("message", out var msg)) return content;
                if (msg is not JsonValue v || !v.TryGetValue<string>(out var s)) return content;
                unwrapped = true;
                return s ?? "";
            }
            catch /* swallow(parse-fallback): content that cannot be parsed as a response envelope is returned unchanged */
            {
                return content;   // 解不出来就当它不是信封
            }
        }

        /// <summary>CallTool 转发的目标工具名；本次调用不是转发则返回 null。</summary>
        internal static string? ForwardedToolName(
            string toolName, IReadOnlyDictionary<string, JsonElement>? args)
        {
            if (!string.Equals(toolName, "CallTool", StringComparison.Ordinal)) return null;
            if (args == null || !args.TryGetValue("name", out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }

        /// <summary>超阈值就寄存并只回头部；其余情况原样返回同一个对象。</summary>
        internal static CallToolResult Shrink(CallToolResult? result, string toolName, string target)
        {
            if (result == null) return result!;

            // 错误结果不动：它们本来就短，而且是模型自我纠正最需要看全的东西。
            if (result.IsError ?? false) return result;

            int limit = McpServer.ResolvedMaxResponseChars();
            if (limit <= 0) return result;

            // 只处理「恰好一个文本块」这一种形状。多块、图片、资源链接一律放行 ——
            // 看不懂的形状去改它，改坏的概率比省下来的上下文值钱。
            var blocks = result.Content;
            if (blocks == null || blocks.Count != 1) return result;
            if (!(blocks[0] is TextContentBlock text)) return result;

            string full = text.Text ?? "";
            // 反向哨兵：没超阈值就在这里原样返回**同一个对象引用**，
            // 未超阈值的响应因此一字不变（含 StructuredContent、Meta、块类型）。
            if (full.Length <= limit) return result;

            // 原子：寄存 + 取头部在同一把锁里完成。分成两步的话，中间别的线程 Put 时
            // 的淘汰可能把我们刚存的挤掉，Slice 就拿到 Error != null、Text=""，
            // 于是模型收到一份「成功但空」的响应 —— 原文既不在上下文也不在句柄里。
            var (id, head) = ExportStore.PutAndSlice(toolName, target, full, limit);
            // fail-open：头部都取不到就原样放行完整响应。多花上下文可以接受，丢内容不行。
            if (head.Error != null) return result;

            var meta = new JsonObject
            {
                ["truncated"] = true,
                ["exportId"] = id,
                ["offset"] = 0,
                ["returned"] = head.Returned,
                ["totalLength"] = head.TotalLength,
                ["nextOffset"] = head.NextOffset.HasValue ? JsonValue.Create(head.NextOffset.Value) : null,
                ["eof"] = head.Eof,
                ["hint"] = $"这是 {toolName} 响应的前 {head.Returned} 个字符，共 {head.TotalLength} 个。"
                         + $"**你自己要读全文**：GetExport(exportId=\"{id}\", offset={head.NextOffset}) 往后翻，"
                         + "直到 eof=true；每页是**字符切片**，会从行或 JSON 中间断开，"
                         + "要解析必须先把所有页拼完整再解析，别拿单页去 parse。"
                         + $"**用户要的是文件**：SaveExport(exportId=\"{id}\", outputPath=...) 一次落盘"
                         + "（它只回路径，不回内容，所以你自己要看的话别用它）。"
            };

            var stub = new JsonObject
            {
                ["message"] = head.Text,
                ["meta"] = meta.DeepClone()
            };

            return new CallToolResult
            {
                IsError = false,
                // 结构化输出也必须换掉，否则宿主照样把整份原文发给模型。
                StructuredContent = stub,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = stub.ToJsonString() }
                }
            };
        }
    }
}
