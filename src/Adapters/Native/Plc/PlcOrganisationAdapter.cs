using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;


namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private readonly IPlcOrganisationSession _session;
        internal bool MutationStarted { get; private set; }
        private readonly object _blockGroupDeleteGate = new object();
        private const int TagPreviewLimit = 200;
        public PlcOrganisationAdapter(IPlcOrganisationSession session) => _session = session;
        private bool IsProjectNull() => _session.IsProjectNull();
        private static object? TryGetPropertyValue(object? target, params string[] names)
        {
            if (target == null) return null;
            foreach (var name in names)
            {
                try { var property = target.GetType().GetProperty(name); if (property != null) return property.GetValue(target); }
                catch { /* swallow(probe-optional): optional tag metadata remains unavailable */ }
            }
            return null;
        }

        public Dictionary<string, object?> DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            EmptyPlcGroupPolicy.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal==null) throw new PlcSoftwareException("InvalidState", "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                // Destructive operations use the shared exact container resolver, never fuzzy PLC selection.
                var sc=_session.ResolveSoftwareContainerUncached(softwarePath);
                var plc=PlcOrganisationPrimitives.SoftwareOrNull(sc) as PlcSoftware ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                PlcBlockUserGroup? Find(string[] parts)
                {
                    PlcBlockGroup current=PlcOrganisationPrimitives.BlockGroup(plc);
                    foreach(var name in parts)
                    {
                        var matches=PlcOrganisationPrimitives.Groups(current).Where(g=>string.Equals(PlcOrganisationPrimitives.Name(g),name,StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                        if(matches.Count==0) return null;
                        if(matches.Count!=1) throw new PlcSoftwareException("InvalidParams","Ambiguous user group: " + name);
                        current=matches[0];
                    }
                    return current as PlcBlockUserGroup ?? throw new PlcSoftwareException("InvalidParams","Only user block groups can be deleted.");
                }
                var result=EmptyPlcGroupPolicy.Execute(groupPath,dryRun,Find,g=>PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Blocks(g)),g=>PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(g)),
                    ()=>PlcOrganisationPrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath,plc))?.ToString() ?? "Unknown",
                    g=> { MutationStarted = true; PlcOrganisationPrimitives.Delete(g); });
                result["softwarePath"]=softwarePath;
                result["resolvedSoftwareName"]=PlcOrganisationPrimitives.Name(plc);
                result["success"]=true;
                return result;
            }
        }

        public Dictionary<string, object?> DeletePlcBlock(string softwarePath, string blockPath, bool dryRun, bool crossReferences = false)
        {
            // 参数校验放在连接检查之前：它不需要工程，放后面就只有连着 TIA 时才走得到，
            // 等于离线永远测不到这条兜底（那样它是不是死代码根本无从判断）。
            if (string.IsNullOrWhiteSpace(blockPath))
            {
                throw new PlcSoftwareException("InvalidParams", "DeletePlcBlock: blockPath is empty");
            }

            var leaf = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;
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
            var refs = crossReferences ? _session.GetCrossReferences(softwarePath, resolvedPath, "Block", "AllObjects", out crossRefReason, out crossRefQueried) : null;
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
                    + "and prefer offline analysis of exported documents. Native diagnostics require the explicit server-process opt-in; compilation cannot guarantee stability.");
                else warnings.Add("Block cross references are unavailable (" + (crossRefReason ?? "unknown reason") + "); this does not establish that the block has no callers. "
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

        public Dictionary<string, object?> DeletePlcTagTable(string softwarePath, string tagTableName, bool dryRun, bool crossReferences = false)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(tagTableName))
            {
                throw new PlcSoftwareException("InvalidParams", "DeletePlcTagTable: tagTableName is empty");
            }

            // 变量表名允许带 '/' 做组分隔，所以只校验叶子名里的元字符。
            var tableLeaf = tagTableName.Replace('\\', '/').Trim('/');
            tableLeaf = tableLeaf.Contains("/") ? tableLeaf.Substring(tableLeaf.LastIndexOf("/") + 1) : tableLeaf;
            if (tableLeaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                // 变量表的定位走字面比对，本来不会把名字当模式；但删除口的规矩三处一致比
                // 「这一处恰好安全」更重要 —— 以后谁把定位换成模式匹配，这道闸仍然在。
                throw new PlcSoftwareException("InvalidParams",
                    "DeletePlcTagTable requires one exact tag table name or group-qualified path; "
                    + "regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PlcSoftwareException("InvalidState",
                    "DeletePlcTagTable: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            var plc = _session.ResolvePlc(softwarePath, dryRun ? false : true);
            if (plc == null)
            {
                throw new PlcSoftwareException("NotFound",
                    $"DeletePlcTagTable: PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            var group = _session.ResolvePlcTagTableGroup(plc);
            if (group == null)
            {
                throw new PlcSoftwareException("NotFound",
                    $"DeletePlcTagTable: tag table group not found on '{softwarePath}' (plcType={plc.GetType().FullName})");
            }

            var wanted = tagTableName.Replace('\\', '/').Trim('/');
            var table = FindTagTableWithPath(group, string.Empty, wanted,
                new HashSet<object>(_session.ReferenceEqualityComparer), out var resolvedPath);
            if (table == null)
            {
                // 打错名字和「表确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = _session.GetPlcTagTables(softwarePath, out _) ?? new List<string>();
                throw new PlcSoftwareException("NotFound",
                    $"DeletePlcTagTable: no tag table named '{tagTableName}' in '{softwarePath}'" +
                    (known.Count > 0 ? ". Available: " + string.Join(", ", known) : " (this PLC has no tag tables)"),
                    known);
            }

            var name = TryGetPropertyValue(table, "Name")?.ToString() ?? wanted;
            bool isDefault = TryGetPropertyValue(table, "IsDefault") is bool b && b;
            if (isDefault)
            {
                // PLC 必须留一张默认变量表，删掉它 TIA 侧行为未定义。宁可挡住也不试。
                throw new PlcSoftwareException("InvalidParams",
                    $"DeletePlcTagTable: '{resolvedPath}' is the DEFAULT tag table (IsDefault=true) and is refused. " +
                    "A PLC always needs one default table. To empty it, delete its tags individually in TIA.");
            }

            var tags = CollectTagSummary(table, out int tagCount);
            int userConstants = CountOf(TryGetPropertyValue(table, "UserConstants"));
            int systemConstants = CountOf(TryGetPropertyValue(table, "SystemConstants"));

            var warnings = new List<object?>
            {
                "Deletion removes all " + tagCount + " tag symbols from this table."
                + "HMI binds PLC tags by symbolic name. PLC compilation may report no error; the broken binding may only become visible on the screen.",
                "If the deleted tags are used by absolute address (%I/%Q/%M), the program can still compile, "
                + "but all comments and symbols are lost and cannot be restored afterwards."
            };

            var result = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["requestedTagTableName"] = tagTableName,
                ["resolvedTagTablePath"] = resolvedPath,
                ["tagTableName"] = name,
                ["isDefaultTable"] = isDefault,
                ["tagCount"] = tagCount,
                ["tags"] = tags,
                ["userConstantCount"] = userConstants,
                ["systemConstantCount"] = systemConstants,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            // 交叉引用仅在 crossReferences=true 时查，保留上方 TIA V21 真机退出证据对应的显式启用约束。
            string? crossRefReason = crossReferences ? _session.CrossReferenceRefusal(softwarePath) : "not queried (crossReferences=false)";
            bool crossRefQueried = false;
            var refs = crossReferences && crossRefReason == null ? TryGetTagTableCrossReferences(table, resolvedPath, out crossRefReason, out crossRefQueried) : null;
            result["crossReferenceQueried"] = crossRefQueried;
            result["crossReferenceAvailable"] = refs != null;
            result["crossReferenceUnavailableReason"] = crossRefReason;
            if (refs != null)
            {
                result["crossReferences"] = ToCrossReferenceArray(refs);
                result["crossReferenceCount"] = refs.Count;
            }
            else
            {
                result["crossReferences"] = null;
                result["crossReferenceCount"] = null;
                if (!crossReferences)
                warnings.Add("Cross references were not queried (crossReferences=false, the default). CrossReferenceService.GetCrossReferences "
                    + "previously terminated TIA Portal V21 during a DeletePlcBlock dry run, so it is no longer automatic. Use SaveProject first "
                    + "and prefer offline analysis of exported documents. Native diagnostics require the explicit server-process opt-in; compilation cannot guarantee stability.");
                else warnings.Add("Table cross references are unavailable; see crossReferenceUnavailableReason. "
                    + "This does not establish that there are no references. Back up with ExportPlcTagTable, "
                    + "then use GetPlcCrossReferences for blocks that may use these symbols, or inspect references manually in TIA.");
            }

            result["warnings"] = warnings;

            if (dryRun) return result;

            // 反射调用 Delete()：本文件里变量表一路都是 object（PlcSoftware / HMI 软件两种形态共用
            // 同一套遍历），保持一致，不为一个调用把整条链改成强类型。
            MutationStarted = table.GetType().GetMethod("Delete", Type.EmptyTypes) != null;
            var invoked = TryInvokeVoidMethod(table, "Delete");
            if (!invoked)
            {
                throw new PlcSoftwareException("OpennessError",
                    $"DeletePlcTagTable: '{resolvedPath}' has no callable Delete() (type={table.GetType().FullName})");
            }

            // Delete() 之后原来的代理对象已死，读回必须从 PlcSoftware 重新取一遍句柄。
            var plc2 = _session.ResolvePlc(softwarePath, dryRun ? false : true);
            var group2 = plc2 == null ? null : _session.ResolvePlcTagTableGroup(plc2);
            bool absent = group2 == null || FindTagTableWithPath(group2, string.Empty, resolvedPath,
                new HashSet<object>(_session.ReferenceEqualityComparer), out _) == null;

            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PlcSoftwareException("OpennessError",
                    $"DeletePlcTagTable: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }

        public Dictionary<string, object?> DeletePlcType(string softwarePath, string typePath, bool dryRun, bool crossReferences = false)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(typePath))
            {
                throw new PlcSoftwareException("InvalidParams", "DeletePlcType: typePath is empty");
            }

            var leaf = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;
            if (leaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                // _session.GetType() 在没有字面同名时会把名字当锚定模式匹配，删除工具绝不能吃这一口。
                throw new PlcSoftwareException("InvalidParams",
                    "DeletePlcType requires one exact type path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PlcSoftwareException("InvalidState",
                    "DeletePlcType: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            PlcType? type = _session.GetType(softwarePath, typePath);
            if (type == null)
            {
                throw new PlcSoftwareException("NotFound",
                    $"DeletePlcType: type '{typePath}' not found in '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            var result = new Dictionary<string, object?>
            {
                ["softwarePath"] = softwarePath,
                ["requestedTypePath"] = typePath,
                ["typeName"] = PlcOrganisationPrimitives.Name(type),
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new List<object?>
            {
                "Deleting the UDT removes the type definition from every DB/block interface that uses it; "
                + "recompile the entire PLC to see the full impact."
            };

            // 仅在 crossReferences=true 时查，保留上方 TIA V21 真机退出证据对应的显式启用约束。
            string? crossRefReason = null;
            bool crossRefQueried = false;
            var refs = crossReferences ? _session.GetCrossReferences(softwarePath, typePath, "Type", "AllObjects", out crossRefReason, out crossRefQueried) : null;
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
                    + "and prefer offline analysis of exported documents. Native diagnostics require the explicit server-process opt-in; compilation cannot guarantee stability.");
                else warnings.Add("⚠️ Cross-references for this UDT could not be retrieved (" + (crossRefReason ?? "unknown reason") + "); this **does not mean** the UDT is unused."
                    + "Back up with ExportPlcType before deletion; compile with CompilePlcSoftware afterwards.");
            }
            result["warnings"] = warnings;

            if (dryRun) return result;

            MutationStarted = true;
            PlcOrganisationPrimitives.Delete(type);
            bool absent = _session.GetType(softwarePath, typePath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PlcSoftwareException("OpennessError",
                    $"DeletePlcType: Delete() returned but '{typePath}' is still present");
            }
            return result;
        }

        private static List<object?> ToCrossReferenceArray(List<PlcCrossReference> refs)
        {
            var arr = new List<object?>();
            foreach (var r in refs)
            {
                arr.Add(new Dictionary<string, object?>
                {
                    ["sourceName"] = r.SourceName,
                    ["sourcePath"] = r.SourcePath,
                    ["referenceName"] = r.ReferenceName,
                    ["referencePath"] = r.ReferencePath,
                    ["referenceType"] = r.ReferenceType,
                    ["access"] = r.Access
                });
            }
            return arr;
        }

        private static object? FindTagTableWithPath(
            object group, string prefix, string wanted, HashSet<object> visited, out string resolvedPath)
        {
            resolvedPath = wanted;
            if (!visited.Add(group)) return null;

            var tables = TryGetPropertyValue(group, "TagTables");
            if (tables is IEnumerable tEnum and not string)
            {
                foreach (var t in tEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    if (name.Length == 0) continue;
                    var qualified = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(qualified, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPath = qualified;
                        return t;
                    }
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    var hit = FindTagTableWithPath(sub, next, wanted, visited, out resolvedPath);
                    if (hit != null) return hit;
                }
            }
            resolvedPath = wanted;
            return null;
        }

        private static List<object?> CollectTagSummary(object table, out int tagCount)
        {
            tagCount = 0;
            var arr = new List<object?>();
            var tags = TryGetPropertyValue(table, "Tags");
            if (tags is not IEnumerable e || tags is string) return arr;

            foreach (var tag in e)
            {
                if (tag == null) continue;
                tagCount++;
                if (arr.Count >= TagPreviewLimit) continue;
                arr.Add(new Dictionary<string, object?>
                {
                    ["name"] = TryGetPropertyValue(tag, "Name")?.ToString(),
                    ["dataType"] = TryGetPropertyValue(tag, "DataTypeName")?.ToString(),
                    ["address"] = TryGetPropertyValue(tag, "LogicalAddress")?.ToString()
                });
            }
            return arr;
        }

        private static int CountOf(object? collection)
        {
            if (collection == null) return 0;
            if (TryGetPropertyValue(collection, "Count") is int n) return n;
            if (collection is IEnumerable e and not string) return e.Cast<object?>().Count(x => x != null);
            return 0;
        }

#if PLC_SOFTWARE_CROSS_REFERENCES
        private List<PlcCrossReference>? TryGetTagTableCrossReferences(
            object table, string resolvedPath, out string? reason, out bool queried)
        {
            queried = false;
            reason = PlcCrossReferencePolicy.PolicyRefusal(Environment.GetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable));
            if (reason != null) return null;
            try
            {
                // The service belongs to tags/constants, not the table itself. Any failed child invalidates the aggregate.
                var typed = table as global::Siemens.Engineering.SW.Tags.PlcTagTable ?? throw new NotSupportedException("Expected PlcTagTable.");
                var items = new List<PlcCrossReference>();
                var targets = PlcGroupOperations.Items(PlcOrganisationPrimitives.Tags(typed)).Concat(PlcGroupOperations.Items(PlcOrganisationPrimitives.SystemConstants(typed)));
                foreach (IEngineeringServiceProvider target in targets)
                {
                    var service = InvocationJournal.Native("TagTable.CrossReference.GetService", () => PlcOrganisationPrimitives.CrossReferences(target));
                    if (service == null) throw new NotSupportedException("CrossReferenceService missing on a tag/system constant; table result incomplete.");
                    queried = true;
                    var raw = InvocationJournal.Native("TagTable.CrossReference.query", () => PlcOrganisationPrimitives.CrossReferences(service, global::Siemens.Engineering.CrossReference.CrossReferenceFilter.AllObjects));
                    items.AddRange(InvocationJournal.Native("TagTable.CrossReference.read", () => _session.TryFlattenCrossReferenceResult(raw, resolvedPath)));
                }
                reason = null;
                return items;
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex))
            { reason = "Tag table cross-reference result incomplete; partial rows discarded: " + ex.GetBaseException().Message; return null; }
        }

#else
        private List<PlcCrossReference>? TryGetTagTableCrossReferences(object table, string path, out string? reason, out bool queried)
        { queried = false; reason = "CrossReferenceService is unavailable before V18; no native query was made."; return null; }
#endif

        private static bool TryInvokeVoidMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(target, Array.Empty<object>());
            return true;
        }

#if PLC_SOFTWARE_PROTECTION
        public HardwareAddressingReply ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "", bool confirmProtectionChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcBlockProtection", meta => {
                bool writing = PlcProtectionPolicy.ValidateProtectionRequest(action, password, confirmProtectionChange, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                _session.ExactPlcForEngineering(softwarePath, writing);
                var block = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock ?? throw new ArgumentException("blockPath must identify a block.");
                var provider = PlcOrganisationPrimitives.Protection(block) ?? throw new NotSupportedException("PlcBlockProtectionProvider unavailable on this block/version.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(block), ["isKnowHowProtected"] = PlcOrganisationPrimitives.IsKnowHowProtected(block), ["programmingLanguage"] = PlcOrganisationPrimitives.Language(block).ToString() };
                var invalid = PlcOrganisationPrimitives.InvalidPasswordCharacters(provider)?.ToArray() ?? Array.Empty<char>();
                meta["invalidPasswordCharacters"] = new List<object?>(invalid.Select(c => (object?)PlcSoftwareValues.Value(c.ToString())!).ToArray());
                if (action == "read") return "Block know-how protection state read.";
                bool expected = action == "protect";
                if (PlcOrganisationPrimitives.IsKnowHowProtected(block) == expected) throw new InvalidOperationException(expected ? "Block is already know-how protected; unprotect first." : "Block is not know-how protected.");
                if (expected && PlcProtectionPolicy.ContainsInvalidPasswordCharacter(password, invalid)) throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Block protection preview; no changes.";
                meta["mayHaveChanged"] = true;
                using (var secure = PlcProtectionPolicy.ToSecureString(password))
                {
                    if (expected) PlcOrganisationPrimitives.Protect(provider, secure); else PlcOrganisationPrimitives.Unprotect(provider, secure);
                }
                meta["apiCallSuccess"] = true;
                meta["after"] = new Dictionary<string, object?> { ["isKnowHowProtected"] = PlcOrganisationPrimitives.IsKnowHowProtected(block) };
                if (PlcOrganisationPrimitives.IsKnowHowProtected(block) != expected) throw new InvalidOperationException("Protection readback differs from the requested state.");
                return "Block know-how protection changed and verified by readback; no save/compile/download.";
            });

#else
        public HardwareAddressingReply ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "", bool confirmProtectionChange = false, bool dryRun = true)
            => throw new NotSupportedException("PlcBlockProtectionProvider requires V15.1 or later.");
#endif

        public Dictionary<string, object?> CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            PlcGroupCreation.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal == null)
                throw new PlcSoftwareException("InvalidState", "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcOrganisationPrimitives.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                var result = PlcGroupCreation.Execute<PlcTypeGroup>(PlcOrganisationPrimitives.TypeGroup(plc), groupPath, dryRun,
                    g => PlcOrganisationPrimitives.Groups(g), g => PlcOrganisationPrimitives.Name(g), (g, name) => { MutationStarted = true; return PlcOrganisationPrimitives.Create(PlcOrganisationPrimitives.Groups(g), name); });
                result["softwarePath"] = softwarePath;
                result["resolvedSoftwareName"] = PlcOrganisationPrimitives.Name(plc);
                return result;
            }
        }

        public HardwareAddressingReply ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcUserGroup", meta => {
                var shape = family switch {
                    "blocks" => ("BlockGroup", "Blocks"), "types" => ("TypeGroup", "Types"),
                    "tags" => ("TagTableGroup", "TagTables"), "technology" => ("TechnologicalObjectGroup", "TechnologicalObjects"),
                    "watchTables" => ("WatchAndForceTableGroup", "WatchTables"), "externalSources" => ("ExternalSourceGroup", "ExternalSources"),
                    _ => throw new ArgumentException("family must be blocks, types, tags, technology, watchTables or externalSources.") };
                PlcGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcOrganisationPrimitives.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                meta["softwarePath"] = softwarePath; meta["resolvedSoftwareName"] = PlcOrganisationPrimitives.Name(plc);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun && PlcOrganisationPrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString() != "Offline")
                    throw new PlcSoftwareException("InvalidState", "Confirmed Offline state is required for group editing.");
                if (!dryRun) meta["mayHaveChanged"] = true;
                meta["result"] = PlcGroupOperations.Manage(PlcGroupOperations.Get(plc, shape.Item1), groupPath, action, newName, dryRun, shape.Item2);
                if (action != "deleteEmpty" || dryRun) { try { meta["group"] = UserGroupRow(PlcGroupOperations.Group(PlcGroupOperations.Get(plc, shape.Item1), action == "rename" && !dryRun ? string.Join("/", PlcGroupOperations.Parts(groupPath).Take(PlcGroupOperations.Parts(groupPath).Length - 1).Append(newName)) : groupPath)); } catch (Exception ex) { meta["groupError"] = ex.GetBaseException().Message; } }
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });

        private static Dictionary<string, object?> UserGroupRow(object group) => group switch
        {
            global::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup b => new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(b), ["groupClass"] = b.GetType().Name, ["blocks"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Blocks(b)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(b)) },
            global::Siemens.Engineering.SW.Types.PlcTypeUserGroup ty => new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(ty), ["groupClass"] = ty.GetType().Name, ["types"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Types(ty)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(ty)) },
            global::Siemens.Engineering.SW.Tags.PlcTagTableUserGroup tg => new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(tg), ["groupClass"] = tg.GetType().Name, ["tagTables"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.TagTables(tg)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(tg)) },
#if PLC_SOFTWARE_PROTECTION
            global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup w => new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(w), ["groupClass"] = w.GetType().Name, ["watchTables"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.WatchTables(w)), ["forceTables"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.ForceTables(w)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(w)) },
#endif
            global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup e => new Dictionary<string, object?> { ["name"] = PlcOrganisationPrimitives.Name(e), ["groupClass"] = e.GetType().Name, ["externalSources"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.ExternalSources(e)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(e)) },
            _ => new Dictionary<string, object?> { ["name"] = PlcGroupOperations.Get(group, "Name").ToString(), ["groupClass"] = group.GetType().Name }
        };

        public PlcBlockGroup? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>();
            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcOrganisationPrimitives.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcOrganisationPrimitives.BlockGroup(plcSoftware) == null)
            {
                return null;
            }

            var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            PlcBlockGroup currentGroup = PlcOrganisationPrimitives.BlockGroup(plcSoftware);
            foreach (var groupName in groupNames)
            {
                var next = PlcOrganisationPrimitives.Groups(currentGroup).FirstOrDefault(g => PlcOrganisationPrimitives.Name(g).Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    MutationStarted = true;
                    next = PlcOrganisationPrimitives.Create(PlcOrganisationPrimitives.Groups(currentGroup), groupName);
                    created.Add(groupName);
                    _session.GroupCreated(groupName);
                }
                currentGroup = next;
            }
            return currentGroup;
        }

        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
        {
            if (IsProjectNull())
            {
                throw new PlcSoftwareException("InvalidState", "No project is open in TIA Portal");
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcOrganisationPrimitives.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcOrganisationPrimitives.BlockGroup(plcSoftware) == null)
            {
                throw new PlcSoftwareException("NotFound", $"PlcSoftware not found at '{softwarePath}'");
            }

            // 1) find the block anywhere by exact name
            var all = new List<PlcBlock>();
            _session.GetBlocksRecursive(PlcOrganisationPrimitives.BlockGroup(plcSoftware), all);
            var block = all.FirstOrDefault(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null)
            {
                throw new PlcSoftwareException("NotFound", $"Block '{blockName}' not found in '{softwarePath}'");
            }

            // Siemens SIMATIC SD import recreates OBs as cyclic OBs with a new
            // number. Do not delete an original OB through this generic move route.
            if (block is OB)
            {
                var existingGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                if (existingGroup != null && ReferenceEquals(PlcOrganisationPrimitives.Parent(block), existingGroup))
                    return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
                throw new PlcSoftwareException("NotSupportedOnVersion",
                    "Moving organization blocks by export/delete/import is disabled: SIMATIC SD import does not preserve OB type/number. Move this OB in TIA Portal.");
            }

            // 2) ensure the target group exists
            var targetGroup = autoCreateGroup
                ? EnsurePlcBlockGroup(softwarePath, targetGroupPath, out _)
                : _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            if (targetGroup == null)
            {
                throw new PlcSoftwareException("NotFound",
                    $"Target block group '{targetGroupPath}' not found (set autoCreateGroup=true to create it)");
            }

            // already in the target group?
            if (ReferenceEquals(PlcOrganisationPrimitives.Parent(block), targetGroup))
            {
                return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
            }
            if (PlcOrganisationPrimitives.Blocks(targetGroup).Any(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase)))
                throw new PlcSoftwareException("InvalidParams",
                    $"Target group already contains '{blockName}'; relocation never overwrites another block.");

            // 3) export -> delete -> import into target group (no native reparent)
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_move", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string method;
            bool moveVerified = false;
            try
            {
                bool usedDocs;
#if PLC_DOCUMENT_EXPORT
                try
                {
                    var exp = PlcOrganisationPrimitives.ExportDocuments(block, new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempDir)), blockName);
                    usedDocs = exp != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcOrganisationPrimitives.State(exp));
                }
                catch (EngineeringNotSupportedException)
                {
 /* swallow(native-fallback): Unsupported document export falls back to SimaticML before deleting the source block. */                    usedDocs = false; // mixed-language / STL -> fall back to XML
                }

#else
                usedDocs = false;
#endif

#if PLC_DOCUMENT_EXPORT
                if (usedDocs)
                {
                    MutationStarted = true;
            PlcOrganisationPrimitives.Delete(block);
                    var res = PlcOrganisationPrimitives.ImportDocuments(PlcOrganisationPrimitives.Blocks(targetGroup), new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempDir)), blockName, ImportDocumentOptions.Override);
                    if (res == null || !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcOrganisationPrimitives.State(res)))
                    {
                        var evidence = new Dictionary<string, object?>();
                        PlcSoftwareValues.RecordNativeResult(evidence, res?.State, res == null ? null : _session.DocumentMessages(res.Messages));
                        var failure = new PlcSoftwareException("ImportFailed",
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (documents)");
                        failure.Data["nativeResultEvidence"] = evidence;
                        throw failure;
                    }
                    method = "documents(.s7dcl)";
                }
                else
#endif
                {
                    var xml = Path.Combine(tempDir, blockName + ".xml");
                    PlcOrganisationPrimitives.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ExportOptions.None);
                    MutationStarted = true;
            PlcOrganisationPrimitives.Delete(block);
                    var imp = PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks(targetGroup), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ImportOptions.Override);
                    if (imp == null || imp.Count == 0)
                    {
                        throw new PlcSoftwareException("ImportFailed",
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (xml)");
                    }
                    method = "xml(SimaticML)";
                }
                var verifyGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                var verifyBlocks = PlcOrganisationPrimitives.BlocksOrNull(verifyGroup);
                moveVerified = verifyGroup != null && verifyBlocks.Any(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
                if (!moveVerified) throw new PlcSoftwareException("ImportFailed",
                    $"Move of '{blockName}' to '{targetGroupPath}' could not be verified");
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed",
                    $"Move failed: {ex.Message}. Recovery export retained at '{tempDir}'; inspect the source and destination before retrying.", null, ex);
            }
            finally
            {
                // A failed import may have already deleted the source. Never erase
                // its only recovery export during exception cleanup.
                if (moveVerified)
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch {  /* swallow(cleanup): Cleanup after verified relocation must not replace the successful move result. *//* best-effort cleanup */ }
            }

            return $"Moved '{blockName}' to '{targetGroupPath}' via {method}";
        }
    }
}
