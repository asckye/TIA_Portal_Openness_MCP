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
    // Partial: software. Family file split out of Portal.Software.cs (2.8.0); behavior unchanged.
    public partial class Portal
    {
        #region software - HmiDescribe

        public (string? Name, string ProgramType, List<string> Screens)? GetHmiProgramInfo(string softwarePath)
        {
            _logger?.LogInformation($"Getting HMI program info by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return null;
            }

            var sw = softwareContainer.Software;

            // Classic WinCC (HmiTarget)
            if (sw is HmiTarget classic)
            {
                return (classic.Name, "Classic", HmiScreenTraversal.ListNames(classic));
            }

            // Unified (HmiSoftware)
            if (sw is HmiSoftware unified)
            {
                return (unified.Name, "Unified", HmiScreenTraversal.ListNames(unified));
            }

            return (sw.ToString(), "Unknown", new List<string>());
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiSoftware(string softwarePath, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "Software",
                    ObjectPath = softwarePath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {softwarePath}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "Software",
                ObjectPath = softwarePath,
                TypeName = sw.GetType().FullName ?? sw.GetType().Name,
                Members = DescribeMembers(sw, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreen(string softwarePath, string screenName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreen",
                    ObjectPath = $"{softwarePath}:{screenName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{screenName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Screen not found: {$"{softwarePath}:{screenName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreen",
                ObjectPath = $"{softwarePath}:{screenName}",
                TypeName = screen.GetType().FullName ?? screen.GetType().Name,
                Members = DescribeMembers(screen, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTagTable(string softwarePath, string tagTableName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTagTable",
                    ObjectPath = $"{softwarePath}:{tagTableName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag table not found: {$"{softwarePath}:{tagTableName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTagTable",
                ObjectPath = $"{softwarePath}:{tagTableName}",
                TypeName = table.GetType().FullName ?? table.GetType().Name,
                Members = DescribeMembers(table, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTag(string softwarePath, string tagTableName, string tagName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sc = GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = sc.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag table not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
            if (tagsComp == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"tagTable.Tags not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            object? tagObj = null;
            try
            {
                if (tagsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            tagObj = it;
                            break;
                        }
                    }
                }
            }
            catch { }

            // 原来这里还有一次 TryFindByNameInCollection(tagsComp, Array.Empty<string>(), tagName) 的"兜底"，
            // 该方法只遍历 propertyHints，空数组＝循环体一次不进＝恒返回 null，纯死代码，已删。

            if (tagObj == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTag",
                ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                TypeName = tagObj.GetType().FullName ?? tagObj.GetType().Name,
                Members = DescribeMembers(tagObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreenItem(string softwarePath, string screenName, string itemName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sc = GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = sc.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Screen not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
            if (itemsComp == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"screen.ScreenItems not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            object? itemObj = null;
            try
            {
                if (itemsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                        {
                            itemObj = it;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (itemObj == null)
            {
                // 「找不到」返回一条正常响应 + 空成员表，调用方看到的是 isError=false，
                // 会把「我路径写错了」记成「这个对象确实没有任何成员」。Describe 系工具正是
                // 用来摸索路径的，摸错必须响。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Screen item not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreenItem",
                ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                TypeName = itemObj.GetType().FullName ?? itemObj.GetType().Name,
                Members = DescribeMembers(itemObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        #endregion
    }
}
