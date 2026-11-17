using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        public Dictionary<string, object?> DeletePlcBlock(string softwarePath, string blockPath, bool dryRun, bool crossReferences = false)
        {
            // 参数校验放在连接检查之前：它不需要工程，放后面就只有连着 TIA 时才走得到，
            // 等于离线永远测不到这条兜底（那样它是不是死代码根本无从判断）。
            if (string.IsNullOrWhiteSpace(blockPath))
            {
                throw new PlcSoftwareException("InvalidParams", "DeletePlcBlock: blockPath is empty");
            }

            var leaf = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/", StringComparison.Ordinal) + 1) : blockPath;
            if (leaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                throw new PlcSoftwareException("InvalidParams",
                    "DeletePlcBlock requires one exact block path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PlcSoftwareException("InvalidState",
                    "DeletePlcBlock: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            var block = _session.GetBlock(softwarePath, blockPath);
            if (block == null)
            {
                // 打错名字和「块确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = _session.GetBlocks(softwarePath);
                var names = known?.Select(_session.GetBlockPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                  .Take(50).ToList() ?? new List<string>();
                throw new PlcSoftwareException("NotFound",
                    $"DeletePlcBlock: block '{blockPath}' not found in '{softwarePath}'" +
                    (names.Count > 0
                        ? ". Available (first 50): " + string.Join(", ", names)
                        : " (this PLC has no blocks, or the block list could not be read)") +
                    _session.AvailablePlcPathsSuffix(),
                    names);
            }

            string resolvedPath = _session.GetBlockPath(block);
            string blockType = block.GetType().Name;

            // 🔴 显式块号是**在删除这一刻**丢的，事后再提醒已经晚了：
            // 把 FB 钉成 103（AutoNumber=false）→ 删掉 → 从同一份外部源重建 →
            // 新块拿到自动分配的号，103 一去不返，依赖它的实例 DB 关联随之断裂，
            // 而整个过程**没有任何报错**。所以要在 dryRun 阶段就把号读出来交给工具层去警告。
            // （注意「不删、直接对已有块重新生成」是安全的，编号原样保留。）
            int? pinnedNumber = null;
            try
            {
                if (!PlcOrganisationPrimitives.AutoNumber(block)) pinnedNumber = PlcOrganisationPrimitives.Number(block);
            }
            catch
            {
 /* swallow(probe-optional): 部分块类型不提供 Number/AutoNumber；缺少编号警告不阻止原有删除流程。 */                // 某些块类型不给 Number/AutoNumber。读不到就不警告，但绝不因此让删除失败。
            }

            var result = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["requestedBlockPath"] = blockPath,
                ["resolvedBlockPath"] = resolvedPath,
                ["blockName"] = PlcOrganisationPrimitives.Name(block),
                ["blockType"] = blockType,
                ["pinnedBlockNumber"] = pinnedNumber,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new List<object?>
            {
                "Only this block is deleted; its instance DB and callers remain. "
                + "Use CompilePlcSoftware after deletion to detect dangling references."
            };

            // TIA Portal V21，2026-09-21（docs/reference/real-machine-ledger.md）：DeletePlcBlock 干跑里的
            // CrossReferenceService.GetCrossReferences 让 TIA Portal 整个退出。默认不查，
            // 并明说「没查」，不是「没人用」。
            string? crossRefReason = null;
            bool crossRefQueried = false;
            var refs = crossReferences ? _session
                .GetCrossReferences(softwarePath, resolvedPath, "Block", "AllObjects", out crossRefReason, out crossRefQueried) : null;
            result["crossReferenceQueried"] = crossRefQueried;
            if (crossReferences) result["crossReferenceUnavailableReason"] = crossRefReason;
            result["crossReferenceAvailable"] = refs != null;
            if (refs != null)
            {
                result["crossReferenceCount"] = refs.Count;
                result["crossReferences"] = ToCrossReferenceArray(refs);
            }
            else
            {
                result["crossReferenceCount"] = null;
                result["crossReferences"] = null;
                if (!crossReferences)
                warnings.Add("Cross references were not queried (crossReferences=false, the default). CrossReferenceService.GetCrossReferences "
                    + "previously terminated TIA Portal V21 during a DeletePlcBlock dry run, so it is no longer automatic. Use SaveProject first "
                    + "and prefer offline analysis of exported documents. Native queries are allowed through V20 where supported; V21 requires the human " +
                        "Workbench session switch or server-process opt-in. Compilation cannot guarantee stability.");
                else warnings
                    .Add("Block cross references are unavailable (" + (crossRefReason ?? "unknown reason")
                        + "); this does not establish that the block has no callers. "
                    + "Back up with ExportPlcBlockDocuments before deletion, then run CompilePlcSoftware and inspect errors.");
            }
            result["warnings"] = warnings;

            if (dryRun) return result;

            MutationStarted = true;
            PlcOrganisationPrimitives.Delete(block);

            // Delete() 之后原来的代理对象已死，回读必须从 PlcSoftware 重新解析一遍路径。
            bool absent = _session.GetBlock(softwarePath, resolvedPath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PlcSoftwareException("OpennessError",
                    $"DeletePlcBlock: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }
    }
}
