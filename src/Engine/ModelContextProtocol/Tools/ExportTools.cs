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

using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ExportTools
    {
        // ── 对模型暴露的分页工具 ────────────────────────────────────────────

        [McpServerTool(Name = "GetExport"), Description(
            "[L1][Exports] Read one page of a large response that was parked under an export handle. "
            + "When a tool's response exceeds the size limit the engine stores the full text and returns "
            + "only its head plus an 'exportId'; call this with that id to read the rest. "
            + "Page forward by passing the 'nextOffset' from the previous page until 'eof' is true. "
            + "Each page is a raw CHARACTER SLICE of the original text — it can cut a line or a JSON value "
            + "in half. Concatenate every page first, THEN parse; never parse a single page on its own. "
            + "Handles live for 24 hours and only inside the current engine session.")]
        public ResponseMessage GetExport(
            [Description("exportId: copy the opaque handle exactly from the truncated response; never construct it or reuse it after a worker/server restart.")] string exportId,
            [Description("offset: character index to start at; use the previous page's nextOffset. 0 = beginning")] int offset = 0,
            [Description("length: how many characters to return. 0 or omitted = this session's response limit.")] int length = 0)
        {
            // 默认跟随本会话阈值，别硬编码 20000：把 TIA_MCP_MAX_RESPONSE_CHARS 调小的人，
            // 本意就是每次少给点，硬编码等于把这个配置整个绕过去。
            if (length <= 0)
            {
                int lim = ResolvedMaxResponseChars();
                length = lim > 0 ? lim : ExportStore.MaxSliceChars;
            }
            var slice = ExportStore.Slice(exportId, offset, length);
            if (slice.Error != null)
            {
                // 句柄不存在/已过期/被淘汰 —— 这一页取不到，而且知道为什么。
                // 本线没有三态契约：取不到就抛，让宿主标 IsError，别返回一份空 Message
                // 让模型误以为「这份导出是空的」。
                throw new McpException(
                    slice.Message ?? $"取不到句柄 {exportId}（{slice.Error}）。",
                    McpErrorCode.InvalidParams);
            }

            return new ResponseMessage
            {
                Message = slice.Text,
                Meta = new JsonObject
                {
                    ["ok"] = true,
                    ["exportId"] = slice.Id,
                    ["offset"] = slice.Offset,
                    ["returned"] = slice.Returned,
                    ["totalLength"] = slice.TotalLength,
                    ["nextOffset"] = slice.NextOffset.HasValue ? JsonValue.Create(slice.NextOffset.Value) : null,
                    ["eof"] = slice.Eof
                }
            };
        }

        [McpServerTool(Name = "ListExports"), Description(
            "[L1][Exports] List the export handles currently held by this engine session — id, the tool "
            + "that produced each one, its target, age, and total size. Use it when you have lost an "
            + "exportId, or to check what is still available before paging.")]
        public ResponseMessage ListExports(
            [Description("tool: optional filter, matches part of the producing tool's name")] string? tool = null,
            [Description("limit: maximum handles to return")] int limit = 20)
        {
            var items = ExportStore.List(tool, limit);
            var arr = new JsonArray();
            foreach (var e in items)
            {
                arr.Add(new JsonObject
                {
                    ["exportId"] = e.Id,
                    ["tool"] = e.Tool,
                    ["target"] = e.Target,
                    ["createdUtc"] = e.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss") + "Z",
                    ["totalLength"] = e.Length
                });
            }
            var (count, chars) = ExportStore.Stats();
            // 列举本身不依赖外部资源，走到这里就是列完了；空表也是一个确定的答案，不该报错。
            return new ResponseMessage
            {
                Message = items.Count == 0
                    ? "当前没有寄存的响应。"
                    : $"寄存中 {count} 份，共 {chars} 字符；此处列出 {items.Count} 份。",
                Meta = new JsonObject { ["ok"] = true, ["count"] = count, ["items"] = arr }
            };
        }

        [McpServerTool(Name = "SaveExport"), Description(
            "[L1][Exports] Write a parked response to a file in one step, instead of paging it through "
            + "the conversation. Prefer this whenever the user wants the whole thing (a full cross-reference "
            + "dump, a whole block list, a whole block export): it costs one call and no context. "
            + "By default the tool's PAYLOAD is written (e.g. the CSV itself), not the JSON envelope around it — "
            + "so saving a truncated table to 'IO.csv' really gives you a CSV you can open in Excel. "
            + "Pass raw=true to write the untouched response text instead. "
            + "Written as UTF-8 with BOM so Chinese opens correctly in Notepad and Excel.")]
        public ResponseMessage SaveExport(
            [Description("exportId: the handle from a truncated response")] string exportId,
            [Description("outputPath: full file path to write, e.g. 'C:\\\\Temp\\\\IO表.csv'")] string outputPath,
            [Description("raw: true = write the response text verbatim (JSON envelope included). Default false = write just the payload.")] bool raw = false,
            [Description("overwrite: DEFAULT false — if the file already exists the call is REFUSED rather than replacing it. Pass true only after the user agreed to overwrite that specific file.")] bool overwrite = false)
        {
            var entry = ExportStore.Get(exportId);
            if (entry == null)
            {
                // 没这个句柄，文件一个字都没写。借 Slice 拿到「过期 / 被淘汰 / id 记错了」的准确说法。
                var probe = ExportStore.Slice(exportId, 0, 1);
                throw new McpException(
                    probe.Message ?? $"没有句柄 {exportId}。",
                    McpErrorCode.InvalidParams);
            }
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new McpException("outputPath 不能为空。", McpErrorCode.InvalidParams);
            }

            string full;
            try
            {
                full = Path.GetFullPath(outputPath.Trim());
            }
            catch (Exception ex)
            {
                throw new McpException($"outputPath 不是一个合法路径：{ex.Message}", ex, McpErrorCode.InvalidParams);
            }

            // 不覆盖已存在的文件。这个工具不动 TIA 工程，所以看起来「只读」、门槛低；
            // 如果它能无声覆盖任意路径，那就等于开了个写盘后门 —— 路径写成工程目录里
            // 某个已有文件，一次调用就把它盖了。默认拒绝，要覆盖得明说。
            if (File.Exists(full) && !overwrite)
            {
                // 这是按设计主动不干，但对调用方来说文件没写成，必须报错 ——
                // 报成功会让它以为备份已经存下来了。
                throw new McpException(
                    $"{full} 已存在，未覆盖。换个文件名，或者在用户同意后传 overwrite=true。",
                    McpErrorCode.InvalidParams);
            }

            // 先初始化：raw 分支不走 UnwrapPayload，out 参数不会被赋值。
            bool unwrapped = false;
            string content;
            try
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                content = raw
                    ? entry.Content
                    : ResponseGuardTool.UnwrapPayload(entry.Content, out unwrapped);
                File.WriteAllText(full, content, new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // 写盘抛了：内容没有完整写出去。注意磁盘上可能留了个半截文件，别当它是好的。
                throw new McpException("写文件失败：" + ex.Message, ex, McpErrorCode.InternalError);
            }

            // WriteAllText 返回即文件已落盘，长度是写进去的那份内容的长度。
            return new ResponseMessage
            {
                Message = $"已写入 {full}（{content.Length} 字符，来自 {entry.Tool}）。"
                        + (unwrapped ? "写的是工具正文本身（已剥掉 JSON 信封）；要原文加 raw=true。" : ""),
                Meta = new JsonObject
                {
                    ["ok"] = true,
                    ["exportId"] = entry.Id,
                    ["path"] = full,
                    ["writtenLength"] = content.Length,
                    ["totalLength"] = entry.Length,
                    ["unwrapped"] = unwrapped
                }
            };
        }

        [McpServerTool(Name = "DeleteExport"), Description(
            "[L1][Exports] Drop one export handle once you are done with it. Optional — handles expire on "
            + "their own after 24 hours and the oldest are evicted automatically when the store fills up.")]
        public ResponseMessage DeleteExport(
            [Description("exportId: the handle to drop")] string exportId)
        {
            if (!ExportStore.Delete(exportId))
            {
                // 没找到时分不清是「本来就没有/已过期」还是「id 打错了」——
                // 后一种情况下调用方真正的那个句柄还活着，报成功等于骗它。
                throw new McpException(
                    $"没有句柄 {exportId}（可能已过期或已删除，也可能 id 写错了）。用 ListExports 看当前还有哪些。",
                    McpErrorCode.InvalidParams);
            }
            return new ResponseMessage
            {
                Message = $"已删除 {exportId}。",
                Meta = new JsonObject { ["ok"] = true, ["exportId"] = exportId ?? "" }
            };
        }

        [McpServerTool(Name = "ClearExports"), Description(
            "[L1][Exports] Drop parked responses in bulk. NOTE: handles already expire on their own at 24h, "
            + "so the default olderThanHours=24 almost always deletes nothing — pass olderThanHours=0 to "
            + "actually free the store now.")]
        public ResponseMessage ClearExports(
            [Description("olderThanHours: drop handles at least this old; 0 drops every handle")] int olderThanHours = 24)
        {
            int n = ExportStore.Clear(olderThanHours);
            var (count, chars) = ExportStore.Stats();
            // 24h 那个默认值恒等于空操作（句柄本来就到 24h 自动过期），
            // 不点破的话最自然的一次裸调用永远回「已删除 0 份」，调用方只会以为工具坏了。
            string hint = (n == 0 && olderThanHours >= ExportStore.DefaultTtlHours)
                ? $"（句柄本来就满 {ExportStore.DefaultTtlHours} 小时自动过期，所以这个默认值几乎总是删不掉东西；"
                  + "要立刻清空传 olderThanHours=0。）" : "";
            // 删了几份、还剩几份都是数出来的，纯内存操作，结局是确定的。
            return new ResponseMessage
            {
                Message = $"已删除 {n} 份；剩余 {count} 份、共 {chars} 字符。" + hint,
                Meta = new JsonObject { ["ok"] = true, ["deleted"] = n, ["remaining"] = count }
            };
        }
    }
}
