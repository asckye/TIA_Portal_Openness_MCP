using System;
using System.Collections;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Reflection;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Action<string?>? UpdateEngineeringReflectionLookupPaths { get; set; }
        public Func<string, string, bool, object?>? ResolveEngineeringUnifiedScripts { get; set; }
        public Func<Exception, PlcSoftwareFailure>? EngineeringReflectionFailure { get; set; }
        private string? reflectionLookupPaths;
        private void SetReflectionLookupPaths(string? value)
        { reflectionLookupPaths = value; UpdateEngineeringReflectionLookupPaths?.Invoke(value); }
        private object? ResolveReflectionObject(string objectKind, string objectPath, string softwarePath, bool write)
        {
            SetReflectionLookupPaths(null);
            if (EngineeringPlcSession.IsProjectNull()) return null;

            switch ((objectKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "project":
                    return project;

                case "portal":
                    return portal;

                case "device":
                    return HardwareResolveDevice(objectPath);

                case "deviceitem":
                case "device_item":
                case "device-item":
                    return HardwareResolveItem(objectPath);

                case "software":
                case "plcsoftware":
                case "plc_software":
                case "plc-software":
                {
                    // PLC 优先
                    PlcSoftware? plcsw;
                    try { plcsw = EngineeringPlcSession.ResolvePlc(objectPath, write); }
                    catch (PlcSoftwareException error) when ((EngineeringPlcOrganisationSession == null || EngineeringPlcOrganisationSession is OrganisationSession) && error.Code == "NotFound") /* swallow(probe-optional): exact PLC lookup retains the optional HMI fallback */
                    { plcsw = null; }
                    if (plcsw != null) return plcsw;
                    var plcPaths = EngineeringReflectionLookupPaths != null && EngineeringPlcOrganisationSession is not OrganisationSession
                        ? EngineeringReflectionLookupPaths() : EngineeringPlcSession.AvailablePlcPathsSuffix();
                    // HMI 兜底（Unified / Classic），让 DescribeObject/InvokeObject 也能操作 HMI
                    try
                    {
                        var sc = EngineeringPlcSession.GetSoftwareContainer(objectPath);
                        if (sc?.Software is { } software && software is not PlcSoftware) return sc.Software;
                    }
                    catch /* swallow(probe-optional): the HMI fallback is optional; a failed probe keeps the PLC not-found result and its path hint */ { }
                    SetReflectionLookupPaths(plcPaths);
                    return null;
                }

                case "hmi":
                case "hmisoftware":
                case "hmi_software":
                case "hmi-software":
                {
                    try
                    {
                        var sc = EngineeringPlcSession.GetSoftwareContainer(objectPath);
                        if (sc?.Software != null) return sc.Software;
                    }
                    catch { /* swallow(probe-optional): An unavailable HMI software container leaves this optional reflection target unresolved. */ }
                    return null;
                }

                case "block":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return EngineeringPlcSession.GetBlock(softwarePath, objectPath);

                case "type":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return EngineeringPlcSession.GetType(softwarePath, objectPath);

                case "hmiscriptmodule":
                case "hmi_script_module":
                case "hmi-script-module":
                    return ResolveReflectionScripts(objectPath, softwarePath, false);

                case "hmiscripts":
                case "hmi_scripts":
                case "hmi-scripts":
                    return ResolveReflectionScripts(objectPath, softwarePath, true);

                case "hmiscreen":
                case "hmi_screen":
                case "hmi-screen":
                {
                    // objectPath: "HMI_RT_1:Main"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var sc = EngineeringPlcSession.GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return ReflectionScreenTraversal.FindByName(sc.Software, screenName);
                }

                case "hmitagtable":
                case "hmi_tagtable":
                case "hmi-tagtable":
                {
                    // objectPath: "HMI_RT_1:默认变量表"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var sc = EngineeringPlcSession.GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return ReflectionScreenTraversal.FindTagTable(sc.Software, tableName);
                }

                case "hmitag":
                case "hmi_tag":
                case "hmi-tag":
                {
                    // objectPath: "HMI_RT_1:默认变量表:StartPB"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var tagName = parts[2];
                    var sc = EngineeringPlcSession.GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var table = ReflectionScreenTraversal.FindTagTable(sc.Software, tableName);
                    if (table == null) return null;
                    var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
                    if (tagsComp == null) return null;
                    try
                    {
                        if (tagsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = LibraryReflectionValues.TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { /* swallow(enumerate-optional): An unreadable HMI tag collection leaves the reflection target unresolved. */ }
                    return null;
                }

                case "hmiconnection":
                case "hmi_connection":
                case "hmi-connection":
                {
                    // objectPath: "HMI_RT_1:HMI_Connection_1"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var connectionName = parts[1];
                    var sc = EngineeringPlcSession.GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var conns = TryGetPropertyValue(sc.Software, "Connections");
                    if (conns == null) return null;
                    return FindExistingByName(conns, connectionName);
                }

                case "hmiscreenitem":
                case "hmi_screenitem":
                case "hmi-screenitem":
                case "hmiscreen_item":
                case "hmi_screen_item":
                case "hmi-screen-item":
                {
                    // objectPath: "HMI_RT_1:Main:BTN_Start"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var itemName = parts[2];
                    var sc = EngineeringPlcSession.GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    ReflectionScreenTraversal.RequireScreenItemsApi(sc.Software);
                    var screen = ReflectionScreenTraversal.FindByName(sc.Software, screenName);
                    if (screen == null) return null;
                    var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
                    if (itemsComp == null) return null;
                    try
                    {
                        if (itemsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = LibraryReflectionValues.TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { /* swallow(enumerate-optional): An unreadable screen-item collection leaves the reflection target unresolved. */ }
                    return null;
                }

                default:
                    return null;
            }
        }


        private object? ResolveReflectionScripts(string path, string software, bool collection)
        {
            if (ResolveEngineeringUnifiedScripts != null) return ResolveEngineeringUnifiedScripts(path, software, collection);
            if (string.IsNullOrWhiteSpace(software))
            {
                int colon = path.IndexOf(':');
                if (colon <= 0) throw new ArgumentException("Specify softwarePath; alternatively use HMI:Navigation or HMI:Scripts.");
                software = path.Substring(0, colon); path = path.Substring(colon + 1);
            }
            var hmi = EngineeringPlcSession.GetSoftwareContainer(software)?.Software;
            var scripts = TryGetPropertyValue(hmi, "Scripts") ?? throw new NotSupportedException("This HMI has no public Scripts composition.");
            if (collection)
            {
                if (path != "" && path != "Scripts" && path != "/Scripts") throw new ArgumentException("HmiScripts objectPath must be /Scripts (or empty) with an explicit softwarePath.");
                return scripts;
            }
            if (path.StartsWith("/Scripts/", StringComparison.Ordinal))
            {
                var segment = path.Substring(9);
                if (segment.Length == 0 || segment.IndexOf('/') >= 0) throw new ArgumentException("Use /Scripts/<URI-escaped exact module name>.");
                path = Uri.UnescapeDataString(segment);
            }
            else if (path.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Unsupported script path; use an exact module name or /Scripts/<name>.");
            return FindExistingByName(scripts, path);
        }
    }
}
