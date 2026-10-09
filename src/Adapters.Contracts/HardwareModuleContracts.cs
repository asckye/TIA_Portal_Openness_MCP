using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
        public sealed class HardwarePlugLocation
        {
            public int PositionNumber { get; set; }
            public string Label { get; set; } = "";
        }

        /// <summary>一个已经插着东西的槽位。用来把「槽位被占」和「槽位不存在」分开。</summary>
        public sealed class HardwarePluggedItem
        {
            public string Name { get; set; } = "";
            public int PositionNumber { get; set; }
            public bool IsPlugged { get; set; }
            public bool IsBuiltIn { get; set; }
            public string TypeIdentifier { get; set; } = "";
        }

        /// <summary>插入子模块的结果。失败时 <see cref="Reason"/> 给出**可判定的失败类别**，不是一句「插入失败」。</summary>
        public sealed class HardwarePlugResult : IWorkerOperationReply
        {
            public bool RequiresSessionReset { get; set; }
            public bool MayHaveChanged { get; set; }
            public bool BlockReadsAfterUncertain => true;
            /// <summary>整体成功（dryRun 时表示「预检通过、可以插」）。</summary>
            public bool Ok { get; set; }

            /// <summary>
            /// 失败类别，取值：NotConnected / DeviceItemNotFound / InvalidParams /
            /// SlotOccupied / SlotNotAvailable / OrderNumberNotFound / NotSupportedByDevice /
            /// PlugFailed / VerifyFailed。成功时为 null。
            /// </summary>
            public string? Reason { get; set; }

            public string Message { get; set; } = "";

            /// <summary>最终被接受的 TypeIdentifier（试出来的那个变体），失败时可能为 null。</summary>
            public string? TypeIdentifier { get; set; }

            /// <summary>最终落位的槽位号。positionNumber 传 -1 时这里是自动选中的那个。</summary>
            public int? PositionNumber { get; set; }

            /// <summary>插入后**读回**的模块信息。dryRun 或失败时为 null。</summary>
            public HardwarePluggedItem? Plugged { get; set; }

            /// <summary>插入后读回的 I/O 地址（读到什么就报什么，不换算）。</summary>
            public IReadOnlyList<IoAddressInfo>? Addresses { get; set; }

            /// <summary>预检时该宿主上的空闲槽位，帮调用方直接改参数重试。</summary>
            public IReadOnlyList<HardwarePlugLocation>? FreeSlots { get; set; }

            /// <summary>已被占用的槽位。</summary>
            public IReadOnlyList<HardwarePluggedItem>? OccupiedSlots { get; set; }

            /// <summary>实际试过的 TypeIdentifier 变体和结论，失败时排障全靠它。</summary>
            public List<string> Attempts { get; set; } = new List<string>();
        }

}
