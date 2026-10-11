using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ProjectSecurityService
    {
        private readonly IEngineeringSession _session;

        public ProjectSecurityService(IEngineeringSession session) => _session = session;

        // ---- UMAC helpers ----
        private T ExactUmacItem<T>(object collection, string name, string kind) where T : class
            => _session.FindOrdinal(collection, name, kind) as T ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + kind + " not found: " + name);
        // HardwareObject.GetService exists on V21 only; Device/DeviceItem expose it on both versions.
        private static T? HardwareService<T>(HardwareObject hardware) where T : class, IEngineeringService
            => hardware is Device device ? device.GetService<T>() : hardware is DeviceItem item ? item.GetService<T>() : null;
        private UmacDevice RequireUmacDevice(string devicePathJson, string itemPathJson)
            => HardwareService<UmacDevice>(_session.ExactEngineeringHardware(devicePathJson, itemPathJson))
               ?? throw new NotSupportedException("UmacDevice service unavailable on the selected hardware object. On the reference project it lives on the Device (devicePathJson only, itemPathJson=[]), not on the CPU DeviceItem; address the Device or the DeviceItem that owns the UMAC configuration.");
        public ResponseMessage ReadProjectUserManagement(string category = "users", string name = "", string devicePathJson = "[]", string itemPathJson = "[]", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetProjectUserManagement", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                ProjectSecurityLogic.ValidateCategory(category); ProjectSecurityLogic.ValidatePage(offset, limit);
                if (ProjectSecurityLogic.DeviceCategories.Contains(category)) ProjectSecurityLogic.ValidateDevicePath(devicePathJson);
                if (category == "roleDeviceRights" && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("roleDeviceRights requires name = exact role name.");
                var umac = _session.RequireUmac();
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
                        items = new object[] { _session.CurrentProject!.GetService<PasswordPolicyConfigurator>() ?? throw new NotSupportedException("PasswordPolicyConfigurator service unavailable on this project.") }; break;
                    case "deviceRights": items = EngineeringGroupOperations.Items(RequireUmacDevice(devicePathJson, itemPathJson).AvailableDeviceFunctionRights).ToArray(); break;
                    default: // roleDeviceRights: name = exact role
                        var role = _session.ExactUmacRole(umac, name); var device = RequireUmacDevice(devicePathJson, itemPathJson);
                        items = role is CustomRole customRole ? customRole.GetAssignedDeviceFunctionRights(device).Cast<object>().ToArray() : ((SystemRole)role).GetAssignedSystemDeviceFunctionRights(device).Cast<object>().ToArray();
                        meta["roleKind"] = role.GetType().Name; break;
                }
                if (!string.IsNullOrEmpty(name) && category != "roleDeviceRights") items = new[] { ExactUmacItem<object>(items, name, category) };
                var rows = new JsonArray(items.Skip(offset).Take(limit).Select(x => (JsonNode)_session.UmacRow(x)).ToArray());
                ProjectSecurityLogic.PageMeta(meta, items.Length, offset, limit, rows.Count);
                meta["records"] = rows; meta["apiCallSuccess"] = true;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                return "UMAC " + category + " read; scalar scope only, paginated.";
            });

        public ResponseMessage ManageProjectUserManagement(string action, string name, string password = "", string roleName = "", string rightName = "", string comment = "", string group = "",
            string devicePathJson = "[]", string itemPathJson = "[]", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageProjectUserManagement", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                var request = ProjectSecurityLogic.ValidateUserManagement(action, name, password, roleName, rightName, group, devicePathJson);
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
                var readback = _session.FindOrdinal(collection, name, request.Target);
                if (request.Deletes) { if (readback != null) throw new InvalidOperationException("Delete returned but " + request.Target + " is still present."); meta["verifiedAbsent"] = true; return "UMAC " + request.Target + " deleted and verified absent; no save."; }
                if (readback == null) throw new InvalidOperationException("Native call returned but " + request.Target + " cannot be read back.");
                var after = _session.UmacRow(readback); meta["after"] = after;
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
            => _session.RunHmiStepTool("GetProjectProtection", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                var project = _session.CurrentProject!;
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
                    if (umac.AnonymousUser != null) { var row = _session.UmacRow(umac.AnonymousUser); meta["anonymousUser"] = row; complete &= row["dataComplete"]!.GetValue<bool>(); }
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
            if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
            var matches = _session.CurrentPortal.ProjectServers.Where(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal)).Take(2).ToArray();
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
            => !dryRun && (action is "connectServer" or "disconnectServer" or "commit") && _session is Portal portal && portal.NeedsSharedLifecycle
                ? portal.SharedSessionOperation(() => ManageMultiuserSession(action, serverName, projectName, protocol, host, port, commitComment, offset, limit, confirmChange, dryRun))
                : _session.RunHmiStepTool("ManageMultiuserSession", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                bool mutation = ProjectSecurityLogic.ValidateMultiuser(action, serverName, projectName, protocol, host, port, commitComment, confirmChange, dryRun);
                ProjectSecurityLogic.ValidatePage(offset, limit);
                if (_session.CurrentPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                switch (action)
                {
                    case "read":
                    {
                        meta["projectServers"] = new JsonArray(_session.CurrentPortal.ProjectServers.Select(s => (JsonNode)EngineeringScalarProperties.Read(s)).ToArray());
                        meta["localSessions"] = new JsonArray(_session.CurrentPortal.LocalSessions.Select(s => (JsonNode)JsonValue.Create(s.Project?.Name)!).ToArray());
                        meta["boundSessionActive"] = _session.CurrentSession != null;
                        if (_session.CurrentSession != null)
                        {
                            var bound = new JsonObject { ["projectName"] = _session.CurrentSession.Project?.Name };
                            try { bound["isUpToDate"] = _session.CurrentSession.IsUptoDate(); } catch (Exception ex) { bound["isUpToDate"] = null; bound["isUpToDateError"] = ex.GetBaseException().Message; }
                            Markings markings = _session.CurrentSession.MarkingService.GetMarkings();
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
                        if (_session.CurrentPortal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("Server alias already configured: " + serverName);
                        var mode = (Protocol)Enum.Parse(typeof(Protocol), protocol);
                        meta["request"] = new JsonObject { ["alias"] = serverName, ["protocol"] = protocol, ["host"] = host, ["port"] = port };
                        if (dryRun) return "Project server connection preview; nothing created.";
                        meta["mayHaveChanged"] = true;
                        var created = _session.CurrentPortal.ProjectServers.Create(serverName, mode, host, port);
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
                        if (_session.CurrentPortal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("DeleteConnection returned but the alias is still configured.");
                        meta["verifiedAbsent"] = true;
                        return "Project server connection removed and verified absent.";
                    }
                    default: // commit
                    {
                        if (_session.CurrentSession == null) throw new PortalException(PortalErrorCode.InvalidState, "No bound local session; commit applies only to a multiuser local session opened via OpenSession.");
                        meta["sessionProject"] = _session.CurrentSession.Project?.Name; meta["commitComment"] = commitComment;
                        if (dryRun) return "Commit preview: CloseAndCommit would commit the local session changes to the server AND close the bound session. Nothing executed.";
                        meta["mayHaveChanged"] = true; meta["sessionClosed"] = false;
                        int committed = _session.CurrentSession.CloseAndCommit(commitComment);
                        meta["apiCallSuccess"] = true; meta["nativeResult"] = committed; meta["sessionClosed"] = true;
                        _session.ReleaseProject();
                        return "Local session committed and closed by the native CloseAndCommit; the MCP project binding was cleared. Re-open a session explicitly to continue.";
                    }
                }
            }, requiresProject: false);

        // ---- Offline comparison ----
    }
}
