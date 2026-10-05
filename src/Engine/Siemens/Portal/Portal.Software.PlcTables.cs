using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region software - PlcTables

        /// <summary>
        /// List every PLC tag table, including the ones nested in user groups. Tables inside a group
        /// come back group-qualified ("驱动/变频器变量表"); root-level tables keep their bare name.
        /// Returns null only when the PLC software itself cannot be resolved.
        /// </summary>
        /// <remarks>
        /// Do NOT route this through TryListNamesFromCollection with a "TagTables" hint: the object in
        /// hand is already the PlcTagTableComposition, so asking it for a *.TagTables* property misses,
        /// a non-empty hint list also skips the plain-IEnumerable path, and the helper swallows that
        /// into an empty list. The tool then answered "this PLC has no tag tables" for every
        /// S7-1200/1500 project ever built — GitHub issue #22.
        /// </remarks>
        public List<string>? GetPlcTagTables(string softwarePath)
        {
            return GetPlcTagTables(softwarePath, out _);
        }

        /// <summary>
        /// 同上，外加一份「走过了什么」的诊断。空清单时它是唯一的证据来源。
        /// </summary>
        public List<string>? GetPlcTagTables(string softwarePath, out TagTableWalkDiagnostics diagnostics)
        {
            diagnostics = new TagTableWalkDiagnostics();
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcTagTableGroup(plc);
            if (group == null)
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag table group not found on '{softwarePath}' (plcType={plc.GetType().FullName}). " +
                    "Tag tables cannot be enumerated for this software object.");

            diagnostics.RootGroupType = group.GetType().FullName ?? group.GetType().Name;
            var result = new List<string>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectTagTableNames(group, "", result, visited, diagnostics);
            diagnostics.TablesFound = result.Count;
            return result;
        }

        /// <summary>
        /// Export one PLC tag table. <paramref name="tagTableName"/> takes either the bare table name
        /// (matched anywhere in the group tree) or the group-qualified form returned by
        /// <see cref="GetPlcTagTables"/>; backslashes count as separators too, so the path printed by
        /// GetCrossReferences can be pasted straight in.
        /// </summary>
        public bool ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath, out string? error)
        {
            error = null;
            if (IsProjectNull()) { error = "no project is open"; return false; }
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) { error = $"PLC software not found at '{softwarePath}'" + AvailablePlcPathsSuffix(); return false; }

            var group = ResolvePlcTagTableGroup(plc);
            if (group == null) { error = $"tag table group not found on '{softwarePath}'"; return false; }

            var wanted = (tagTableName ?? string.Empty).Replace('\\', '/').Trim('/');
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var table = FindTagTable(group, "", wanted, visited);
            if (table == null)
            {
                // "no such table" and "found it, but Openness refused the export" used to share one
                // message, so a caller could not tell a typo from a real failure (GitHub issue #22).
                var known = GetPlcTagTables(softwarePath) ?? new List<string>();
                error = $"no tag table named '{tagTableName}' in '{softwarePath}'" +
                        (known.Count > 0 ? ". Available: " + string.Join(", ", known) : " (this PLC has no tag tables)");
                return false;
            }
            return TryExportEngineeringObject(table, exportPath, out error);
        }

        /// <summary>The container that owns TagTables (and the user groups below it).</summary>
        private static object? ResolvePlcTagTableGroup(object plc)
            => TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder")
               // HMI-shaped software hangs the composition straight off the root.
               ?? (TryGetPropertyValue(plc, "TagTables") != null ? plc : null);


        /// <summary>
        /// 枚举变量表时**顺手记下走过了什么**，专门给「返回空清单」这种情况用。
        ///
        /// 为什么要有它：空清单有三种完全不同的成因 —— 这个 PLC 确实没有表、
        /// TagTables 属性在这个版本上叫别的名字、读属性时抛了异常被吞掉。
        /// 诊断区分这三种情况，帮助调用方判断空清单的原因。
        /// 用户报「V20 上枚举返回空但删除工具能找到同一张表」时，我们手上没有任何证据。
        /// 有了这几行，空清单至少能自证是哪一种。
        /// </summary>
        public sealed class TagTableWalkDiagnostics
        {
            public string RootGroupType { get; set; } = "";
            public bool TagTablesPropertyFound { get; set; }
            public string? TagTablesPropertyError { get; set; }
            public int GroupsVisited { get; set; }
            public int TablesFound { get; set; }
            public List<string> Notes { get; } = new List<string>();
        }

        private static void CollectTagTableNames(object group, string prefix, List<string> result,
            HashSet<object> visited, TagTableWalkDiagnostics? diag = null)
        {
            if (!visited.Add(group)) return;
            if (diag != null) diag.GroupsVisited++;

            // 直接反射一次，把「属性不存在」「读属性抛了」「读到了但是 null」分开记。
            // TryGetPropertyValue 会把这三种全折成 null —— 那正是空清单无法自证的根因。
            object? tables = null;
            if (diag != null && string.IsNullOrEmpty(prefix))
            {
                var prop = group.GetType().GetProperty("TagTables",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    diag.Notes.Add("The root group has no TagTables property (type=" + group.GetType().Name + ")");
                }
                else
                {
                    diag.TagTablesPropertyFound = true;
                    try { tables = prop.GetValue(group); }
                    catch (Exception ex)
                    {
                        diag.TagTablesPropertyError = ex.GetBaseException().Message;
                        diag.Notes.Add("Reading TagTables threw an exception: " + diag.TagTablesPropertyError);
                    }
                    if (tables == null && diag.TagTablesPropertyError == null)
                        diag.Notes.Add("The TagTables property exists but returned null");
                }
            }
            else
            {
                tables = TryGetPropertyValue(group, "TagTables");
            }

            if (tables is IEnumerable tEnum and not string)
            {
                foreach (var t in tEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    if (name.Length == 0) continue;
                    result.Add(string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name);
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
                    CollectTagTableNames(sub, next, result, visited, diag);
                }
            }
        }

        /// <summary>Walks the same tree as <see cref="CollectTagTableNames"/>, matching a table on
        /// either its bare name or the group-qualified path that walk would have produced.</summary>
        private static object? FindTagTable(object group, string prefix, string wanted, HashSet<object> visited)
        {
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
                        return t;
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
                    var hit = FindTagTable(sub, next, wanted, visited);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var plc = ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'" + AvailablePlcPathsSuffix());

            try
            {
                object root = TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                // TagTables collection lives on group
                var tables = TryGetPropertyValue(group, "TagTables") ?? TryGetPropertyValue(root, "TagTables");
                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. plcType={plc.GetType().FullName} groupType={group.GetType().FullName}");

                // Route through PrepareXmlForImport so the hardcoded <Engineering version="V21"/>
                // header is rewritten to the connected portal version (and a UTF-8 BOM is ensured).
                // Without this, tag-table imports fail on a V20 portal with
                // "The engineering version 'V21' ... is not supported." (block/type imports already
                // sanitize via PrepareXmlForImport; tag tables previously skipped it).
                if (TryImportEngineeringObjectIntoCollection(tables, PrepareXmlForImport(importPath), out _, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportPlcTagTable failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        private static object? TryInvokeMethodByName(object target, string methodName, params object?[] args)
        {
            try
            {
                var method = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
                return method?.Invoke(target, args);
            }
            catch { /* swallow(probe-optional): Missing or unsupported reflective methods return null to the existing caller fallback. */ return null; }
        }

        #endregion
    }
}
