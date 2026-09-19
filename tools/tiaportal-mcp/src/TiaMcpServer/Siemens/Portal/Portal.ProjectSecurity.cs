using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.AdvancedProtection;
using Siemens.Engineering.CustomIdentity;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Settings;
using Siemens.Engineering.SW;
using Siemens.Engineering.Umac;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // ---- UMAC helpers ----
        private UmacConfigurator RequireUmac()
            => _project!.GetService<UmacConfigurator>() ?? throw new NotSupportedException("UmacConfigurator service unavailable on this project: TIA returns it only for a protected project opened with UMAC credentials. Enabling protection is intentionally not exposed.");
        private static object? FindOrdinal(object collection, string name, string kind)
        {
            var matches = EngineeringGroupOperations.Items(collection).Where(x => string.Equals(EngineeringGroupOperations.Get(x, "Name").ToString(), name, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact " + kind + " name: " + name);
            return matches.SingleOrDefault();
        }
        private static T ExactUmacItem<T>(object collection, string name, string kind) where T : class
            => FindOrdinal(collection, name, kind) as T ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + kind + " not found: " + name);
        private static Role ExactUmacRole(UmacConfigurator umac, string name)
        {
            var custom = FindOrdinal(umac.CustomRoles, name, "custom role"); var system = FindOrdinal(umac.SystemRoles, name, "system role");
            if (custom != null && system != null) throw new InvalidOperationException("Role name exists as both custom and system role: " + name);
            return (Role?)custom ?? (Role?)system ?? throw new PortalException(PortalErrorCode.NotFound, "Exact role not found: " + name);
        }
        // HardwareObject.GetService exists on V21 only; Device/DeviceItem expose it on both versions.
        private static T? HardwareService<T>(HardwareObject hardware) where T : class, IEngineeringService
            => hardware is Device device ? device.GetService<T>() : hardware is DeviceItem item ? item.GetService<T>() : null;
        private UmacDevice RequireUmacDevice(string devicePathJson, string itemPathJson)
            => HardwareService<UmacDevice>(ExactEngineeringHardware(devicePathJson, itemPathJson))
               ?? throw new NotSupportedException("UmacDevice service unavailable on the selected hardware object. On the reference project it lives on the Device (devicePathJson only, itemPathJson=[]), not on the CPU DeviceItem; address the Device or the DeviceItem that owns the UMAC configuration.");
        private static JsonArray Names(object collection)
            => new JsonArray(EngineeringGroupOperations.Items(collection).Select(x => (JsonNode)JsonValue.Create(EngineeringGroupOperations.Get(x, "Name").ToString())!).ToArray());
        private static JsonObject UmacRow(object item)
        {
            var row = EngineeringScalarProperties.Read(item);
            if (item is User user) row["roles"] = Names(user.Roles);
            if (item is UmcUserGroup group) row["roles"] = Names(group.Roles);
            if (item is CustomRole custom) row["assignedEngineeringRights"] = Names(custom.AssignedEngineeringRights);
            if (item is SystemRole system) row["assignedEngineeringRights"] = Names(system.AssignedEngineeringRights);
            // 2.7.32: DeviceFunctionRight rows typed (Identifier / Group; Comment on both the system and the custom subclass).
            if (item is DeviceFunctionRight right) { row["identifier"] = right.Identifier; row["group"] = right.Group; row["rightClass"] = right.GetType().Name; }
            if (item is SystemDeviceFunctionRight systemRight) row["comment"] = systemRight.Comment;
            if (item is CustomDeviceFunctionRight customRight) row["comment"] = customRight.Comment;
            return row;
        }

        public ResponseMessage ReadProjectUserManagement(string category = "users", string name = "", string devicePathJson = "[]", string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadProjectUserManagement", meta => {
                ProjectSecurityLogic.ValidateCategory(category); ProjectSecurityLogic.ValidatePage(offset, limit);
                if (ProjectSecurityLogic.DeviceCategories.Contains(category)) ProjectSecurityLogic.ValidateDevicePath(devicePathJson);
                if (category == "roleDeviceRights" && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("roleDeviceRights requires name = exact role name.");
                var umac = RequireUmac();
                meta["category"] = category; meta["scope"] = "Public scalar properties plus role/right names. Passwords are never readable.";
                object[] items;
                switch (category)
                {
                    case "users": items = EngineeringGroupOperations.Items(umac.ProjectUsers).ToArray(); break;
                    case "anonymousUser": items = umac.AnonymousUser == null ? Array.Empty<object>() : new object[] { umac.AnonymousUser }; break;
                    case "systemRoles": items = EngineeringGroupOperations.Items(umac.SystemRoles).ToArray(); break;
                    case "customRoles": items = EngineeringGroupOperations.Items(umac.CustomRoles).ToArray(); break;
                    case "engineeringRights": items = EngineeringGroupOperations.Items(umac.EngineeringFunctionRights).ToArray(); break;
                    case "customDeviceRights": items = EngineeringGroupOperations.Items(umac.CustomDeviceFunctionRights).ToArray(); break;
                    case "umcUsers": items = EngineeringGroupOperations.Items(umac.UmcUsers).ToArray(); break;
                    case "umcUserGroups": items = EngineeringGroupOperations.Items(umac.UmcUserGroups).ToArray(); break;
                    case "passwordPolicy":
                        items = new object[] { _project!.GetService<PasswordPolicyConfigurator>() ?? throw new NotSupportedException("PasswordPolicyConfigurator service unavailable on this project.") }; break;
                    case "deviceRights": items = EngineeringGroupOperations.Items(RequireUmacDevice(devicePathJson, itemPathJson).AvailableDeviceFunctionRights).ToArray(); break;
                    default: // roleDeviceRights: name = exact role
                        var role = ExactUmacRole(umac, name); var device = RequireUmacDevice(devicePathJson, itemPathJson);
                        items = role is CustomRole customRole ? customRole.GetAssignedDeviceFunctionRights(device).Cast<object>().ToArray() : ((SystemRole)role).GetAssignedSystemDeviceFunctionRights(device).Cast<object>().ToArray();
                        meta["roleKind"] = role.GetType().Name; break;
                }
                if (!string.IsNullOrEmpty(name) && category != "roleDeviceRights") items = new[] { ExactUmacItem<object>(items, name, category) };
                var rows = new JsonArray(items.Skip(offset).Take(limit).Select(x => (JsonNode)UmacRow(x)).ToArray());
                ProjectSecurityLogic.PageMeta(meta, items.Length, offset, limit, rows.Count);
                meta["records"] = rows; meta["apiCallSuccess"] = true;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                return "UMAC " + category + " read; scalar scope only, paginated.";
            });

        public ResponseMessage ManageProjectUserManagement(string action, string name, string password = "", string roleName = "", string rightName = "", string comment = "", string group = "",
            string devicePathJson = "[]", string itemPathJson = "[]", bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("ManageProjectUserManagement", meta => {
                var request = ProjectSecurityLogic.ValidateUserManagement(action, name, password, roleName, rightName, group, devicePathJson);
                bool writing = !dryRun;
                if (writing && !confirmChange) throw new ArgumentException("Real UMAC changes require confirmChange=true together with dryRun=false.");
                using var access = writing ? AcquireHmiEditAccess() : null;
                var umac = RequireUmac();
                meta["action"] = action; meta["name"] = name; meta["dryRun"] = dryRun; meta["confirmChange"] = confirmChange; meta["mayHaveChanged"] = false;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                if (request.Target == "anonymousUser")
                {
                    // Official: AnonymousUser is null while deactivated; one anonymous user per protected project.
                    var anonymousBefore = umac.AnonymousUser; meta["before"] = anonymousBefore == null ? null : UmacRow(anonymousBefore); meta["activeBefore"] = anonymousBefore?.IsActive ?? false;
                    if (dryRun) return "Anonymous user " + action + " preview; nothing written (deactivating it can lock out clients that rely on password-less access).";
                    meta["mayHaveChanged"] = true;
                    if (action == "activateAnonymousUser") umac.ActivateAnonymousUser(); else umac.DeactivateAnonymousUser();
                    meta["apiCallSuccess"] = true;
                    var anonymousAfter = RequireUmac().AnonymousUser; meta["after"] = anonymousAfter == null ? null : UmacRow(anonymousAfter); meta["activeAfter"] = anonymousAfter?.IsActive ?? false;
                    bool active = anonymousAfter?.IsActive ?? false;
                    if (active != (action == "activateAnonymousUser")) throw new InvalidOperationException("Native call returned but the anonymous user state did not change to the requested one.");
                    return "Anonymous user " + action + " executed and read back; no save.";
                }
                object collection = request.Target == "user" ? umac.ProjectUsers : request.Target == "role" ? umac.CustomRoles : umac.CustomDeviceFunctionRights;
                var existing = FindOrdinal(collection, name, request.Target);
                if (request.Creates && existing != null) throw new InvalidOperationException("Exact " + request.Target + " already exists: " + name);
                if (!request.Creates && existing == null) throw new PortalException(PortalErrorCode.NotFound, "Exact " + request.Target + " not found: " + name + (request.Target == "role" ? " (only custom roles can be managed)" : ""));
                if (existing != null) meta["before"] = UmacRow(existing);
                Role? role = request.NeedsRole ? ExactUmacRole(umac, roleName) : null;
                EngineeringFunctionRight? engineeringRight = null; UmacDevice? device = null; DeviceFunctionRight? deviceRight = null;
                if (request.NeedsDevice) { device = RequireUmacDevice(devicePathJson, itemPathJson); deviceRight = ExactUmacItem<DeviceFunctionRight>(device.AvailableDeviceFunctionRights, rightName, "device function right"); }
                else if (request.NeedsRight) engineeringRight = ExactUmacItem<EngineeringFunctionRight>(umac.EngineeringFunctionRights, rightName, "engineering function right");
                if (dryRun) return "UMAC change preview; objects validated, nothing written.";
                meta["mayHaveChanged"] = true;
                SecureString? secure = request.NeedsPassword ? ProjectSecurityLogic.Secure(password) : null;
                try
                {
                    var user = existing as ProjectUser; var customRole = existing as CustomRole;
                    ProjectUserComposition projectUsers = umac.ProjectUsers; CustomRoleComposition customRoles = umac.CustomRoles; CustomDeviceFunctionRightComposition deviceRights = umac.CustomDeviceFunctionRights;
                    RoleAssociation? userRoles = user?.Roles; EngineeringFunctionRightAssociation? engineeringRights = customRole?.AssignedEngineeringRights;
                    switch (action)
                    {
                        case "createUser": projectUsers.Create(name, secure!); break;
                        case "deleteUser": user!.Delete(); break;
                        case "setUserPassword": user!.SetPassword(secure!); break;
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
                        case "deleteDeviceRight": ((CustomDeviceFunctionRight)existing!).Delete(); break;
                    }
                }
                finally { secure?.Dispose(); }
                meta["apiCallSuccess"] = true;
                var readback = FindOrdinal(collection, name, request.Target);
                if (request.Deletes) { if (readback != null) throw new InvalidOperationException("Delete returned but " + request.Target + " is still present."); meta["verifiedAbsent"] = true; return "UMAC " + request.Target + " deleted and verified absent; no save."; }
                if (readback == null) throw new InvalidOperationException("Native call returned but " + request.Target + " cannot be read back.");
                var after = UmacRow(readback); meta["after"] = after;
                bool verified = action switch
                {
                    "activateUser" => ((ProjectUser)readback).IsActive,
                    "deactivateUser" => !((ProjectUser)readback).IsActive,
                    "assignRole" => after["roles"]!.AsArray().Any(x => x!.GetValue<string>() == role!.Name),
                    "unassignRole" => !after["roles"]!.AsArray().Any(x => x!.GetValue<string>() == role!.Name),
                    "assignEngineeringRight" => after["assignedEngineeringRights"]!.AsArray().Any(x => x!.GetValue<string>() == engineeringRight!.Name),
                    "unassignEngineeringRight" => !after["assignedEngineeringRights"]!.AsArray().Any(x => x!.GetValue<string>() == engineeringRight!.Name),
                    "assignDeviceRight" => ((CustomRole)readback).GetAssignedDeviceFunctionRights(device!).Any(x => x.Name == deviceRight!.Name),
                    "unassignDeviceRight" => !((CustomRole)readback).GetAssignedDeviceFunctionRights(device!).Any(x => x.Name == deviceRight!.Name),
                    _ => true
                };
                if (!verified) throw new InvalidOperationException("Native call returned but readback does not show the requested state for " + action + ".");
                if (action == "setUserPassword") meta["verification"] = "Password is not readable; verified only that the API returned and the user still exists.";
                return "UMAC " + action + " executed and read back; no save/compile/download.";
            });

        public ResponseMessage ReadProjectProtection()
            => RunHmiStepTool("ReadProjectProtection", meta => {
                var project = _project!;
                bool complete = true;
                var projectRow = EngineeringScalarProperties.Read(project); meta["project"] = projectRow; complete &= projectRow["dataComplete"]!.GetValue<bool>();
                meta["protectProjectMethodAvailable"] = project.GetType().GetMethod("ProtectProject", new[] { typeof(string), typeof(SecureString) }) != null;
                meta["projectProtectedFlag"] = null;
                meta["note"] = "The Openness API exposes no project-level IsProtected scalar. Availability of the UmacConfigurator service is the only native indicator. ProjectBase.ProtectProject and ProtectionProviderBase.Protect/Unprotect are intentionally not exposed.";
                var umac = project.GetService<UmacConfigurator>();
                meta["umacConfiguratorAvailable"] = umac != null;
                if (umac != null)
                {
                    meta["counts"] = new JsonObject {
                        ["users"] = EngineeringGroupOperations.Items(umac.ProjectUsers).Count(), ["customRoles"] = EngineeringGroupOperations.Items(umac.CustomRoles).Count(),
                        ["systemRoles"] = EngineeringGroupOperations.Items(umac.SystemRoles).Count(), ["engineeringRights"] = EngineeringGroupOperations.Items(umac.EngineeringFunctionRights).Count(),
                        ["customDeviceRights"] = EngineeringGroupOperations.Items(umac.CustomDeviceFunctionRights).Count(), ["umcUsers"] = EngineeringGroupOperations.Items(umac.UmcUsers).Count(),
                        ["umcUserGroups"] = EngineeringGroupOperations.Items(umac.UmcUserGroups).Count() };
                    if (umac.AnonymousUser != null) { var row = UmacRow(umac.AnonymousUser); meta["anonymousUser"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
                }
                var policy = project.GetService<PasswordPolicyConfigurator>();
                if (policy != null) { var row = EngineeringScalarProperties.Read(policy); meta["passwordPolicy"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
                var umcServer = project.GetService<UmcServerConfigurator>();
                meta["umcServerConfiguratorAvailable"] = umcServer != null;
                if (umcServer?.UmcServer != null) { var row = EngineeringScalarProperties.Read(umcServer.UmcServer); meta["umcServer"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
                var provider = project.GetService<ProtectionProviderBase>();
                meta["advancedProtectionProviderAvailable"] = provider != null;
                if (provider != null) meta["invalidPasswordCharacters"] = new string(provider.GetInvalidPasswordCharacters().ToArray());
                meta["verificationCertificates"] = "Siemens.Engineering.Online.Security.VerificationCertificate is only reachable via TlsVerificationConfiguration.Certificates inside an online/download configuration callback; there is no project-level read.";
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                return "Project protection indicators read; no protection state changed.";
            });

        // ---- Multiuser ----
        private ProjectServer ExactProjectServer(string serverName)
        {
            if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
            var matches = _portal.ProjectServers.Where(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous server alias: " + serverName);
            return matches.SingleOrDefault() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact project server alias not found: " + serverName);
        }
        private static ServerProjectInfo ExactServerProject(ProjectServer server, string projectName)
        {
            var matches = server.GetServerProjects().Where(p => string.Equals(p.ProjectName, projectName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous server project name: " + projectName);
            return matches.SingleOrDefault() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact server project not found: " + projectName);
        }
        public ResponseMessage ManageMultiuserSession(string action = "read", string serverName = "", string projectName = "", string protocol = "Https", string host = "", int port = 0,
            string commitComment = "", int offset = 0, int limit = 100, bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("ManageMultiuserSession", meta => {
                bool mutation = ProjectSecurityLogic.ValidateMultiuser(action, serverName, projectName, protocol, host, port, commitComment, confirmChange, dryRun);
                ProjectSecurityLogic.ValidatePage(offset, limit);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                switch (action)
                {
                    case "read":
                    {
                        meta["projectServers"] = new JsonArray(_portal.ProjectServers.Select(s => (JsonNode)EngineeringScalarProperties.Read(s)).ToArray());
                        meta["localSessions"] = new JsonArray(_portal.LocalSessions.Select(s => (JsonNode)JsonValue.Create(s.Project?.Name)!).ToArray());
                        meta["boundSessionActive"] = _session != null;
                        if (_session != null)
                        {
                            var bound = new JsonObject { ["projectName"] = _session.Project?.Name };
                            try { bound["isUpToDate"] = _session.IsUptoDate(); } catch (Exception ex) { bound["isUpToDate"] = null; bound["isUpToDateError"] = ex.GetBaseException().Message; }
                            Markings markings = _session.MarkingService.GetMarkings();
                            var all = EngineeringGroupOperations.Items(markings.AllMarkings).ToArray();
                            bound["conflictedMarkingCount"] = EngineeringGroupOperations.Items(markings.ConflictedMarkings).Count();
                            var rows = ProjectSecurityLogic.Page(all.Select(m => (JsonNode)new JsonObject {
                                ["markState"] = ((Marking)m).MarkState.ToString(), ["objectType"] = ((Marking)m).MarkedObject?.GetType().FullName,
                                ["objectName"] = ((Marking)m).MarkedObject == null ? null : ((Marking)m).MarkedObject.GetType().GetProperty("Name")?.GetValue(((Marking)m).MarkedObject)?.ToString() }).ToList(), offset, limit, meta);
                            bound["markings"] = rows; meta["boundSession"] = bound;
                            meta["dataComplete"] = offset == 0 && rows.Count == all.Length;
                        }
                        else meta["dataComplete"] = true;
                        meta["apiCallSuccess"] = true;
                        return "Multiuser state read: configured servers, open local sessions, bound session up-to-date flag and markings.";
                    }
                    case "listServerProjects":
                    {
                        var server = ExactProjectServer(serverName); meta["server"] = EngineeringScalarProperties.Read(server);
                        var projects = server.GetServerProjects();
                        var rows = ProjectSecurityLogic.Page(projects.Select(p => (JsonNode)EngineeringScalarProperties.Read(p)).ToList(), offset, limit, meta);
                        meta["records"] = rows; meta["serverGroups"] = new JsonArray(server.GetProjectServerGroups().Select(g => { ProjectServerGroup group = g; return (JsonNode)JsonValue.Create(group.Name)!; }).ToArray());
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == projects.Count;
                        return "Server projects listed for the exact server alias; nothing opened.";
                    }
                    case "readLockState":
                    {
                        var server = ExactProjectServer(serverName); var info = ExactServerProject(server, projectName);
                        LockStateProvider lockState = server.GetLockStateProvider(info);
                        meta["serverProject"] = EngineeringScalarProperties.Read(info);
                        meta["isProjectLocked"] = lockState.IsProjectLocked(); meta["lockOwner"] = lockState.GetLockOwner();
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                        return "Server project lock state read.";
                    }
                    case "listLocalSessions":
                    {
                        var server = ExactProjectServer(serverName); var info = ExactServerProject(server, projectName);
                        var sessions = server.GetLocalSessions(info);
                        var rows = ProjectSecurityLogic.Page(sessions.Select(s => (JsonNode)new JsonObject { ["sessionId"] = s.SessionId, ["projectFile"] = s.ProjectFileInfo?.FullName }).ToList(), offset, limit, meta);
                        meta["records"] = rows; meta["serverProject"] = EngineeringScalarProperties.Read(info);
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == sessions.Count;
                        return "Local sessions registered on the server for the exact project listed.";
                    }
                    case "connectServer":
                    {
                        if (_portal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("Server alias already configured: " + serverName);
                        var mode = (Protocol)Enum.Parse(typeof(Protocol), protocol);
                        meta["request"] = new JsonObject { ["alias"] = serverName, ["protocol"] = protocol, ["host"] = host, ["port"] = port };
                        if (dryRun) return "Project server connection preview; nothing created.";
                        meta["mayHaveChanged"] = true;
                        var created = _portal.ProjectServers.Create(serverName, mode, host, port);
                        meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(created);
                        if (created.ServerName != serverName || created.Host != host || created.Port != port) throw new InvalidOperationException("Server connection created but readback differs.");
                        return "Project server connection created and read back. This is a persistent TIA configuration; no project opened.";
                    }
                    case "disconnectServer":
                    {
                        var server = ExactProjectServer(serverName); meta["before"] = EngineeringScalarProperties.Read(server);
                        if (dryRun) return "Project server connection removal preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        server.DeleteConnection(); meta["apiCallSuccess"] = true;
                        if (_portal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("DeleteConnection returned but the alias is still configured.");
                        meta["verifiedAbsent"] = true;
                        return "Project server connection removed and verified absent.";
                    }
                    default: // commit
                    {
                        if (_session == null) throw new PortalException(PortalErrorCode.InvalidState, "No bound local session; commit applies only to a multiuser local session opened via OpenSession.");
                        meta["sessionProject"] = _session.Project?.Name; meta["commitComment"] = commitComment;
                        if (dryRun) return "Commit preview: CloseAndCommit would commit the local session changes to the server AND close the bound session. Nothing executed.";
                        meta["mayHaveChanged"] = true; meta["sessionClosed"] = false;
                        int committed = _session.CloseAndCommit(commitComment);
                        meta["apiCallSuccess"] = true; meta["nativeResult"] = committed; meta["sessionClosed"] = true;
                        _session = null; _project = null; _projectOpenedByUs = false; InvalidateHmiSoftwareCache();
                        return "Local session committed and closed by the native CloseAndCommit; the MCP project binding was cleared. Re-open a session explicitly to continue.";
                    }
                }
            }, requiresProject: false);

        // ---- Offline comparison ----
        private static object InvokeNativeCompare(object subject, string method, object target)
        {
            var info = subject.GetType().GetMethods().FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(target))
                ?? throw new NotSupportedException(subject.GetType().Name + "." + method + " does not accept " + target.GetType().Name + " on this API version.");
            try { return info.Invoke(subject, new[] { target }) ?? throw new InvalidOperationException("Native comparison returned null; no result available, do not read this as identical."); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        private static JsonObject CompareRow(object element)
        {
            var type = element.GetType();
            var row = new JsonObject {
                ["leftName"] = type.GetProperty("LeftName")?.GetValue(element)?.ToString(), ["rightName"] = type.GetProperty("RightName")?.GetValue(element)?.ToString(),
                ["comparisonResult"] = type.GetProperty("ComparisonResult")?.GetValue(element)?.ToString(), ["detailedInformation"] = type.GetProperty("DetailedInformation")?.GetValue(element)?.ToString() };
            var left = type.GetProperty("Left")?.GetValue(element); var right = type.GetProperty("Right")?.GetValue(element);
            if (left != null) row["leftType"] = left.GetType().FullName;
            if (right != null) row["rightType"] = right.GetType().FullName;
            return row;
        }
        private static string PublishCompareResult(object result, bool includeIdentical, int maxDepth, int offset, int limit, JsonObject meta)
        {
            // 2.7.33: the software compare tree is typed (CompareResult.RootElement -> CompareResultElement.Elements); the library
            // tree keeps the reflective walk (LibraryCompareResultElement lives in the Library.Compare namespace).
            var root = result is global::Siemens.Engineering.Compare.CompareResult typedResult ? (object)typedResult.RootElement : EngineeringGroupOperations.Get(result, "RootElement");
            if (root is global::Siemens.Engineering.Compare.CompareResultElement rootElement) meta["rootElement"] = new JsonObject { ["leftName"] = rootElement.LeftName, ["rightName"] = rootElement.RightName, ["comparisonResult"] = rootElement.ComparisonResult.ToString(), ["detailedInformation"] = rootElement.DetailedInformation, ["elements"] = EngineeringGroupOperations.Items(rootElement.Elements).Count() };
            var rows = ProjectSecurityLogic.FlattenCompareTree(root, e => EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(e, "Elements")), CompareRow, maxDepth, 10000, out bool treeTruncated);
            meta["resultType"] = result.GetType().FullName; meta["summary"] = ProjectSecurityLogic.Summarize(rows); meta["totalElements"] = rows.Count; meta["treeTruncated"] = treeTruncated;
            var selected = includeIdentical ? rows : rows.Where(r => !ProjectSecurityLogic.IsIdentical(r["comparisonResult"]?.GetValue<string>())).ToList();
            meta["records"] = ProjectSecurityLogic.Page(selected.Cast<JsonNode?>().ToList(), offset, limit, meta);
            meta["includeIdentical"] = includeIdentical; meta["apiCallSuccess"] = true;
            meta["dataComplete"] = !treeTruncated && meta["truncated"]!.GetValue<bool>() == false && offset == 0;
            return "Native offline comparison completed; " + rows.Count + " elements walked (summary counts all states, records exclude identical unless requested).";
        }
        public ResponseMessage CompareLibraries(string leftLibraryName = "", string rightLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100)
            => RunHmiStepTool("CompareLibraries", meta => {
                ProjectSecurityLogic.ValidateLibraryPair(leftLibraryName, rightLibraryName); ProjectSecurityLogic.ValidatePage(offset, limit); ProjectSecurityLogic.ValidateDepth(maxDepth);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                if ((string.IsNullOrEmpty(leftLibraryName) || string.IsNullOrEmpty(rightLibraryName)) && IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project library requested but no project is bound.");
                var left = ExactOpenEngineeringLibrary(leftLibraryName); var right = ExactOpenEngineeringLibrary(rightLibraryName);
                if (ReferenceEquals(left, right)) throw new ArgumentException("Left and right resolve to the same library.");
                meta["left"] = LibraryRef(left); meta["left"]!["type"] = left.GetType().FullName;
                meta["right"] = LibraryRef(right); meta["right"]!["type"] = right.GetType().FullName;
                return PublishCompareResult(InvokeNativeCompare(left, "CompareToLibrary", right), includeIdentical, maxDepth, offset, limit, meta);
            }, requiresProject: false);

        private ProjectBase ExactOpenProject(string projectName)
        {
            var candidates = _portal!.Projects.Cast<ProjectBase>().Concat(_portal.LocalSessions.Select(s => (ProjectBase)s.Project))
                .Where(p => p != null && string.Equals(p.Name, projectName, StringComparison.Ordinal)).Take(2).ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("Ambiguous open project name: " + projectName);
            return candidates.SingleOrDefault() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact open project not found in this Portal instance: " + projectName);
        }
        private static HardwareObject ExactHardwareInProject(ProjectBase project, string devicePathJson, string itemPathJson)
        {
            var names = JsonNode.Parse(devicePathJson)!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Device device;
            if (names.Length == 1)
            {
                var all = project.Devices.Cast<Device>().Concat(EnumerateGroupDevices(project.DeviceGroups)).ToArray();
                device = (Device)(EngineeringGroupOperations.Find(all, names[0]) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact unique device not found in project " + project.Name + ": " + names[0]));
            }
            else
            {
                var group = EngineeringGroupOperations.Find(project.DeviceGroups, names[0]) as DeviceUserGroup ?? throw new PortalException(PortalErrorCode.NotFound, "Device group not found: " + names[0]);
                foreach (var name in names.Skip(1).Take(names.Length - 2))
                    group = (DeviceUserGroup)(EngineeringGroupOperations.Find(group.Groups, name) ?? throw new PortalException(PortalErrorCode.NotFound, "Device group not found: " + name));
                device = (Device)(EngineeringGroupOperations.Find(group.Devices, names.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Device not found: " + names.Last()));
            }
            HardwareObject current = device;
            var items = JsonNode.Parse(itemPathJson) as JsonArray ?? throw new ArgumentException("itemPathJson must be a JSON array.");
            if (items.Count > 64) throw new ArgumentException("Invalid item path.");
            foreach (var node in items) current = (DeviceItem)(EngineeringGroupOperations.Find(current.DeviceItems, node!.GetValue<string>()) ?? throw new PortalException(PortalErrorCode.NotFound, "Device item not found: " + node));
            return current;
        }
        public ResponseMessage CompareProjects(string kind, string softwarePath = "", string devicePathJson = "[]", string itemPathJson = "[]", string targetProjectName = "", string targetSoftwarePath = "",
            string targetDevicePathJson = "[]", string targetItemPathJson = "[]", string targetLibraryName = "", bool includeIdentical = false, int maxDepth = 8, int offset = 0, int limit = 100)
            => RunHmiStepTool("CompareProjects", meta => {
                ProjectSecurityLogic.ValidateCompareRequest(kind, softwarePath, devicePathJson, targetProjectName, targetSoftwarePath, targetDevicePathJson);
                ProjectSecurityLogic.ValidatePage(offset, limit); ProjectSecurityLogic.ValidateDepth(maxDepth);
                meta["kind"] = kind; meta["scope"] = "Offline native comparison (PlcSoftware.CompareTo / HardwareObject.CompareTo / CompareToLibrary target); no online access, nothing modified.";
                ProjectBase? other = string.IsNullOrWhiteSpace(targetProjectName) ? null : ExactOpenProject(targetProjectName);
                if (other != null && ReferenceEquals(other, _project)) throw new ArgumentException("targetProjectName is the bound project; use targetSoftwarePath/targetDevicePathJson without targetProjectName.");
                if (other != null) meta["targetProject"] = new JsonObject { ["name"] = other.Name, ["path"] = other.Path?.FullName };
                object subject, target; string method;
                if (kind == "hardware")
                {
                    subject = ExactEngineeringHardware(devicePathJson, itemPathJson);
                    target = other == null ? ExactEngineeringHardware(targetDevicePathJson, targetItemPathJson) : ExactHardwareInProject(other, targetDevicePathJson, targetItemPathJson);
                    method = "CompareTo";
                }
                else
                {
                    subject = ExactPlcForEngineering(softwarePath, false);
                    if (kind == "softwareToLibrary") { target = ExactOpenEngineeringLibrary(targetLibraryName); }
                    else if (other == null) target = ExactPlcForEngineering(targetSoftwarePath, false);
                    else target = HardwareService<SoftwareContainer>(ExactHardwareInProject(other, targetDevicePathJson, targetItemPathJson))?.Software as PlcSoftware
                        ?? throw new PortalException(PortalErrorCode.NotFound, "Selected hardware object in the target project hosts no PLC software; address the CPU DeviceItem exactly.");
                    method = "CompareTo";
                }
                if (ReferenceEquals(subject, target)) throw new ArgumentException("Source and target resolve to the same object.");
                meta["source"] = new JsonObject { ["type"] = subject.GetType().FullName, ["name"] = subject.GetType().GetProperty("Name")?.GetValue(subject)?.ToString() };
                meta["target"] = new JsonObject { ["type"] = target.GetType().FullName, ["name"] = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString() };
                return PublishCompareResult(InvokeNativeCompare(subject, method, target), includeIdentical, maxDepth, offset, limit, meta);
            });

        // ---- Settings / custom identity ----
        public ResponseMessage ReadProjectSettings(string folderPath = "", string customIdentityKey = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadProjectSettings", meta => {
                ProjectSecurityLogic.ValidatePage(offset, limit);
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                var parts = EngineeringGroupOperations.Parts(folderPath, true);
                // 2.7.33: typed root (TiaPortal.SettingsFolders -> TiaPortalSettingsFolder.Folders / Settings -> TiaPortalSetting.Name / Value).
                TiaPortalSettingsFolderComposition rootFolders = _portal.SettingsFolders; object collection = rootFolders; object? folder = null;
                foreach (var part in parts)
                {
                    folder = EngineeringGroupOperations.Find(collection, part) ?? throw new PortalException(PortalErrorCode.NotFound, "Settings folder not found: " + part);
                    collection = EngineeringGroupOperations.Get(folder, "Folders");
                }
                meta["folderPath"] = folderPath;
                meta["subFolders"] = Names(collection);
                List<JsonNode?> rows; bool complete = true;
                if (folder == null) rows = EngineeringGroupOperations.Items(collection).Select(f => (JsonNode?)EngineeringScalarProperties.Read(f)).ToList();
                else
                {
                    var row = EngineeringScalarProperties.Read(folder); meta["folder"] = row; complete &= row["dataComplete"]!.GetValue<bool>();
                    rows = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(folder, "Settings")).Select(s => (JsonNode?)EngineeringScalarProperties.Read(s)).ToList();
                }
                var page = ProjectSecurityLogic.Page(rows, offset, limit, meta); meta["records"] = page;
                complete &= offset == 0 && page.Count == rows.Count && page.All(r => r!["dataComplete"]!.GetValue<bool>());
                meta["scope"] = folder == null ? "Root TIA Portal settings folders (scalar scope); pass folderPath to list a folder's settings." : "Settings of the exact folder: Name and scalar Value only; non-scalar values are excluded.";
                if (!string.IsNullOrEmpty(customIdentityKey))
                {
                    if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "customIdentityKey requires a bound project.");
                    var provider = _project!.GetService<CustomIdentityProvider>() ?? throw new NotSupportedException("CustomIdentityProvider service unavailable on the project root.");
                    var identity = new JsonObject { ["key"] = customIdentityKey };
                    try { identity["value"] = provider.Get(customIdentityKey); identity["found"] = true; }
                    catch (CustomIdentityNotFoundException) { identity["value"] = null; identity["found"] = false; }
                    meta["customIdentity"] = identity;
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = complete;
                return "TIA Portal settings read (read-only); nothing modified.";
            }, requiresProject: false);
    }
}
