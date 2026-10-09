using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks.Interface;

#if TIA_SHARED_ADAPTER_PATHS
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
#else
using PlcNative = TiaMcpServer.Siemens.LocalPlcBlocks.PlcBlockPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcBlocksService
    {
        private readonly IEngineeringSession _session;

#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.BlockSurface? _plcBlocks;
        private ProjectBase? PlcBlocksProject => (_plcBlocks ?? (_plcBlocks =
            TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).PlcBlocks)).CurrentProject;
#else
        private ProjectBase? PlcBlocksProject => _session.CurrentProject;
#endif

        private bool IsProjectNull()
        {
            if (PlcBlocksProject == null) return true;
            _session.VerifyBinding("Project access");
            return false;
        }

        public PlcBlocksService(IEngineeringSession session) => _session = session;

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _session.Logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = (_session.GetBlocks(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                SoftwareContainerLookup.PathOf(_session.GetSoftwareContainer(softwarePath)), true);
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks", list.Where(x => !PlcNative.IsConsistent(x)).Select(x => PlcNative.Name(x)), "groupPath");

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _session.Logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {PlcNative.Name(block)}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (PlcNative.Parent(block) is PlcBlockGroup parentGroup)
                    {
                        groupPath = _session.GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{PlcNative.Name(block)}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{PlcNative.Name(block)}.xml");
                }

                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{PlcNative.Name(block)}: cannot delete existing file ({ioEx.Message})");
                            _session.Logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        PlcNative.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{PlcNative.Name(block)}: license not found ({licEx.Message})");
                        _session.Logger?.LogError(licEx, "License issue exporting {Block}", PlcNative.Name(block));

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{PlcNative.Name(block)}: target invocation failed ({engEx.Message})");
                        _session.Logger?.LogError(engEx, "TargetInvocationException exporting {Block}", PlcNative.Name(block));

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{PlcNative.Name(block)}: export failed ({ex.Message})");
                        _session.Logger?.LogError(ex, "Export failed for {Block}", PlcNative.Name(block));

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{PlcNative.Name(block)}: unexpected exception ({ex.Message})");
                    _session.Logger?.LogError(ex, "Unexpected error at block {Block}", PlcNative.Name(block));
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _session.Logger?.LogWarning($"ExportPlcBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _session.Logger?.LogInformation($"ExportPlcBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportBlocksToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportBlocks(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        private readonly object _blockGroupDeleteGate = new object();
        public JsonObject DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            EmptyPlcGroupDeletion.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal==null) throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                // Destructive operations use the shared exact container resolver, never fuzzy PLC selection.
                var sc=_session.ResolveSoftwareContainerUncached(softwarePath);
                var plc=PlcNative.SoftwareOrNull(sc) as PlcSoftware ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                PlcBlockUserGroup? Find(string[] parts)
                {
                    PlcBlockGroup current=PlcNative.BlockGroup(plc);
                    foreach(var name in parts)
                    {
                        var matches=PlcNative.Groups(current).Where(g=>string.Equals(PlcNative.Name(g),name,StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                        if(matches.Count==0) return null;
                        if(matches.Count!=1) throw new PortalException(PortalErrorCode.InvalidParams,"Ambiguous user group: " + name);
                        current=matches[0];
                    }
                    return current as PlcBlockUserGroup ?? throw new PortalException(PortalErrorCode.InvalidParams,"Only user block groups can be deleted.");
                }
                var result=EmptyPlcGroupDeletion.Execute(groupPath,dryRun,Find,g=>PlcNative.Count(PlcNative.Blocks(g)),g=>PlcNative.Count(PlcNative.Groups(g)),
                    ()=>PlcNative.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath,plc))?.ToString() ?? "Unknown",
                    g=>PlcNative.Delete(g));
                result["softwarePath"]=softwarePath;
                result["resolvedSoftwareName"]=PlcNative.Name(plc);
                result["success"]=true;
                return result;
            }
        }

        #region delete blocks / tag tables / types

    /// <summary>
    /// Partial: 删除程序块 / PLC 变量表 / 用户数据类型（UDT）。
    ///
    /// 为什么删除单独成一族、而不是散在各自的 Portal.Blocks.cs / Portal.Software.cs 里：
    /// 删除是这个服务器里唯一**不可逆**的一类写操作，护栏（精确路径、dryRun、删后回读）
    /// 必须三个入口一模一样。放在一起，改一条规矩就三处一起改，不会漏。
    ///
    /// 三条共同规矩：
    /// 1) 路径必须精确 —— 叶子名含正则元字符一律拒绝。理由见 <see cref="ResolveSingleByName"/>：
    ///    定位是「先字面、再锚定正则」，模式仍可能命中到一个**同样合法但不是你要的**对象；
    ///    读错了顶多返回错东西，删错了工程就没了。所以删除口不吃模式，一个字符都不行。
    /// 2) dryRun=true 时**一行工程都不动**，只解析目标并把代价（引用它的是谁）摆出来。
    /// 3) dryRun=false 时删完必须重新取句柄回读；对象还在 = 失败，抛异常，绝不报成功。
    ///
    /// 为什么删变量表比删块更危险：块被删了，调用方编译立刻报错；
    /// 一张变量表被删，表里所有变量的**符号**同时消失，而 HMI 是按符号名绑定 PLC 变量的，
    /// PLC 侧编译不一定报错，故障要到画面上才暴露。所以这里的预览必须把
    /// 「这张表里有多少变量、都叫什么」摆给调用方，而不是只回一句「找到了，可以删」。
    /// </summary>
        /// <summary>
        /// 预览或删除**一个** PLC 程序块（FB / FC / OB / 全局 DB / 背景 DB 都是 PlcBlock）。
        /// dryRun=true（默认）只解析目标，不做任何改动。
        /// </summary>
        public JsonObject DeletePlcBlock(string softwarePath, string blockPath, bool dryRun, bool crossReferences = false)
        {
            // 参数校验放在连接检查之前：它不需要工程，放后面就只有连着 TIA 时才走得到，
            // 等于离线永远测不到这条兜底（那样它是不是死代码根本无从判断）。
            if (string.IsNullOrWhiteSpace(blockPath))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcBlock: blockPath is empty");
            }

            var leaf = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;
            if (leaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcBlock requires one exact block path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcBlock: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            var block = _session.GetBlock(softwarePath, blockPath);
            if (block == null)
            {
                // 打错名字和「块确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = _session.GetBlocks(softwarePath);
                var names = known?.Select(_session.GetBlockPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                  .Take(50).ToList() ?? new List<string>();
                throw new PortalException(PortalErrorCode.NotFound,
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
                if (!PlcNative.AutoNumber(block)) pinnedNumber = PlcNative.Number(block);
            }
            catch
            {
 /* swallow(probe-optional): 部分块类型不提供 Number/AutoNumber；缺少编号警告不阻止原有删除流程。 */                // 某些块类型不给 Number/AutoNumber。读不到就不警告，但绝不因此让删除失败。
            }

            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["requestedBlockPath"] = blockPath,
                ["resolvedBlockPath"] = resolvedPath,
                ["blockName"] = PlcNative.Name(block),
                ["blockType"] = blockType,
                ["pinnedBlockNumber"] = pinnedNumber,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new JsonArray
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

            PlcNative.Delete(block);

            // Delete() 之后原来的代理对象已死，回读必须从 PlcSoftware 重新解析一遍路径。
            bool absent = _session.GetBlock(softwarePath, resolvedPath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcBlock: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }

        /// <summary>
        /// 预览或删除一张 PLC 变量表。<paramref name="tagTableName"/> 接受裸表名或
        /// GetPlcTagTables 返回的组限定名（"驱动/变频器变量表"），反斜杠也当分隔符。
        /// dryRun=true（默认）只解析并清点内容，不做任何改动。
        /// </summary>
        public JsonObject DeletePlcTagTable(string softwarePath, string tagTableName, bool dryRun, bool crossReferences = false)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(tagTableName))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcTagTable: tagTableName is empty");
            }

            // 变量表名允许带 '/' 做组分隔，所以只校验叶子名里的元字符。
            var tableLeaf = tagTableName.Replace('\\', '/').Trim('/');
            tableLeaf = tableLeaf.Contains("/") ? tableLeaf.Substring(tableLeaf.LastIndexOf("/") + 1) : tableLeaf;
            if (tableLeaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                // 变量表的定位走字面比对，本来不会把名字当模式；但删除口的规矩三处一致比
                // 「这一处恰好安全」更重要 —— 以后谁把定位换成模式匹配，这道闸仍然在。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcTagTable requires one exact tag table name or group-qualified path; "
                    + "regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcTagTable: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            var plc = _session.ResolvePlc(softwarePath, dryRun ? PlcAccess.Read : PlcAccess.Write);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            var group = _session.ResolvePlcTagTableGroup(plc);
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: tag table group not found on '{softwarePath}' (plcType={plc.GetType().FullName})");
            }

            var wanted = tagTableName.Replace('\\', '/').Trim('/');
            var table = FindTagTableWithPath(group, string.Empty, wanted,
                new HashSet<object>(_session.ReferenceEqualityComparer), out var resolvedPath);
            if (table == null)
            {
                // 打错名字和「表确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = _session.GetPlcTagTables(softwarePath, out _) ?? new List<string>();
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: no tag table named '{tagTableName}' in '{softwarePath}'" +
                    (known.Count > 0 ? ". Available: " + string.Join(", ", known) : " (this PLC has no tag tables)"),
                    known);
            }

            var name = TryGetPropertyValue(table, "Name")?.ToString() ?? wanted;
            bool isDefault = TryGetPropertyValue(table, "IsDefault") is bool b && b;
            if (isDefault)
            {
                // PLC 必须留一张默认变量表，删掉它 TIA 侧行为未定义。宁可挡住也不试。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"DeletePlcTagTable: '{resolvedPath}' is the DEFAULT tag table (IsDefault=true) and is refused. " +
                    "A PLC always needs one default table. To empty it, delete its tags individually in TIA.");
            }

            var tags = CollectTagSummary(table, out int tagCount);
            int userConstants = CountOf(TryGetPropertyValue(table, "UserConstants"));
            int systemConstants = CountOf(TryGetPropertyValue(table, "SystemConstants"));

            var warnings = new JsonArray
            {
                "Deletion removes all " + tagCount + " tag symbols from this table."
                + "HMI binds PLC tags by symbolic name. PLC compilation may report no error; the broken binding may only become visible on the screen.",
                "If the deleted tags are used by absolute address (%I/%Q/%M), the program can still compile, "
                + "but all comments and symbols are lost and cannot be restored afterwards."
            };

            var result = new JsonObject
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
            var invoked = TryInvokeVoidMethod(table, "Delete");
            if (!invoked)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcTagTable: '{resolvedPath}' has no callable Delete() (type={table.GetType().FullName})");
            }

            // Delete() 之后原来的代理对象已死，读回必须从 PlcSoftware 重新取一遍句柄。
            var plc2 = _session.ResolvePlc(softwarePath, dryRun ? PlcAccess.Read : PlcAccess.Write);
            var group2 = plc2 == null ? null : _session.ResolvePlcTagTableGroup(plc2);
            bool absent = group2 == null || FindTagTableWithPath(group2, string.Empty, resolvedPath,
                new HashSet<object>(_session.ReferenceEqualityComparer), out _) == null;

            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcTagTable: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }

        /// <summary>
        /// 预览或删除一个 PLC 用户数据类型（UDT）。dryRun=true（默认）只解析并查引用。
        /// </summary>
        public JsonObject DeletePlcType(string softwarePath, string typePath, bool dryRun, bool crossReferences = false)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(typePath))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcType: typePath is empty");
            }

            var leaf = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;
            if (leaf.IndexOfAny(_session.RegexChars) >= 0)
            {
                // _session.GetType() 在没有字面同名时会把名字当锚定模式匹配，删除工具绝不能吃这一口。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcType requires one exact type path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcType: no project is open. ConnectPortal / AttachOpenProject first.");
            }

            PlcType? type = _session.GetType(softwarePath, typePath);
            if (type == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcType: type '{typePath}' not found in '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["requestedTypePath"] = typePath,
                ["typeName"] = PlcNative.Name(type),
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new JsonArray
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

            PlcNative.Delete(type);
            bool absent = _session.GetType(softwarePath, typePath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcType: Delete() returned but '{typePath}' is still present");
            }
            return result;
        }

        private static JsonArray ToCrossReferenceArray(List<ModelContextProtocol.CrossReferenceEntry> refs)
        {
            var arr = new JsonArray();
            foreach (var r in refs)
            {
                arr.Add(new JsonObject
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

        /// <summary>
        /// 和 Portal.Software.cs 里的 FindTagTable 走同一棵树、同一套匹配规则，
        /// 但额外回传组限定路径 —— 删除要报「到底删的是哪一张」，裸表名在多组重名时不够用。
        /// </summary>
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

        /// <summary>清点表里的变量。列表封顶，免得一张几千行的表把响应撑爆。</summary>
        private const int TagPreviewLimit = 200;

        private static JsonArray CollectTagSummary(object table, out int tagCount)
        {
            tagCount = 0;
            var arr = new JsonArray();
            var tags = TryGetPropertyValue(table, "Tags");
            if (tags is not IEnumerable e || tags is string) return arr;

            foreach (var tag in e)
            {
                if (tag == null) continue;
                tagCount++;
                if (arr.Count >= TagPreviewLimit) continue;
                arr.Add(new JsonObject
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

        /// <summary>
        /// 试着从变量表对象自己取 CrossReferenceService。
        /// 已知事实（V21 真机实测）：Software / Device / DeviceItem 三层都回 "service not available"，
        /// 只有 Block 层给得出。变量表属于哪一层没有实测数据，
        /// 所以这里**试一次**，拿不到就如实回 null + 原因，绝不假装查过。
        /// </summary>
        private List<ModelContextProtocol.CrossReferenceEntry>? TryGetTagTableCrossReferences(
            object table, string resolvedPath, out string? reason, out bool queried)
        {
            queried = false;
            reason = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable));
            if (reason != null) return null;
            try
            {
                // The service belongs to tags/constants, not the table itself. Any failed child invalidates the aggregate.
                var typed = table as global::Siemens.Engineering.SW.Tags.PlcTagTable ?? throw new NotSupportedException("Expected PlcTagTable.");
                var items = new List<ModelContextProtocol.CrossReferenceEntry>();
                var targets = EngineeringGroupOperations.Items(PlcNative.Tags(typed)).Concat(EngineeringGroupOperations.Items(PlcNative.SystemConstants(typed)));
                foreach (IEngineeringServiceProvider target in targets)
                {
                    var service = InvocationJournal.Native("TagTable.CrossReference.GetService", () => PlcNative.CrossReferences(target));
                    if (service == null) throw new NotSupportedException("CrossReferenceService missing on a tag/system constant; table result incomplete.");
                    queried = true;
                    var raw = InvocationJournal.Native("TagTable.CrossReference.query", () => PlcNative.CrossReferences(service, global::Siemens.Engineering.CrossReference.CrossReferenceFilter.AllObjects));
                    items.AddRange(InvocationJournal.Native("TagTable.CrossReference.read", () => _session.TryFlattenCrossReferenceResult(raw, resolvedPath)));
                }
                reason = null;
                return items;
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex))
            { reason = "Tag table cross-reference result incomplete; partial rows discarded: " + ex.GetBaseException().Message; return null; }
        }

        private static bool TryInvokeVoidMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(target, Array.Empty<object>());
            return true;
        }

        #endregion

        private static object RequireEngineeringService(object owner, string typeName)
        {
            // Type lookup by name so a member absent from V20 compiles on both targets and degrades to NotSupported.
            var type = typeof(PlcBlockInterface).Assembly.GetType(typeName) ?? throw new NotSupportedException("Native service type unavailable in this Openness version: " + typeName);
            if (owner is not IEngineeringServiceProvider) throw new NotSupportedException("Selected object is not a service provider.");
            try { return typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner, null) ?? throw new NotSupportedException("Service unavailable on selected object: " + typeName); }
            catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        public ResponseMessage ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "", bool confirmProtectionChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcBlockProtection", meta => {
                bool writing = PlcBlockServicesLogic.ValidateProtectionRequest(action, password, confirmProtectionChange, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                _session.ExactPlcForEngineering(softwarePath, writing);
                var block = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock ?? throw new ArgumentException("blockPath must identify a block.");
                var provider = PlcNative.Protection(block) ?? throw new NotSupportedException("PlcBlockProtectionProvider unavailable on this block/version.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = new JsonObject { ["name"] = PlcNative.Name(block), ["isKnowHowProtected"] = PlcNative.IsKnowHowProtected(block), ["programmingLanguage"] = PlcNative.Language(block).ToString() };
                var invalid = PlcNative.InvalidPasswordCharacters(provider)?.ToArray() ?? Array.Empty<char>();
                meta["invalidPasswordCharacters"] = new JsonArray(invalid.Select(c => (JsonNode)JsonValue.Create(c.ToString())!).ToArray());
                if (action == "read") return "Block know-how protection state read.";
                bool expected = action == "protect";
                if (PlcNative.IsKnowHowProtected(block) == expected) throw new InvalidOperationException(expected ? "Block is already know-how protected; unprotect first." : "Block is not know-how protected.");
                if (expected && PlcBlockServicesLogic.ContainsInvalidPasswordCharacter(password, invalid)) throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Block protection preview; no changes.";
                meta["mayHaveChanged"] = true;
                using (var secure = PlcBlockServicesLogic.ToSecureString(password))
                {
                    if (expected) PlcNative.Protect(provider, secure); else PlcNative.Unprotect(provider, secure);
                }
                meta["apiCallSuccess"] = true;
                meta["after"] = new JsonObject { ["isKnowHowProtected"] = PlcNative.IsKnowHowProtected(block) };
                if (PlcNative.IsKnowHowProtected(block) != expected) throw new InvalidOperationException("Protection readback differs from the requested state.");
                return "Block know-how protection changed and verified by readback; no save/compile/download.";
            });
        public ResponseMessage ManagePlcDataBlockSnapshot(string softwarePath, string blockPath, string action, string filePath = "", bool confirmValueChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcDataBlockSnapshot", meta => {
                bool writing = PlcBlockServicesLogic.ValidateSnapshotRequest(action, filePath, confirmValueChange, dryRun);
                bool exporting = action == "exportSnapshot";
                var file = exporting ? NativeFileOutput.Plan(filePath) : null;
                using var access = writing && !exporting ? _session.AcquireHmiEditAccess() : null;
                // Snapshot/load semantics need the PLC already online in TIA, so Offline is not demanded here; this tool never changes the online state.
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var db = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as DataBlock ?? throw new ArgumentException("blockPath must identify a data block.");
                var iface = PlcNative.Interface(db) ?? throw new NotSupportedException("Data block exposes no interface.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["onlineState"] = PlcNative.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString();
                meta["before"] = EngineeringScalarProperties.Read(db);
                const string valueServiceName = "Siemens.Engineering.SW.Blocks.Interface.ValueService";
                meta["valueServiceTypeAvailable"] = typeof(PlcBlockInterface).Assembly.GetType(valueServiceName) != null;
                object? valueService = null; InterfaceSnapshot? snapshot = null;
                if (!exporting && action != "read") valueService = RequireEngineeringService(iface, valueServiceName);
                else { try { valueService = RequireEngineeringService(iface, valueServiceName); } catch (NotSupportedException ex) { meta["valueServiceUnavailable"] = ex.Message; } }
                if (exporting || action == "read")
                {
                    var attempts = new JsonArray();
                    foreach (var (source, owner) in new (string, object?)[] { ("DataBlock.Interface", iface), ("DataBlock", db), ("ValueService", valueService) })
                    {
                        if (owner == null || snapshot != null) continue;
                        try { snapshot = PlcNative.SnapshotOrNull(owner as IEngineeringServiceProvider); if (snapshot != null) meta["snapshotServiceSource"] = source; }
                        catch (Exception ex) { attempts.Add(source + ": " + ex.GetBaseException().Message); }
                    }
                    meta["snapshotServiceAttempts"] = attempts; meta["snapshotServiceAvailable"] = snapshot != null;
                    if (exporting && snapshot == null) throw new NotSupportedException("InterfaceSnapshot service unavailable on this data block/version.");
                }
                string nativeMethod = action == "createSnapshot" ? "CreateSnapshot" : action == "loadSnapshotAsActualValues" ? "LoadSnapshotAsActualValues" : action == "loadStartValuesAsActualValues" ? "LoadStartValuesAsActualValues" : "";
                if (nativeMethod.Length > 0 && valueService!.GetType().GetMethod(nativeMethod, Type.EmptyTypes) == null) throw new NotSupportedException("ValueService." + nativeMethod + "() unavailable.");
                meta["semantics"] = "Siemens semantics: CreateSnapshot reads actual values from the CPU; load actions write values into the CPU's actual values. The PLC must already be online in TIA; this tool never goes online/offline.";
                if (action == "read") return "Data block snapshot services inspected; no changes.";
                if (exporting) meta["filePath"] = file!.FullName;
                if (!writing) return "Data block snapshot preview; no native call.";
                if (exporting)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    PlcNative.Export(snapshot!, file!, ExportOptions.None);
                    meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file!); meta["dataComplete"] = false;
                    return "Snapshot values exported by TIA and hashed; content semantics not verified; no project change.";
                }
                meta["mayHaveChanged"] = true;
                EngineeringGroupOperations.Call(valueService!, nativeMethod, Type.EmptyTypes);
                meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(db);
                meta["readbackVerified"] = false; meta["readbackScope"] = "Openness exposes no snapshot/actual value readback; only the native call return and block scalars are reported.";
                return "Native " + nativeMethod + " returned; values not independently verified; no save/compile/download.";
            });
        public ResponseMessage UpdatePlcProgram(string softwarePath, bool confirmUpdate = false, bool dryRun = true)
            => _session.RunHmiStepTool("SetPlcProgram", meta => {
                bool writing = !dryRun;
                if (writing && !confirmUpdate) throw new ArgumentException("Real program update requires confirmUpdate=true besides dryRun=false.");
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var method = plc.GetType().GetMethod("UpdateProgram", Type.EmptyTypes) ?? throw new NotSupportedException("PlcSoftware.UpdateProgram() unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath;
                meta["nativeReturnType"] = method.ReturnType.FullName;
                meta["before"] = EngineeringScalarProperties.Read(plc);
                if (!writing) return "PLC program update preview; no native call.";
                meta["mayHaveChanged"] = true;
                var result = method.Invoke(plc, null);
                meta["apiCallSuccess"] = true; meta["nativeResult"] = result == null ? null : EngineeringScalarProperties.Read(result);
                meta["after"] = EngineeringScalarProperties.Read(plc);
                meta["readbackVerified"] = false; meta["readbackScope"] = "UpdateProgram returns void; program content changes are not enumerated by this tool.";
                return "Native PlcSoftware.UpdateProgram returned; no save/compile/download.";
            });
        public ResponseMessage ReadPlcBlockFingerprints(string softwarePath, string targetIpAddress, string pgPcInterface = "", string password = "", int offset = 0, int limit = 100, bool dryRun = true)
            => _session.RunHmiStepTool("GetPlcBlockFingerprints", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<FingerprintDataProvider>(softwarePath, plc) ?? throw new NotSupportedException("FingerprintDataProvider unavailable for this PLC/version.");
                var configuration = PlcNative.Configuration(provider) ?? throw new PortalException(PortalErrorCode.InvalidState, "No connection configuration; configure the CPU network interface first.");
                var candidates = new List<PlcBlockServicesLogic.RouteCandidate>();
                foreach (var mode in _session.EnumerateReflectedProperty(configuration, "Modes"))
                    foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        foreach (var target in _session.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                            foreach (var address in _session.EnumerateReflectedProperty(target, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = _session.ReadReflectedString(target, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                        // The CPU's configured IP is listed under the PC interface's subnets / gateways (the target interface stays empty until the adapter sees the CPU)
                        foreach (var subnet in _session.EnumerateReflectedProperty(pcInterface, "Subnets"))
                        {
                            foreach (var address in _session.EnumerateReflectedProperty(subnet, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "subnet " + _session.ReadReflectedString(subnet, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                            foreach (var gateway in _session.EnumerateReflectedProperty(subnet, "Gateways"))
                                foreach (var address in _session.EnumerateReflectedProperty(gateway, "Addresses"))
                                    if (address is ConfigurationAddress native)
                                        candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "gateway " + _session.ReadReflectedString(gateway, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                        }
                    }
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["contactedPlc"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["candidateRoutes"] = new JsonArray(candidates.Take(100).Select(c => (JsonNode)JsonValue.Create(c.Describe())!).ToArray());
                var route = PlcBlockServicesLogic.SelectFingerprintRoute(candidates, targetIpAddress, pgPcInterface);
                meta["selectedRoute"] = route.Describe();
                if (dryRun) return "Fingerprint read preview: route resolved, PLC not contacted.";
                using var secure = string.IsNullOrEmpty(password) ? null : PlcBlockServicesLogic.ToSecureString(password);
                OnlineConfigurationDelegate handler = PlcNative.Handler(cfg =>
                {
                    if (secure != null && cfg is OnlinePasswordConfiguration pwd) PlcNative.Password(pwd, secure);
                    else if (cfg is TlsVerificationConfiguration tls)   // FW >= 2.9 CPUs ask for certificate trust before any online read
                    {
                        var before = PlcNative.Selection(tls).ToString();
                        if (EngineeringCredentialRules.TlsSelectionToApply(true, before) != null) PlcNative.Selection(tls, TlsVerificationConfigurationSelection.Trusted);
                        meta["tlsVerification"] = new JsonObject { ["plcName"] = PlcNative.PlcName(tls), ["verificationInfo"] = PlcNative.VerificationInfo(tls), ["selectionBefore"] = before, ["selectionAfter"] = PlcNative.Selection(tls).ToString() };
                    }
                });
                meta["contactedPlc"] = true;
                var result = PlcNative.Fingerprints(provider, (ConfigurationAddress)route.NativeAddress!, handler) ?? throw new InvalidOperationException("GetFingerprintData returned no result.");
                meta["apiCallSuccess"] = true;
                var items = EngineeringGroupOperations.Items(PlcNative.Items(result)).Cast<FingerprintDataItem>().ToArray();
                var window = PlcBlockServicesLogic.Paginate(meta, items.Length, offset, limit);
                meta["records"] = new JsonArray(items.Skip(window.Skip).Take(window.Take).Select(i => (JsonNode)new JsonObject { ["identifier"] = PlcNative.Identifier(i), ["value"] = PlcNative.Value(i) }).ToArray());
                return "Fingerprint data read from the PLC via the selected route; no project or PLC change.";
            });

        public ResponseMessage ImportPlcBlockVerified(string softwarePath, string blockPath, string importPath, string evidenceDirectory, bool dryRun, string expectedToken, bool compileAfterImport)
            => _session.RunHmiStepTool("ImportPlcBlockVerified", meta => {
                var candidate = PlcDocumentEditing.Read(importPath);
                var candidateDocument = PlcDocumentEditing.Parse(candidate);
                var parts = EngineeringGroupOperations.Parts(blockPath);
                if (parts.Length == 0 || parts.Last() != PlcDocumentEditing.Name(candidateDocument)) throw new ArgumentException("Exact blockPath must end with candidate Name.");
                meta["mayHaveChanged"] = false; meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath;
                IDisposable? exclusive = null;
                try {
                    exclusive = _session.AcquireHmiEditAccess();
                    var plc = _session.ExactPlcForEngineering(softwarePath, true); // Offline even for export preview.
                    var group = (PlcBlockGroup)EngineeringGroupOperations.Group(PlcNative.BlockGroup(plc), string.Join("/", parts.Take(parts.Length - 1)));
                    PlcBlock Resolve() => PlcNative.Find(PlcNative.Blocks(group), parts.Last()) ?? throw new InvalidOperationException("Exact existing block not found in its target group.");
                    void Check() { _session.VerifyBinding("ImportPlcBlockVerified"); _session.ExactPlcForEngineering(softwarePath, true); }
                    void Export(string path) {
                        var block = Resolve();
                        if (!PlcNative.IsConsistent(block)) throw new InvalidOperationException("Block is inconsistent; export verification is unavailable. No automatic compile or further import.");
                        PlcNative.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.WithDefaults | ExportOptions.WithReadOnly);
                    }
                    void Import(string path) {
                        Resolve();
                        var result = PlcNative.Import(PlcNative.Blocks(group), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ImportOptions.Override);
                        if (result == null || result.Count != 1) throw new InvalidOperationException("Import did not return exactly one block; inspect retained evidence.");
                    }
                    void Compile() {
                        var compiler = PlcNative.Compiler(Resolve()) ?? throw new NotSupportedException("Block compiler service unavailable.");
                        var result = PlcNative.Compile(compiler);
                        if (result == null) throw new InvalidOperationException("Block compiler returned no result.");
                        meta["compileState"] = PlcNative.State(result).ToString(); meta["compileErrorCount"] = PlcNative.ErrorCount(result); meta["compileWarningCount"] = PlcNative.WarningCount(result);
                        if (PlcNative.ErrorCount(result) != 0 || (PlcNative.State(result).ToString() != "Success" && !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcNative.State(result)))) throw new InvalidOperationException("Imported block compilation failed. Backup retained; no further native readback attempted.");
                    }
                    var identity = _session.GetBindingIdentity();
                    if (identity["identity"] == null) throw new InvalidOperationException("Exact binding identity required.");
                    var scope = identity.ToJsonString() + "\n" + softwarePath + "\n" + blockPath;
                    return PlcVerifiedImport.Execute(candidate, scope, evidenceDirectory, dryRun, expectedToken, Export, Import, Check, meta, compileAfterImport ? (Action)Compile : null);
                }
                catch (Exception ex) { if (HmiReadSafety.ConnectionUnavailable(ex)) meta["connectionUnavailable"] = true; throw; }
                finally {
                    if (exclusive != null && meta["connectionUnavailable"]?.GetValue<bool>() == true) meta["exclusiveReleaseSkipped"] = true;
                    else exclusive?.Dispose();
                }
            });

        public JsonObject CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            PlcTypeGroupCreation.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal == null)
                throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcNative.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                var result = PlcTypeGroupCreation.Execute<PlcTypeGroup>(PlcNative.TypeGroup(plc), groupPath, dryRun,
                    g => PlcNative.Groups(g), g => PlcNative.Name(g), (g, name) => PlcNative.Create(PlcNative.Groups(g), name));
                result["softwarePath"] = softwarePath;
                result["resolvedSoftwareName"] = PlcNative.Name(plc);
                return result;
            }
        }

        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcUserGroup", meta => {
                var shape = family switch {
                    "blocks" => ("BlockGroup", "Blocks"), "types" => ("TypeGroup", "Types"),
                    "tags" => ("TagTableGroup", "TagTables"), "technology" => ("TechnologicalObjectGroup", "TechnologicalObjects"),
                    "watchTables" => ("WatchAndForceTableGroup", "WatchTables"), "externalSources" => ("ExternalSourceGroup", "ExternalSources"),
                    _ => throw new ArgumentException("family must be blocks, types, tags, technology, watchTables or externalSources.") };
                EngineeringGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcNative.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PortalException(PortalErrorCode.NotFound, "PLC software not found: " + softwarePath);
                meta["softwarePath"] = softwarePath; meta["resolvedSoftwareName"] = PlcNative.Name(plc);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun && PlcNative.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString() != "Offline")
                    throw new PortalException(PortalErrorCode.InvalidState, "Confirmed Offline state is required for group editing.");
                if (!dryRun) meta["mayHaveChanged"] = true;
                meta["result"] = EngineeringGroupOperations.Manage(EngineeringGroupOperations.Get(plc, shape.Item1), groupPath, action, newName, dryRun, shape.Item2);
                if (action != "deleteEmpty" || dryRun) { try { meta["group"] = UserGroupRow(EngineeringGroupOperations.Group(EngineeringGroupOperations.Get(plc, shape.Item1), action == "rename" && !dryRun ? string.Join("/", EngineeringGroupOperations.Parts(groupPath).Take(EngineeringGroupOperations.Parts(groupPath).Length - 1).Append(newName)) : groupPath)); } catch (Exception ex) { meta["groupError"] = ex.GetBaseException().Message; } }
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });

        // Typed user-group row for every STEP 7 user group class (Name is writable, Delete exists on each).
        private static JsonObject UserGroupRow(object group) => group switch
        {
            global::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup b => new JsonObject { ["name"] = PlcNative.Name(b), ["groupClass"] = b.GetType().Name, ["blocks"] = PlcNative.Count(PlcNative.Blocks(b)), ["groups"] = PlcNative.Count(PlcNative.Groups(b)) },
            global::Siemens.Engineering.SW.Types.PlcTypeUserGroup ty => new JsonObject { ["name"] = PlcNative.Name(ty), ["groupClass"] = ty.GetType().Name, ["types"] = PlcNative.Count(PlcNative.Types(ty)), ["groups"] = PlcNative.Count(PlcNative.Groups(ty)) },
            global::Siemens.Engineering.SW.Tags.PlcTagTableUserGroup tg => new JsonObject { ["name"] = PlcNative.Name(tg), ["groupClass"] = tg.GetType().Name, ["tagTables"] = PlcNative.Count(PlcNative.TagTables(tg)), ["groups"] = PlcNative.Count(PlcNative.Groups(tg)) },
            global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup w => new JsonObject { ["name"] = PlcNative.Name(w), ["groupClass"] = w.GetType().Name, ["watchTables"] = PlcNative.Count(PlcNative.WatchTables(w)), ["forceTables"] = PlcNative.Count(PlcNative.ForceTables(w)), ["groups"] = PlcNative.Count(PlcNative.Groups(w)) },
            global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup e => new JsonObject { ["name"] = PlcNative.Name(e), ["groupClass"] = e.GetType().Name, ["externalSources"] = PlcNative.Count(PlcNative.ExternalSources(e)), ["groups"] = PlcNative.Count(PlcNative.Groups(e)) },
            _ => new JsonObject { ["name"] = EngineeringGroupOperations.Get(group, "Name").ToString(), ["groupClass"] = group.GetType().Name }
        };

        // ======================================================================
        // Block group operations: create (sub)groups + move a block into a group.
        // NOTE: TIA Openness has NO API to reparent an existing PlcBlock
        // (PlcBlock.Parent is read-only). So:
        //   - EnsurePlcBlockGroup: native, via PlcBlockUserGroupComposition.Create.
        //   - MoveBlockToGroup: export -> Delete -> import-into-group round-trip,
        //     so generated blocks can be organized into layer folders afterwards.
        // ======================================================================

        // Navigate the block-group tree by path, CREATING any missing user groups.
        // Returns the leaf group (or null if software not found). `created` lists the
        // group names that were newly created this call (for reporting / idempotency).
        public PlcBlockGroup? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>();
            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcNative.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcNative.BlockGroup(plcSoftware) == null)
            {
                return null;
            }

            var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            PlcBlockGroup currentGroup = PlcNative.BlockGroup(plcSoftware);
            foreach (var groupName in groupNames)
            {
                var next = PlcNative.Groups(currentGroup).FirstOrDefault(g => PlcNative.Name(g).Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = PlcNative.Create(PlcNative.Groups(currentGroup), groupName);
                    created.Add(groupName);
                    _session.Logger?.LogInformation($"Created PLC block group '{groupName}'");
                }
                currentGroup = next;
            }
            return currentGroup;
        }

        // Move an existing block (found anywhere by exact name) into targetGroupPath.
        // Implemented as export -> Delete -> import-into-group because Openness cannot
        // reparent a block. Prefers SIMATIC SD documents (.s7dcl, keeps comments);
        // falls back to SimaticML XML for mixed-language/STL blocks. Returns a summary.
        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
        {
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcNative.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcNative.BlockGroup(plcSoftware) == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");
            }

            // 1) find the block anywhere by exact name
            var all = new List<PlcBlock>();
            _session.GetBlocksRecursive(PlcNative.BlockGroup(plcSoftware), all);
            var block = all.FirstOrDefault(b => PlcNative.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Block '{blockName}' not found in '{softwarePath}'");
            }

            // Siemens SIMATIC SD import recreates OBs as cyclic OBs with a new
            // number. Do not delete an original OB through this generic move route.
            if (block is OB)
            {
                var existingGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                if (existingGroup != null && ReferenceEquals(PlcNative.Parent(block), existingGroup))
                    return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion,
                    "Moving organization blocks by export/delete/import is disabled: SIMATIC SD import does not preserve OB type/number. Move this OB in TIA Portal.");
            }

            // 2) ensure the target group exists
            var targetGroup = autoCreateGroup
                ? EnsurePlcBlockGroup(softwarePath, targetGroupPath, out _)
                : _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            if (targetGroup == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Target block group '{targetGroupPath}' not found (set autoCreateGroup=true to create it)");
            }

            // already in the target group?
            if (ReferenceEquals(PlcNative.Parent(block), targetGroup))
            {
                return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
            }
            if (PlcNative.Blocks(targetGroup).Any(b => PlcNative.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase)))
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Target group already contains '{blockName}'; relocation never overwrites another block.");

            // 3) export -> delete -> import into target group (no native reparent)
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_move", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string method;
            bool moveVerified = false;
            try
            {
                bool usedDocs;
                try
                {
                    var exp = PlcNative.ExportDocuments(block, new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempDir)), blockName);
                    usedDocs = exp != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcNative.State(exp));
                }
                catch (EngineeringNotSupportedException)
                {
 /* swallow(native-fallback): Unsupported document export falls back to SimaticML before deleting the source block. */                    usedDocs = false; // mixed-language / STL -> fall back to XML
                }

                if (usedDocs)
                {
                    PlcNative.Delete(block);
                    var res = PlcNative.ImportDocuments(PlcNative.Blocks(targetGroup), new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(tempDir)), blockName, ImportDocumentOptions.Override);
                    if (res == null || !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(PlcNative.State(res)))
                    {
                        var evidence = new JsonObject();
                        TiaMcp.Logic.V4.NativeResultState.Record(evidence, res?.State, true, messages: res == null ? null : _session.DocumentMessages(res.Messages));
                        var failure = new PortalException(PortalErrorCode.ImportFailed,
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (documents)");
                        failure.Data["nativeResultEvidence"] = evidence;
                        throw failure;
                    }
                    method = "documents(.s7dcl)";
                }
                else
                {
                    var xml = Path.Combine(tempDir, blockName + ".xml");
                    PlcNative.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ExportOptions.None);
                    PlcNative.Delete(block);
                    var imp = PlcNative.Import(PlcNative.Blocks(targetGroup), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ImportOptions.Override);
                    if (imp == null || imp.Count == 0)
                    {
                        throw new PortalException(PortalErrorCode.ImportFailed,
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (xml)");
                    }
                    method = "xml(SimaticML)";
                }
                var verifyGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                var verifyBlocks = PlcNative.BlocksOrNull(verifyGroup);
                moveVerified = verifyGroup != null && verifyBlocks.Any(b => PlcNative.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
                if (!moveVerified) throw new PortalException(PortalErrorCode.ImportFailed,
                    $"Move of '{blockName}' to '{targetGroupPath}' could not be verified");
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed,
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
