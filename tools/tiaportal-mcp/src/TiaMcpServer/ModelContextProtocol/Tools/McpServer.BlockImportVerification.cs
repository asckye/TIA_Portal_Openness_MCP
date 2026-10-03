using ModelContextProtocol;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 导入后读回校验的三种结局。公开线的 ResponseMessage 只有 Message + Meta，
    /// 没有三态 Outcome 契约，所以三态在这一层表达，由调用方翻译成两态：
    ///   Mismatch → 计入 failed[]（真失败，不能当成功返回）
    ///   Unknown  → 正常返回，但 Message 以 "⚠ 未验证：" 开头 + Meta["verified"]=false
    /// 绝不能把 Unknown 折叠成 Verified —— "没验成" 冒充 "验过了" 比报错更糟。
    /// </summary>
    internal enum PlcBlockVerificationState
    {
        Verified,
        Mismatch,
        Unknown
    }

    internal sealed class PlcBlockVerificationOutcome
    {
        public PlcBlockVerificationOutcome(PlcBlockVerificationState state, string detail)
        {
            State = state;
            Detail = detail;
        }

        public PlcBlockVerificationState State { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// 导入前后用来比对的块属性快照。
    /// PriorityNumber：SimaticML 的 OB 导出里根本不带这一项，读回侧也常常不暴露，
    /// 所以它读不到属于 "unavailable"，不是 "不相等"。
    /// </summary>
    internal sealed class PlcBlockAttributeSnapshot
    {
        public string Name { get; set; } = "";
        public int? Number { get; set; }

        /// <summary>OB 的类型（ProgramCycle / Startup / CyclicInterrupt …）。非 OB 块为 null。</summary>
        public string? SecondaryType { get; set; }
        public int? PriorityNumber { get; set; }
        public string? BlockKind { get; set; }
    }

}
