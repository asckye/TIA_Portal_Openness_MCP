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

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HmiDescribeService
    {
        private readonly IEngineeringSession _session;

        public HmiDescribeService(IEngineeringSession session) => _session = session;

        #region software - HmiDescribe

        public (string? Name, string ProgramType, List<string> Screens)? GetHmiProgramInfo(string softwarePath)
        {
            _session.Logger?.LogInformation($"Getting HMI program info by path: {softwarePath}");

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
            if (_session.IsProjectNull())
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

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
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
                Members = _session.DescribeMembers(sw, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreen(string softwarePath, string screenName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
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

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{screenName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
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
                Members = _session.DescribeMembers(screen, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTagTable(string softwarePath, string tagTableName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
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

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = softwareContainer.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
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
                Members = _session.DescribeMembers(table, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTag(string softwarePath, string tagTableName, string tagName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
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

            var sc = _session.GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = sc.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Tag table not found: {$"{softwarePath}:{tagTableName}:{tagName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
            if (tagsComp == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
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
                Members = _session.DescribeMembers(tagObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreenItem(string softwarePath, string screenName, string itemName, int maxMembers = 200)
        {
            if (_session.IsProjectNull())
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

            var sc = _session.GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var sw = sc.Software;
            var screen = HmiScreenTraversal.FindByName(sw, screenName);
            if (screen == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Screen not found: {$"{softwarePath}:{screenName}:{itemName}"}. Resolve the exact path first "
                    + "(GetProjectTree / GetDevices / GetHmiScreens / GetHmiTagTables).");
            }

            var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
            if (itemsComp == null)
            {
                // 路径解析失败必须报错，不能返回表示空成员表的成功响应。
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
                Members = _session.DescribeMembers(itemObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        #endregion
    }
}
