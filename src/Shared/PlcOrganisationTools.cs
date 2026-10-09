using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcOrganisationTools
    {
        private readonly PlcOrganisationPortService _blocks;
        public PlcOrganisationTools(PlcOrganisationPortService blocks) => _blocks = blocks;

        [McpServerTool(Name = "ManagePlcBlockProtection"), Description("[L2][PLC-Software][WRITE] Read/protect/unprotect know-how protection of one exact block path via native PlcBlockProtectionProvider. Password is passed straight to TIA and never stored or logged; native invalid-password characters are reported. Refuses protecting an already protected block or unprotecting an unprotected one. Default preview; real change requires dryRun=false AND confirmProtectionChange=true, an Offline PLC, and is verified by IsKnowHowProtected readback. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcBlockProtectionV4(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | protect | unprotect.")] string action,
            string password="",
            [Description("confirmProtectionChange: must be true together with dryRun=false to change the protection.")] bool confirmProtectionChange=false,
            bool dryRun=true)
            => PlcToolContract.Run("ManagePlcBlockProtection", !dryRun && action != "read", true, () => ManagePlcBlockProtection(softwarePath, blockPath, action, password, confirmProtectionChange, dryRun));

        public ResponseMessage ManagePlcBlockProtection(
            string softwarePath,
            string blockPath,
            string action,
            string password="",
            bool confirmProtectionChange=false,
            bool dryRun=true)
            => _blocks.ManagePlcBlockProtection(softwarePath,blockPath,action,password,confirmProtectionChange,dryRun);

        [McpServerTool(Name = "DeletePlcBlock"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete exactly one PLC block by its exact path, including "
            + "blocks inside nested groups. THIS IS ALSO THE TOOL FOR DELETING A DATA BLOCK: global DB, instance DB, "
            + "ARRAY DB, FB, FC and OB are all PLC blocks, so there is no separate DeleteGlobalDb / DeleteDb / "
            + "DeleteFunctionBlock tool - use this one. Defaults to dryRun=true, which changes nothing and reports "
            + "the resolved target (pinned block number, warnings). Native cross references are disabled by default at the server-process level; see GetPlcCrossReferences. They also require "
            + "crossReferences=true: on the maintainer's real project (2026-09-21) that CrossReferenceService query took "
            + "TIA Portal V21 down during a dry run, so it is off by default and the response says 'not queried' - never "
            + "read that as 'nobody uses it'. It never deletes instance DBs or callers automatically. Before dryRun=false, "
            + "back the block up with ExportPlcBlockDocuments and review dependencies (an unavailable query does not prove it is unused); compile with "
            + "CompilePlcSoftware after deletion and before SaveProject. Regex and wildcards are rejected. To delete a tag "
            + "table use DeletePlcTagTable, a UDT use DeletePlcType. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcBlockV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockPath: exact block path, e.g. 'DB_Test' or 'GroupA/FB_Motor'. Regex and wildcards are rejected.")] string blockPath,
            [Description("dryRun: true (default) only resolves and reports the target; false performs Delete() and verifies the block is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcBlock", !dryRun, true, () => DeletePlcBlock(softwarePath, blockPath, dryRun, crossReferences));

        public ResponseJsonReport DeletePlcBlock(
            string softwarePath,
            string blockPath,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcBlock(softwarePath, blockPath, dryRun, crossReferences);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;
                int? pinned = data["pinnedBlockNumber"]?.GetValue<int>();

                // 🔴 显式块号是**在删除这一刻**丢的，事后再提醒已经晚了：
                // 把 FB 钉成 103（AutoNumber=false）→ 删掉 → 从同一份外部源重建 →
                // 新块拿到自动分配的号，103 一去不返，依赖它的实例 DB 关联随之断裂，全程无报错。
                // 「不删、直接对已有块重新生成」是安全的，编号原样保留 —— 所以这里要说的不是
                // 「别删」，而是「删了就拿不回来，想保号就别删」。
                string? pinnedWarning = pinned == null ? null
                    : $"This block has the explicit block number {pinned}(AutoNumber=false)."
                      + (dryRun ? "Deleting the block will also remove this number: " : "This number has already been removed with the block: ")
                      + "when rebuilt from an external source, the new block receives an automatically assigned number. Instance DB associations that depend on the original block number will break without any error being reported. "
                      + "To update block content, keep the block and run GenerateBlocksFromExternalSource against the existing block to preserve its number."
                      + $"If deletion and recreation are required, restore the number afterwards with InvokeObject: SetAttribute(\"AutoNumber\", false) then SetAttribute(\"Number\", {pinned}).";

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"Program block '{data["resolvedBlockPath"]}'",
                    dryRunTail: "After verifying the preview, use dryRun=false to perform the deletion.",
                    extraWarning: pinnedWarning,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcBlockDocuments - back up this block before deletion.",
                            "GetPlcCrossReferences - inspect each remaining caller.",
                            "After confirmation, call DeletePlcBlock(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect dangling caller references",
                            "SaveProject: save only after verification"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC block '{blockPath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC block '{blockPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcTagTable"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete ONE PLC tag table (variable table / tag list) "
            + "by name, including tables nested in user groups. Defaults to dryRun=true, which only reports what "
            + "the table contains and deletes nothing. DANGER: deleting a tag table removes the SYMBOLS of every "
            + "tag in it. HMI panels bind PLC tags by symbolic name, so the PLC may still compile clean while the "
            + "HMI silently loses its bindings - always review the previewed tag list first. Native cross references are disabled by default at the server-process level; see GetPlcCrossReferences. They are "
            + "queried only with crossReferences=true (the same TIA CrossReferenceService that took TIA Portal V21 down "
            + "during a DeletePlcBlock dry run on the maintainer's project, 2026-09-21) and may be unavailable at "
            + "tag-table level anyway; the response says explicitly whether they were queried and obtained. Regex and "
            + "wildcards are rejected. Back up first with ExportPlcTagTable. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcTagTableV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tagTableName: bare table name, or the group-qualified path from ListPlcTagTables (e.g. 'Drives/VFD tags'). Regex and wildcards are rejected.")] string tagTableName,
            [Description("dryRun: true (default) only resolves the table and lists its contents; false performs Delete() and verifies the table is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcTagTable", !dryRun, true, () => DeletePlcTagTable(softwarePath, tagTableName, dryRun, crossReferences));

        public ResponseJsonReport DeletePlcTagTable(
            string softwarePath,
            string tagTableName,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcTagTable(softwarePath, tagTableName, dryRun, crossReferences);
                int tagCount = data["tagCount"]?.GetValue<int>() ?? 0;
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                var report = BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"Tag table '{data["resolvedTagTablePath"]}'({tagCount} tag(s))",
                    dryRunTail: "After verifying the preview, use dryRun=false to perform the deletion.",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcTagTable: export a backup of this table before deleting it",
                            "Use GetPlcCrossReferences on the related blocks; table-level references may be unavailable.",
                            "After confirmation, call DeletePlcTagTable(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect broken PLC references",
                            "⚠️ Compilation cannot detect HMI symbol-binding failures; verify screen tags separately",
                            "SaveProject: save only after verification"
                        });

                report.Meta!["tagCount"] = tagCount;
                return report;
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC tag table '{tagTableName}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcType"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete ONE PLC user data type (UDT / PlcType) by its exact "
            + "path. Defaults to dryRun=true. Deleting a UDT breaks every DB and block interface declared with it; "
            + "native cross references are disabled by default at the server-process level (see GetPlcCrossReferences) and also require crossReferences=true (the TIA CrossReferenceService query "
            + "took TIA Portal V21 down during a DeletePlcBlock dry run on the maintainer's project, 2026-09-21), so "
            + "review dependencies before deletion; an unavailable query is not evidence it is unused. Regex and wildcards are rejected. Export the type "
            + "first with ExportPlcType, and CompilePlcSoftware afterwards. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcTypeV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("typePath: exact UDT path, e.g. 'UDT_Motor' or 'GroupA/UDT_Motor'. Regex and wildcards are rejected.")] string typePath,
            [Description("dryRun: true (default) only resolves the type; false performs Delete() and verifies the type is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcType", !dryRun, true, () => DeletePlcType(softwarePath, typePath, dryRun, crossReferences));

        public ResponseJsonReport DeletePlcType(
            string softwarePath,
            string typePath,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcType(softwarePath, typePath, dryRun, crossReferences);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"UDT '{typePath}'",
                    dryRunTail: "After verifying the preview, use dryRun=false to perform the deletion.",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcType: back up this UDT before deletion",
                            "GetPlcCrossReferences - inspect DBs and blocks using this data type.",
                            "After confirmation, call DeletePlcType(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect DBs or blocks with missing type definitions",
                            "SaveProject: save only after verification"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC type '{typePath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC type '{typePath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildDeletionReport(
            JsonObject data, bool dryRun, bool crossRefOk,
            string objectLabel, string dryRunTail, string? extraWarning, JsonArray nextActions)
        {
            bool deleted = data["deleted"]?.GetValue<bool>() ?? false;
            bool verifiedAbsent = data["verifiedAbsent"]?.GetValue<bool>() ?? false;

            var warnings = new List<string>();
            if (data["warnings"] is JsonArray raw)
            {
                warnings.AddRange(raw.Select(w => w?.GetValue<string>()).Where(w => w != null)!);
            }
            if (extraWarning != null) warnings.Add(extraWarning);

            string message;
            bool ok;
            if (dryRun)
            {
                // 预览路径：工程一行没动。交叉引用是预览的全部价值，取不到就必须明说，
                // 否则「成功」会被读成「确认可以删」—— 删除类工具里这是代价最大的错档。
                ok = true;
                bool queried = data["crossReferenceQueried"]?.GetValue<bool>() ?? true;
                message = $"[dryRun] No changes made. Target: {objectLabel}, "
                        + (crossRefOk
                            ? $"Cross-references: {data["crossReferenceCount"]} (see data.crossReferences)."
                            : queried
                                ? "⚠️ Cross-references could not be retrieved. This does not mean the object is unreferenced; verify it before continuing."
                                : "Cross-references were not queried (" + (data["crossReferenceUnavailableReason"]?.GetValue<string>() ?? "crossReferences=false, the default")
                                    + "). This does not mean the object is unreferenced.")
                        + dryRunTail;
            }
            else if (deleted && verifiedAbsent)
            {
                ok = true;
                message = $"{objectLabel} was deleted and a fresh readback confirmed its absence.";
            }
            else
            {
                // 走到这里说明 Delete() 调过但回读没能确认对象消失。绝不当成功报。
                ok = false;
                message = $"⚠️ UNVERIFIED: {objectLabel} deletion outcome cannot be confirmed (deleted={deleted}, verifiedAbsent={verifiedAbsent}). "
                        + "Manually verify in TIA whether the object still exists; do not continue on the assumption that it was deleted.";
                warnings.Add("Post-deletion readback confirmation failed; this result is unverified.");
            }

            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = ResponseMeta.Basic(ok, ("dryRun", dryRun), ("deleted", deleted),
                    ("verifiedAbsent", verifiedAbsent), ("crossReferenceAvailable", crossRefOk), ("nextActions", nextActions))
            };
        }

        [McpServerTool(Name = "CreatePlcTypeGroup"), Description("[L2][PLC-Software][WRITE] Preview or create nested PLC data type (UDT) user groups. Exact groupPath relative to PLC data types, e.g. Common/Motors. Creates missing parents and reuses existing groups. dryRun defaults to true; pass false to create. Uses exact PLC software resolution. Does not create a UDT, save, compile or download. Errors report already-created parents; no automatic rollback. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult CreatePlcTypeGroupV4(string softwarePath, string groupPath, bool dryRun = true)
            => PlcToolContract.Run("CreatePlcTypeGroup", !dryRun, true, () => CreatePlcTypeGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            try
            {
                return new ResponseMessage
                {
                    Message = dryRun ? "PLC type group creation preview; nothing changed." : "PLC type group path ready.",
                    Meta = _blocks.CreatePlcTypeGroup(softwarePath, groupPath, dryRun)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException("CreatePlcTypeGroup failed: " + ex.Message, ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "DeleteEmptyPlcBlockGroup"), Description("[L2][PLC-Software][WRITE] Preview or delete exactly one empty PLC user block group. dryRun defaults to true. Requires an exact groupPath relative to Program blocks, e.g. ZZ_MCP_TEST or Parent/Child. Root, nonempty, ambiguous targets are refused. Real deletion requires confirmed Offline state and exclusive access, rechecks contents, calls native Delete and verifies absence. No recursive deletion, save, compile, download or automatic DisconnectOnlinePlc. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeleteEmptyPlcBlockGroupV4(string softwarePath, string groupPath, bool dryRun=true)
            => PlcToolContract.Run("DeleteEmptyPlcBlockGroup", !dryRun, true, () => DeleteEmptyPlcBlockGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            try
            {
                var result=_blocks.DeleteEmptyPlcBlockGroup(softwarePath,groupPath,dryRun);
                return new ResponseMessage { Message=dryRun ? "Empty PLC group deletion preview; nothing changed." : "Empty PLC user group deleted and absence verified.", Meta=result };
            }
            catch(Exception ex) when (ex is not McpException)
            { throw new McpException("DeleteEmptyPlcBlockGroup failed: " + ex.Message,ex,McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "CreatePlcBlockGroup"), Description("[L2][PLC-Software] Create nested program-block groups, creating missing parents and reusing existing groups. groupPath is relative to Program blocks. Requires ConnectPortal + OpenProject. Organize blocks with MovePlcBlockToGroup. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult CreatePlcBlockGroupV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("groupPath: '/'-separated group path under Program blocks, e.g. '01_手动控制/手动意图'")] string groupPath)
            => PlcToolContract.Run("CreatePlcBlockGroup", true, true, () => CreatePlcBlockGroup(softwarePath, groupPath));

        public ResponseMessage CreatePlcBlockGroup(
            string softwarePath,
            string groupPath)
        {
            try
            {
                var group = _blocks.EnsurePlcBlockGroup(softwarePath, groupPath, out var created);
                if (group == null)
                {
                    throw new McpException($"Could not create block group '{groupPath}': PlcSoftware not found at '{softwarePath}'", McpErrorCode.InvalidParams);
                }
                return new ResponseMessage
                {
                    Message = created.Count > 0
                        ? $"PLC block group '{groupPath}' ready (created: {string.Join(", ", created)})"
                        : $"PLC block group '{groupPath}' already existed",
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("createdCount", created.Count))
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed creating PLC block group '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating PLC block group '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "MovePlcBlockToGroup"), Description("[L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: ConnectPortal + OpenProject + block consistent (compile first). After moving, call CompilePlcDiagnostics to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult MoveBlockToGroupV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name to move (searched across all groups)")] string blockName,
            [Description("targetGroupPath: '/'-separated destination group under Program blocks, e.g. '02_手自动接口'")] string targetGroupPath,
            [Description("autoCreateGroup: create the target group path if it does not exist (default true)")] bool autoCreateGroup = true)
            => PlcToolContract.Run("MovePlcBlockToGroup", true, true, () => MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup));

        public ResponseMessage MoveBlockToGroup(
            string softwarePath,
            string blockName,
            string targetGroupPath,
            bool autoCreateGroup = true)
        {
            try
            {
                var summary = _blocks.MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed moving block '{blockName}' to '{targetGroupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error moving block '{blockName}' to '{targetGroupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ManagePlcUserGroup"), Description("[L2][PLC-Software][WRITE] Create, rename or deleteEmpty an exact nested PLC user group. family: blocks/types/tags/technology/watchTables/externalSources (PlcBlockUserGroup / PlcTypeUserGroup / PlcTagTableUserGroup / TechnologicalInstanceDBUserGroup / PlcWatchAndForceTableUserGroup / PlcExternalSourceUserGroup; the resulting group is read back as a typed row). groupPath is relative to the family's root. newName is one segment for rename. Default dryRun=true; actual edits require Offline and exclusive access. Missing parents are created; root and nonempty deletion refused. No save/compile/download. Partial failures may leave created parents; inspect errors. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcUserGroupV4(string softwarePath, [Description("blocks | types | tags | technology | watchTables | externalSources. PLC user-group family.")] string family, string groupPath, [Description("create | rename | deleteEmpty. ")] string action, string newName = "", bool dryRun = true)
            => PlcToolContract.Run("ManagePlcUserGroup", !dryRun, true, () => ManagePlcUserGroup(softwarePath, family, groupPath, action, newName, dryRun));

        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _blocks.ManagePlcUserGroup(softwarePath, family, groupPath, action, newName, dryRun);
        public ResponseMessage ReadPlcSystemGroups(
            string softwarePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("includeBlocks: true also returns the blocks of each chart / group.")] bool includeBlocks=true,
            int maxDepth=4)
        => _blocks.ReadPlcSystemGroups(softwarePath,unitName,unitKind,includeBlocks,maxDepth);

        [McpServerTool(Name = "ListPlcSystemGroups"), Description("[L2][PLC-Software][READ] Typed read of the system-generated groups of the exact PLC (or of a unit via unitName + unitKind): PlcBlockSystemGroup.SystemBlockGroups as a PlcSystemBlockGroup tree (Name, blocks with number / class / language when includeBlocks, nested Groups to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types). First 200 objects per group. No modification." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ReadPlcSystemGroupsV4(
            string softwarePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("includeBlocks: true also returns the blocks of each chart / group.")] bool includeBlocks=true,
            int maxDepth=4)
        {
            return PlcExchangeContract.Run("ListPlcSystemGroups", () => ReadPlcSystemGroups(softwarePath, unitName, unitKind, includeBlocks, maxDepth), write: false, current: true);
        }
    }
}
