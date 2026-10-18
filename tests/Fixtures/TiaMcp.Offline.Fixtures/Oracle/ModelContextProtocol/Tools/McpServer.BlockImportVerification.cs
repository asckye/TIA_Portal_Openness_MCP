using ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // A confirmed mismatch is distinct from an unavailable readback.
    internal sealed class PlcBlockVerificationException : McpException
    {
        internal PlcBlockVerificationException(string message) : base(message, McpErrorCode.InternalError) { }
    }

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
