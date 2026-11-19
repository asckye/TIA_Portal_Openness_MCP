using System;
using TiaMcp.Adapters.Native.Engineering;
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
            foreach (var member in Enum.GetValues(permissions.GetType()).Cast<object>()) {
                long bit = Convert.ToInt64(member); if (bit != 0 && (value & bit) == bit) names.Add(member.ToString()!); }
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

        public SecurityResult ManageDeviceUsers(string[] devicePathJson, string[] itemPathJson, string family, string action = "read", string userName = "",
                string password = "",
            string permissionsJson = "[]", bool active = true, string newName = "", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageDeviceUsers", meta => {
                var permissionNames = Permissions();
                _session.Validate("user");
                if (action == "delete") _session.Validate("confirmation");
                bool write = action != "read" && !dryRun;
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["family"] = family; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = _session
                    .HardwareOwnerPath(owner);
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                object Composition(HardwareObject hardware) => family switch
                {
                    "webserver" => _session.RequireHardwareService<WebserverUserManagement>(hardware, "itemPathJson").WebserverUsers,
#if SECURITY_SIMPLE_WEB
                    "simpleWebserver" => _session.RequireHardwareService<SimpleWebserverUserManagement>(hardware, "itemPathJson").WebserverUsers,
#endif
                    _ => _session.RequireHardwareService<OpcUaUserManagement>(hardware, "itemPathJson").OpcUaUsers
                };
                Func<object> fresh = action is "create" or "setPermissions"
                    ? () => Composition(_session.ExactEngineeringHardware(devicePathJson, itemPathJson)) : () => Composition(owner);
                object composition = Composition(owner);
                var users = HardwareGroupOperations.Items(composition).ToArray(); meta["countBefore"] = users.Length;
                if (action == "read")
                {
                    meta["records"] = new HmiArray(users.Select(u => (object?)UserRow(u)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["scope"] =
                        "UserName, Permissions (flag names) and Active; passwords are never readable.";
                    return "Device users read; no modification.";
                }
                object? user = users
                    .FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal));
                if ((action == "create") == (user != null))
                    throw new HardwareAddressingException(action == "create" ? "AlreadyExists" : "NotFound", action == "create" ? "User exists: " + userName
                            : "User not found: " + userName);
                if (user != null) meta["before"] = UserRow(user);
                string joined = permissionNames.Length == 0 ? "None" : string.Join(", ", permissionNames);
                meta["requestedPermissions"] = new HmiArray(permissionNames.Select(x => (object?)x).ToArray());
                if (!write) return "Device user " + action
                    + " preview; nothing changed (TIA refuses create/delete/setPassword while the web server or OPC UA authentication is disabled).";
                meta["mayHaveChanged"] = action is not ("create" or "setPermissions");
                object?[]? Fingerprint()
                {
                    var rows = HardwareGroupOperations.Items(fresh()).Select(u => {
                        string name = (string?)u.GetType().GetProperty("UserName")?.GetValue(u)
                            ?? throw new InvalidOperationException("Missing user identity.");
                        object? permissions = null;
                        if (name == userName && family != "opcUa")
                            permissions = u.GetType().GetProperty("Permissions")?.GetValue(u)
                                ?? throw new InvalidOperationException("Missing user permissions.");
                        return (name, permissions);
                    }).ToArray();
                    if (rows.Count(row => row.name == userName) != (action == "create" ? 0 : 1)) return null;
                    return rows.OrderBy(row => row.name, StringComparer.Ordinal).SelectMany(row => new[] { row.name, row.permissions }).ToArray();
                }
                switch (action)
                {
                    case "create":
                        using (var secure = EngineeringCredentialRules.ToSecureString(password))
                        {
                            if (family == "webserver")
                            {
                                var flags = (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined);
                                NativeRefusalProbe.Run(meta, Fingerprint, () => user = ((WebserverUserComposition)composition).Create(userName, flags, secure));
                            }
                            else NativeRefusalProbe.Run(meta, Fingerprint, () => user = ((OpcUaUserComposition)composition).Create(userName, secure));
                        }
                        meta["createdName"] = user!.GetType().GetProperty("UserName")?.GetValue(user)?.ToString();
                        if (_session.CountOnFresh(fresh, meta, "countAfter") is int grown && grown != users.Length + 1)
                            throw new InvalidOperationException("Create returned but the refreshed user count did not grow by one.");
                        break;
                    case "delete":
                        HardwareGroupOperations.Call(user!, "Delete", Type.EmptyTypes);
                        if (FindUserOnFresh(fresh, userName, meta) != null)
                            throw new InvalidOperationException("Delete returned but the user is still listed on the refreshed composition; do not blindly " +
                            "retry.");
                        meta["verifiedAbsent"] = true; _session.CountOnFresh(fresh, meta, "countAfter");
                        return "Device user deleted and count verified. No save/compile/download.";
                    case "setPassword":
                        using (var secure = EngineeringCredentialRules.ToSecureString(password))
                            if (!EngineeringCredentialRules
                                    .TrySetPassword(() => HardwareGroupOperations
                                        .Call(user!, "SetPassword", new[] { typeof(System.Security.SecureString) }, secure), meta))
                                return EngineeringCredentialRules.PasswordPolicyRefusal;
                        meta["passwordReadbackPossible"] = false;
                        break;
                    case "setPermissions":
                        EngineeringWriteJournal.Property(user!, user!.GetType().GetProperty("Permissions")!,
                            () => HardwareGroupOperations.Items(fresh()).Single(u =>
                                string.Equals((string?)u.GetType().GetProperty("UserName")?.GetValue(u), userName, StringComparison.Ordinal)));
                        if (user is WebserverUser web)
                        {
                            var flags = (WebserverUserPermissions)Enum.Parse(typeof(WebserverUserPermissions), joined);
                            NativeRefusalProbe.Run(meta, Fingerprint, () => web.Permissions = flags);
                        }
#if SECURITY_SIMPLE_WEB
                        else
                        {
                            var simple = (SimpleWebserverUser)user!;
                            var flags = (SimpleWebserverUserPermissions)Enum.Parse(typeof(SimpleWebserverUserPermissions), joined);
                            NativeRefusalProbe.Run(meta, Fingerprint, () => simple.Permissions = flags);
                        }
#else
                        else throw new NotSupportedException("Simple web users require V18 or later.");
#endif
                        break;
#if SECURITY_SIMPLE_WEB
                    case "setActive":
                        ((SimpleWebserverUser)user!).Active = active;
                        if (((SimpleWebserverUser)user).Active != active) throw new InvalidOperationException("Active readback differs.");
                        break;
                    case "rename":
                        ((SimpleWebserverUser)user!).UserName = newName;
                        if (((SimpleWebserverUser)user).UserName != newName) throw new InvalidOperationException("UserName readback differs.");
                        break;
#endif
                }
                meta["after"] = UserRow(user!); if (meta["countAfter"] == null) _session.CountOnFresh(fresh, meta, "countAfter");
                return "Device user operation completed and read back (password never echoed). No save/compile/download.";
            });

        private static object? FindUserOnFresh(Func<object> composition, string userName, HmiObject meta)
        {
            try {
                return HardwareGroupOperations.Items(composition())
                    .FirstOrDefault(u => string.Equals(u.GetType().GetProperty("UserName")?.GetValue(u)?.ToString(), userName, StringComparison.Ordinal));
            }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex)) { meta["postDeleteEnumeration"] = ex.GetBaseException().Message; return null; }
        }
        private string[] Permissions() { _session.Validate("permissions"); return request.Permissions; }
    }
#endif
}
