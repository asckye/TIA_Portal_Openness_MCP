using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters.Native.Security
{
#if SECURITY_DEVICE_USERS
    internal sealed class DeviceUsersAdapter
    {
        private readonly SecuritySession _session;
        private readonly DeviceUserRequest request;
        public DeviceUsersAdapter(SecuritySession session, DeviceUserRequest request) { _session = session; this.request = request; }
        private static HmiArray PermissionNames(object? permissions)
        {
            if (permissions == null || !permissions.GetType().IsEnum) return new HmiArray();
            long value = Convert.ToInt64(permissions); var names = new List<string>();
            foreach (var member in Enum.GetValues(permissions.GetType()).Cast<object>()) { long bit = Convert.ToInt64(member); if (bit != 0 && (value & bit) == bit) names.Add(member.ToString()!); }
            if (names.Count == 0) names.Add(Enum.GetName(permissions.GetType(), Enum.ToObject(permissions.GetType(), 0)) ?? "None");
            return new HmiArray(names.Select(n => (object?)n).ToArray());
        }

        private static HmiObject UserRow(object user)
        {
            var row = HmiScalar.Read(user);
            var permissions = user.GetType().GetProperty("Permissions")?.GetValue(user);
            if (permissions != null) row["permissionNames"] = PermissionNames(permissions);
            return row;
        }

        public SecurityResult ManageDeviceUsers(string[] devicePathJson, string[] itemPathJson, string family, string action = "read", string userName = "", string password = "",
            string permissionsJson = "[]", bool active = true, string newName = "", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageDeviceUsers", meta => {
                var permissionNames = Permissions();
                _session.Validate("user");
                if (action == "delete") _session.Validate("confirmation");
                bool write = action != "read" && !dryRun;
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["family"] = family; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = _session.HardwareOwnerPath(owner);
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                Func<object> fresh = family switch
                {
                    "webserver" => () => _session.RequireHardwareService<WebserverUserManagement>(owner, "itemPathJson").WebserverUsers,
#if SECURITY_SIMPLE_WEB
                    "simpleWebserver" => () => (object)_session.RequireHardwareService<SimpleWebserverUserManagement>(owner, "itemPathJson").WebserverUsers,
#endif
                    _ => () => (object)_session.RequireHardwareService<OpcUaUserManagement>(owner, "itemPathJson").OpcUaUsers
                };
                object composition = fresh();
                var users = HardwareGroupOperations.Items(composition).ToArray(); meta["countBefore"] = users.Length;
                if (action == "read")
                {
                    meta["records"] = new HmiArray(users.Select(u => (object?)UserRow(u)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["scope"] = "UserName, Permissions (flag names) and Active; passwords are never readable.";
                    return "Device users read; no modification.";
                }
                object? user = users.FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal));
                if ((action == "create") == (user != null)) throw new InvalidOperationException(action == "create" ? "User exists: " + userName : "User not found: " + userName);
                if (user != null) meta["before"] = UserRow(user);
                string joined = permissionNames.Length == 0 ? "None" : string.Join(", ", permissionNames);
                meta["requestedPermissions"] = new HmiArray(permissionNames.Select(x => (object?)x).ToArray());
                if (!write) return "Device user " + action + " preview; nothing changed (TIA refuses create/delete/setPassword while the web server or OPC UA authentication is disabled).";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create":
                        using (var secure = EngineeringCredentialRules.ToSecureString(password))
                            user = family == "webserver"
                                ? ((WebserverUserComposition)composition).Create(userName, (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined), secure)
                                : (object)((OpcUaUserComposition)composition).Create(userName, secure);
                        meta["createdName"] = user.GetType().GetProperty("UserName")?.GetValue(user)?.ToString();
                        if (_session.CountOnFresh(fresh, meta, "countAfter") is int grown && grown != users.Length + 1) throw new InvalidOperationException("Create returned but the refreshed user count did not grow by one.");
                        break;
                    case "delete":
                        HardwareGroupOperations.Call(user!, "Delete", Type.EmptyTypes);
                        if (FindUserOnFresh(fresh, userName, meta) != null) throw new InvalidOperationException("Delete returned but the user is still listed on the refreshed composition; do not blindly retry.");
                        meta["verifiedAbsent"] = true; _session.CountOnFresh(fresh, meta, "countAfter");
                        return "Device user deleted and count verified. No save/compile/download.";
                    case "setPassword":
                        using (var secure = EngineeringCredentialRules.ToSecureString(password)) HardwareGroupOperations.Call(user!, "SetPassword", new[] { typeof(System.Security.SecureString) }, secure);
                        meta["passwordReadbackPossible"] = false;
                        break;
                    case "setPermissions":
                        if (user is WebserverUser web) web.Permissions = (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined);
#if SECURITY_SIMPLE_WEB
                        else ((SimpleWebserverUser)user!).Permissions = (SimpleWebserverUserPermissions)Enum.Parse(typeof(SimpleWebserverUserPermissions), joined);
                        break;
                    case "setActive":
                        ((SimpleWebserverUser)user!).Active = active;
                        if (((SimpleWebserverUser)user).Active != active) throw new InvalidOperationException("Active readback differs.");
                        break;
                    case "rename":
                        ((SimpleWebserverUser)user!).UserName = newName;
                        if (((SimpleWebserverUser)user).UserName != newName) throw new InvalidOperationException("UserName readback differs.");
                        break;
#else
                        else throw new NotSupportedException("Simple web users require V18 or later.");
                        break;
#endif
                }
                meta["after"] = UserRow(user!); if (meta["countAfter"] == null) _session.CountOnFresh(fresh, meta, "countAfter");
                return "Device user operation completed and read back (password never echoed). No save/compile/download.";
            });

        private static object? FindUserOnFresh(Func<object> composition, string userName, HmiObject meta)
        {
            try { return HardwareGroupOperations.Items(composition()).FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal)); }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex)) { meta["postDeleteEnumeration"] = ex.GetBaseException().Message; return null; }
        }
        private string[] Permissions() { _session.Validate("permissions"); return request.Permissions; }
    }
#endif
}
