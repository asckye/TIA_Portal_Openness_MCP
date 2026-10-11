using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcp.Adapters.Native.Hmi.HmiAccessHelpers;
#if HMI_UNIFIED
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
#endif
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
using System.Xml.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiDescribeAdapter
    {
        private readonly HmiSession _session;

        public HmiDescribeAdapter(HmiSession session) => _session = session;

        #region software - HmiDescribe

        public HmiProgramInfo? GetHmiProgramInfo(string softwarePath)
        {


            if (_session.IsProjectNull())
            {
                return null;
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return null;
            }

            var sw = softwareContainer.Software;

            // Classic WinCC (HmiTarget)
            if (sw is HmiTarget classic)
            {
                return new HmiProgramInfo { Name = classic.Name, ProgramType = "Classic", Screens = HmiScreenTraversal.ListNames(classic) };
            }

            // Unified (HmiSoftware)
#if HMI_UNIFIED
            if (sw is HmiSoftware unified)
            {
                return new HmiProgramInfo { Name = unified.Name, ProgramType = "Unified", Screens = HmiScreenTraversal.ListNames(unified) };
            }
#endif

            return new HmiProgramInfo { Name = sw.ToString(), ProgramType = "Unknown", Screens = new List<string>() };
        }

        public HmiDescribe DescribeHmiSoftware(string softwarePath, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
            {
                return new HmiDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "Software",
                    ObjectPath = softwarePath,
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"HMI software not found: {softwarePath}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            return new HmiDescribe
            {
                Message = "OK",
                ObjectKind = "Software",
                ObjectPath = softwarePath,
                TypeName = sw.GetType().FullName ?? sw.GetType().Name,
                Members = _session.DescribeMembers(sw, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public HmiDescribe DescribeHmiScreen(string softwarePath, string screenName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
            {
                return new HmiDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreen",
                    ObjectPath = $"{softwarePath}:{screenName}",
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"HMI software not found: {$"{softwarePath}:{screenName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Screen not found: {$"{softwarePath}:{screenName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            return new HmiDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreen",
                ObjectPath = $"{softwarePath}:{screenName}",
                TypeName = screen.GetType().FullName ?? screen.GetType().Name,
                Members = _session.DescribeMembers(screen, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public HmiDescribe DescribeHmiTagTable(string softwarePath, string tagTableName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
            {
                return new HmiDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTagTable",
                    ObjectPath = $"{softwarePath}:{tagTableName}",
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Tag table not found: {$"{softwarePath}:{tagTableName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            return new HmiDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTagTable",
                ObjectPath = $"{softwarePath}:{tagTableName}",
                TypeName = table.GetType().FullName ?? table.GetType().Name,
                Members = _session.DescribeMembers(table, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public HmiDescribe DescribeHmiTag(string softwarePath, string tagTableName, string tagName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
            {
                return new HmiDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var sc = _session.GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var sw = sc.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Tag table not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
            if (tagsComp == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"tagTable.Tags not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            object? tagObj = null;
            try
            {
                if (tagsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = _session.TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            tagObj = it;
                            break;
                        }
                    }
                }
            }
            catch { /* swallow(enumerate-optional): unavailable collection enumeration falls through to the existing NotFound diagnostic */ }

            if (tagObj == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Tag not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            return new HmiDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTag",
                ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                TypeName = tagObj.GetType().FullName ?? tagObj.GetType().Name,
                Members = _session.DescribeMembers(tagObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public HmiDescribe DescribeHmiScreenItem(string softwarePath, string screenName, string itemName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
            {
                return new HmiDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var sc = _session.GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"HMI software not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var sw = sc.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Screen not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
            if (itemsComp == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"screen.ScreenItems not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            object? itemObj = null;
            try
            {
                if (itemsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = _session.TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                        {
                            itemObj = it;
                            break;
                        }
                    }
                }
            }
            catch { /* swallow(enumerate-optional): unavailable collection enumeration falls through to the existing NotFound diagnostic */ }

            if (itemObj == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PlcSoftwareException("NotFound",
                    $"Screen item not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / ListDevices / ListHmiScreens / ListHmiTagTables).");
            }

            return new HmiDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreenItem",
                ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                TypeName = itemObj.GetType().FullName ?? itemObj.GetType().Name,
                Members = _session.DescribeMembers(itemObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        #endregion
    }
}
