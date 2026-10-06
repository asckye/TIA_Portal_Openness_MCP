using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TiaOpenness.Shared
{
    /// <summary>Read and configure the Workbench's one TCP rule through the Windows Firewall COM API.</summary>
    public static class WindowsFirewallRule
    {
        public static bool Matches(bool enabled, int direction, int action, int protocol, string localPorts, string localAddresses,
            string expectedPort, string expectedAddress)
        {
            return enabled && direction == 1 && action == 1 && protocol == 6 &&
                ContainsExact(localPorts, expectedPort) && ContainsExact(localAddresses, expectedAddress);
        }

        /// <summary>
        /// The COM API exposes a rule's display name as Name. Earlier releases created the rule with New-NetFirewallRule, whose rule id
        /// (TIA-MCP-address-port) is not visible here and whose display name is "TIA MCP TCP port", so that rule is matched by its
        /// display name and local address instead.
        /// </summary>
        public static bool IsSameRule(string ruleName, string localAddresses, string name, string legacyDisplayName, string address)
        {
            return string.Equals(ruleName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ruleName, legacyDisplayName, StringComparison.OrdinalIgnoreCase) && ContainsExact(localAddresses, address);
        }

        public static string LegacyDisplayName(int port) => "TIA MCP TCP " + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool? IsAllowed(string name, string address, int port)
        {
            object? policy = null, rules = null, rule = null;
            try
            {
                policy = Create("HNetCfg.FwPolicy2");
                rules = Get(policy, "Rules");
                rule = Find(rules, name, LegacyDisplayName(port), address);
                if (rule == null) return false;
                return Matches(Convert.ToBoolean(Get(rule, "Enabled")), Convert.ToInt32(Get(rule, "Direction")),
                    Convert.ToInt32(Get(rule, "Action")), Convert.ToInt32(Get(rule, "Protocol")),
                    Convert.ToString(Get(rule, "LocalPorts")) ?? "", Convert.ToString(Get(rule, "LocalAddresses")) ?? "",
                    port.ToString(System.Globalization.CultureInfo.InvariantCulture), address);
            }
            catch (Exception) /* swallow(env-probe): a firewall COM/API failure leaves rule status unknown to diagnostics */ { return null; }
            finally { Release(rule); Release(rules); Release(policy); }
        }

        public static void Configure(string name, string displayName, string address, int port)
        {
            object? policy = null, rules = null, rule = null;
            try
            {
                policy = Create("HNetCfg.FwPolicy2");
                rules = Get(policy, "Rules");
                rule = Find(rules, name, LegacyDisplayName(port), address);
                bool exists = rule != null;
                rule = rule ?? Create("HNetCfg.FWRule");
                Set(rule, "Name", name);
                Set(rule, "Description", displayName);
                Set(rule, "ApplicationName", "");
                Set(rule, "ServiceName", "");
                Set(rule, "Protocol", 6); // NET_FW_IP_PROTOCOL_TCP
                Set(rule, "LocalPorts", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Set(rule, "LocalAddresses", address);
                Set(rule, "RemoteAddresses", "LocalSubnet");
                Set(rule, "Direction", 1); // NET_FW_RULE_DIR_IN
                Set(rule, "Profiles", -1); // NET_FW_PROFILE2_ALL
                Set(rule, "Action", 1); // NET_FW_ACTION_ALLOW
                Set(rule, "Enabled", true);
                if (!exists) Invoke(rules, "Add", rule);
            }
            finally { Release(rule); Release(rules); Release(policy); }
        }

        private static object Create(string progId)
        {
            var type = Type.GetTypeFromProgID(progId, true)!;
            return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Firewall COM API could not create " + progId);
        }

        private static object? Find(object rules, string name, string legacyDisplayName, string address)
        {
            foreach (object candidate in (IEnumerable)rules)
            {
                bool matches;
                try
                {
                    matches = IsSameRule(Convert.ToString(Get(candidate, "Name")) ?? "", Convert.ToString(Get(candidate, "LocalAddresses")) ?? "",
                        name, legacyDisplayName, address);
                }
                catch { Release(candidate); throw; }
                if (matches) return candidate;
                Release(candidate);
            }
            return null;
        }

        private static object? Get(object target, string property) => target.GetType().InvokeMember(property,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null);

        private static void Set(object target, string property, object value) => target.GetType().InvokeMember(property,
            BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, new[] { value });

        private static object? Invoke(object target, string method, params object[] values) => target.GetType().InvokeMember(method,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, target, values);

        private static bool ContainsExact(string list, string value)
        {
            foreach (string item in list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (item.Trim().Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Release(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
}
