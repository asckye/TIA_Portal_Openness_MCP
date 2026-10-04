using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ObjectIdentityRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        // ---- object identifiers / show in editor ------------------------------------------------------------------------------
        internal static readonly string[] ObjectKinds = { "device", "deviceItem", "plcBlock", "plcType", "plcTagTable" };
        internal static void ValidateObjectSelection(string kind, string devicePathJson, string itemPathJson, string softwarePath, string objectPath, string identifier)
        {
            RequireOneOf(kind, ObjectKinds, "kind");
            if (!string.IsNullOrEmpty(identifier)) { if (identifier.Length > 4096) throw new ArgumentException("identifier too long."); return; }
            if (kind == "device" || kind == "deviceItem") { ProjectSecurityLogic.ValidateDevicePath(devicePathJson); if (kind == "deviceItem" && (string.IsNullOrWhiteSpace(itemPathJson) || itemPathJson.Trim() == "[]")) throw new ArgumentException("kind=deviceItem needs a non-empty itemPathJson."); }
            else { RequireName(softwarePath, "softwarePath"); RequireName(objectPath, "objectPath", 1024); }
        }
    }
}
