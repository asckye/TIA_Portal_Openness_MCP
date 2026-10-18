using System;
using System.Collections.Generic;

namespace TiaMcp.Engine.Tests
{
    internal static class S7ResScannerTests
    {
        /// <summary>
        /// .s7res 是 YAML，不是 XML。原实现拿 XML 解析器去读，对**每一个真实文件**都抛异常，
        /// 异常又被外面的 catch 吞掉 —— 于是「没有缺失的 en-US 条目」这个结论，
        /// 是在一次都没真正扫过的情况下得出的。这里用真实形态的行喂它。
        /// </summary>
        internal static void Run(Action<bool, string> Check)
        {
            // 真实形态：MultiLingualTexts 容器 + 「- id: MLC_xxx」列表项 + 各语言行。
            var missing = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    de-DE: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            var ids = TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(missing);
            Check(ids.Contains("MLC_Comment"), "缺 en-US 的条目要被点名（MLC_Comment）");
            Check(!ids.Contains("MLC_Title"), "有 en-US 的条目不许被误报（MLC_Title）");

            // 反向哨兵：全都有 en-US 时必须一条都不报。
            // 少了这条，「永远返回空列表」的坏实现也能通过上面那两条里的第二条。
            var complete = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    en-US: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(complete).Count == 0,
                "[反向哨兵] 全都有 en-US 时不许报缺失");

            // [哨兵] 空输入不许崩：预检坏掉本身就该看得见，但不该把调用方一起带走。
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(new List<string>()).Count == 0,
                "[哨兵] 空输入返回空列表且不抛");
        }
    }
}
