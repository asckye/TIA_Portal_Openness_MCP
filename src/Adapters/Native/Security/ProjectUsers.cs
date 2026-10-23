using System;
using TiaMcpServer.Siemens;
using System.Linq;
using System.Security;
using Siemens.Engineering;
using Siemens.Engineering.HW;
#if SECURITY_UMAC
using Siemens.Engineering.Umac;
using Siemens.Engineering.Settings;
using Siemens.Engineering.HW.Features;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters.Native.Security;
#if SECURITY_UMAC
internal sealed class ProjectUsers
{
    private readonly SecuritySession _session;
    internal ProjectUsers(SecuritySession session) => _session = session;
        // ---- UMAC helpers ----
        private T ExactUmacItem<T>(object collection, string name, string kind) where T : class
            => _session.FindOrdinal(collection, name, kind) as T ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + kind + " not found: " + name);
        // HardwareObject.GetService exists on V21 only; Device/DeviceItem expose it on both versions.
        private static T? HardwareService<T>(HardwareObject hardware) where T : class, IEngineeringService
            => hardware is Device device ? device.GetService<T>() : hardware is DeviceItem item ? item.GetService<T>() : null;
        private UmacDevice RequireUmacDevice(string[] devicePathJson, string[] itemPathJson)
            => HardwareService<UmacDevice>(_session.ExactEngineeringHardware(devicePathJson, itemPathJson))
               ?? throw new NotSupportedException("UmacDevice service unavailable on the selected hardware object. On the reference project it lives on the Device (devicePathJson only, itemPathJson=[]), not on the CPU DeviceItem; address the Device or the DeviceItem that owns the UMAC configuration.");
        public SecurityResult ReadProjectUserManagement(string category = "users", string name = "", string[] devicePathJson = null!, string[] itemPathJson = null!, int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetProjectUserManagement", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                _session.RequireUserAction();
                _session.Validate("category"); _session.Validate("page");
                if (SecurityRules.DeviceCategories.Contains(category)) _session.Validate("devicePath");
                if (category == "roleDeviceRights" && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("roleDeviceRights requires name = exact role name.");
                var umac = _session.RequireUmac();
                meta["category"] = category; meta["scope"] = "Public scalar properties plus role/right names. Passwords are never readable.";
                object[] items;
                switch (category)
                {
                    case "users": items = HardwareGroupOperations.Items(umac.ProjectUsers).ToArray(); break;
                    case "anonymousUser": items = umac.AnonymousUser == null ? Array.Empty<object>() : new object[] { umac.AnonymousUser }; break;
                    case "systemRoles": items = HardwareGroupOperations.Items(umac.SystemRoles).ToArray(); break;
                    case "customRoles": items = HardwareGroupOperations.Items(umac.CustomRoles).ToArray(); break;
                    case "engineeringRights": items = HardwareGroupOperations.Items(umac.EngineeringFunctionRights).ToArray(); break;
                    case "customDeviceRights": items = HardwareGroupOperations.Items(umac.CustomDeviceFunctionRights).ToArray(); break;
                    case "umcUsers": items = HardwareGroupOperations.Items(umac.UmcUsers).ToArray(); break;
                    case "umcUserGroups": items = HardwareGroupOperations.Items(umac.UmcUserGroups).ToArray(); break;
                    case "passwordPolicy":
#if SECURITY_POLICY
                        items = new object[] { _session.CurrentProject!.GetService<PasswordPolicyConfigurator>() ?? throw new NotSupportedException("PasswordPolicyConfigurator service unavailable on this project.") }; break;
#else
                        throw new NotSupportedException("PasswordPolicyConfigurator requires V18 or later.");
#endif
                    case "deviceRights": items = HardwareGroupOperations.Items(RequireUmacDevice(devicePathJson, itemPathJson).AvailableDeviceFunctionRights).ToArray(); break;
                    default: // roleDeviceRights: name = exact role
                        var role = _session.ExactUmacRole(umac, name); var device = RequireUmacDevice(devicePathJson, itemPathJson);
                        items = role is CustomRole customRole ? customRole.GetAssignedDeviceFunctionRights(device).Cast<object>().ToArray() : ((SystemRole)role).GetAssignedSystemDeviceFunctionRights(device).Cast<object>().ToArray();
                        meta["roleKind"] = role.GetType().Name; break;
                }
                if (!string.IsNullOrEmpty(name) && category != "roleDeviceRights") items = new[] { ExactUmacItem<object>(items, name, category) };
                var rows = new HmiArray(items.Skip(offset).Take(limit).Select(x => (object)_session.UmacRow(x)).ToArray());
                SecurityRules.PageMeta(meta, items.Length, offset, limit, rows.Count);
                meta["records"] = rows; meta["apiCallSuccess"] = true;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!.Field("dataComplete")!.GetValue<bool>());
                return "UMAC " + category + " read; scalar scope only, paginated.";
            });

        public SecurityResult ManageProjectUserManagement(string action, string name, string password = "", string roleName = "", string rightName = "", string comment = "", string group = "",
            string[] devicePathJson = null!, string[] itemPathJson = null!, bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageProjectUserManagement", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                var request = _session.UserPlan();
                bool writing = !dryRun;
                if (writing && !confirmChange) throw new ArgumentException("Real UMAC changes require confirmChange=true together with dryRun=false.");
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var umac = _session.RequireUmac();
                meta["action"] = action; meta["name"] = name; meta["dryRun"] = dryRun; meta["confirmChange"] = confirmChange; meta["mayHaveChanged"] = false;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                if (request.Target == "anonymousUser")
                {
                    // Official: AnonymousUser is null while deactivated; one anonymous user per protected project.
                    var anonymousBefore = umac.AnonymousUser; meta["before"] = anonymousBefore == null ? null : _session.UmacRow(anonymousBefore); meta["activeBefore"] = anonymousBefore?.IsActive ?? false;
                    if (dryRun) return "Anonymous user " + action + " preview; nothing written (deactivating it can lock out clients that rely on password-less access).";
                    meta["mayHaveChanged"] = true;
                    if (action == "activateAnonymousUser") umac.ActivateAnonymousUser(); else umac.DeactivateAnonymousUser();
                    meta["apiCallSuccess"] = true;
                    var anonymousAfter = _session.RequireUmac().AnonymousUser; meta["after"] = anonymousAfter == null ? null : _session.UmacRow(anonymousAfter); meta["activeAfter"] = anonymousAfter?.IsActive ?? false;
                    bool active = anonymousAfter?.IsActive ?? false;
                    if (active != (action == "activateAnonymousUser")) throw new InvalidOperationException("Native call returned but the anonymous user state did not change to the requested one.");
                    return "Anonymous user " + action + " executed and read back; no save.";
                }
                object collection = request.Target == "user" ? umac.ProjectUsers : request.Target == "role" ? umac.CustomRoles : umac.CustomDeviceFunctionRights;
                var existing = _session.FindOrdinal(collection, name, request.Target);
                if (request.Creates && existing != null) throw new InvalidOperationException("Exact " + request.Target + " already exists: " + name);
                if (!request.Creates && existing == null) throw new PortalException(PortalErrorCode.NotFound, "Exact " + request.Target + " not found: " + name + (request.Target == "role" ? " (only custom roles can be managed)" : ""));
                if (existing != null) meta["before"] = _session.UmacRow(existing);
                Role? role = request.NeedsRole ? _session.ExactUmacRole(umac, roleName) : null;
                EngineeringFunctionRight? engineeringRight = null; UmacDevice? device = null; DeviceFunctionRight? deviceRight = null;
                if (request.NeedsDevice) { device = RequireUmacDevice(devicePathJson, itemPathJson); deviceRight = ExactUmacItem<DeviceFunctionRight>(device.AvailableDeviceFunctionRights, rightName, "device function right"); }
                else if (request.NeedsRight) engineeringRight = ExactUmacItem<EngineeringFunctionRight>(umac.EngineeringFunctionRights, rightName, "engineering function right");
                if (dryRun) return "UMAC change preview; objects validated, nothing written.";
                meta["mayHaveChanged"] = true;
                SecureString? secure = request.NeedsPassword ? SecurityRules.Secure(password) : null;
                try
                {
                    var user = existing as ProjectUser; var customRole = existing as CustomRole;
                    ProjectUserComposition projectUsers = umac.ProjectUsers; CustomRoleComposition customRoles = umac.CustomRoles; CustomDeviceFunctionRightComposition deviceRights = umac.CustomDeviceFunctionRights;
                    RoleAssociation? userRoles = user?.Roles; EngineeringFunctionRightAssociation? engineeringRights = customRole?.AssignedEngineeringRights;
                    switch (action)
                    {
                        case "createUser": projectUsers.Create(name, secure!); break;
                        case "deleteUser": user!.Delete(); break;
                        case "setUserPassword":
                            if (!EngineeringCredentialRules.TrySetPassword(() => user!.SetPassword(secure!), meta))
                                return EngineeringCredentialRules.PasswordPolicyRefusal;
                            break;
                        case "activateUser": user!.Activate(); break;
                        case "deactivateUser": user!.Deactivate(); break;
                        case "assignRole": userRoles!.Add(role!); break;
                        case "unassignRole": if (!userRoles!.Remove(role!)) throw new InvalidOperationException("RoleAssociation.Remove returned false; the role was not assigned."); break;
                        case "createRole": customRoles.Create(name, comment); break;
                        case "deleteRole": customRole!.Delete(); break;
                        case "assignEngineeringRight": engineeringRights!.Add(engineeringRight!); break;
                        case "unassignEngineeringRight": if (!engineeringRights!.Remove(engineeringRight!)) throw new InvalidOperationException("EngineeringFunctionRightAssociation.Remove returned false; the right was not assigned."); break;
                        case "assignDeviceRight": customRole!.AssignDeviceFunctionRight(device!, deviceRight!); break;
                        case "unassignDeviceRight": customRole!.UnAssignDeviceFunctionRight(device!, deviceRight!); break;
                        case "createDeviceRight": deviceRights.Create(name, group, comment); break;
                        case "deleteDeviceRight":
#if SECURITY_DEVICE_RIGHT_DELETE
                            ((CustomDeviceFunctionRight)existing!).Delete(); break;
#else
                            throw new NotSupportedException("CustomDeviceFunctionRight.Delete requires V20 or later.");
#endif
                    }
                }
                finally { secure?.Dispose(); }
                meta["apiCallSuccess"] = true;
                var readback = _session.FindOrdinal(collection, name, request.Target);
                if (request.Deletes) { if (readback != null) throw new InvalidOperationException("Delete returned but " + request.Target + " is still present."); meta["verifiedAbsent"] = true; return "UMAC " + request.Target + " deleted and verified absent; no save."; }
                if (readback == null) throw new InvalidOperationException("Native call returned but " + request.Target + " cannot be read back.");
                var after = _session.UmacRow(readback); meta["after"] = after;
                bool verified = action switch
                {
                    "activateUser" => ((ProjectUser)readback).IsActive,
                    "deactivateUser" => !((ProjectUser)readback).IsActive,
                    "assignRole" => after["roles"]!.Array().Any(x => x!.GetValue<string>() == role!.Name),
                    "unassignRole" => !after["roles"]!.Array().Any(x => x!.GetValue<string>() == role!.Name),
                    "assignEngineeringRight" => after["assignedEngineeringRights"]!.Array().Any(x => x!.GetValue<string>() == engineeringRight!.Name),
                    "unassignEngineeringRight" => !after["assignedEngineeringRights"]!.Array().Any(x => x!.GetValue<string>() == engineeringRight!.Name),
                    "assignDeviceRight" => ((CustomRole)readback).GetAssignedDeviceFunctionRights(device!).Any(x => x.Name == deviceRight!.Name),
                    "unassignDeviceRight" => !((CustomRole)readback).GetAssignedDeviceFunctionRights(device!).Any(x => x.Name == deviceRight!.Name),
                    _ => true
                };
                if (!verified) throw new InvalidOperationException("Native call returned but readback does not show the requested state for " + action + ".");
                if (action == "setUserPassword") meta["verification"] = "Password is not readable; verified only that the API returned and the user still exists.";
                return "UMAC " + action + " executed and read back; no save/compile/download.";
            });

}
#endif
