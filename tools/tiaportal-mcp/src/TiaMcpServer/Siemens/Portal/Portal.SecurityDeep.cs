using System;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Security;
using Siemens.Engineering.Umac;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Phase 3 sub-batch 3 (2.7.32): project-global syslog servers, password policies and UMC users / groups / server.
    // Official pages: "Managing Syslog configuration" (PLC service), "Setting password policies for UMAC", "Setting password
    // policy for PLC", "Functions for UMAC Global Users and UMC Server" (offline users, import, activate, delete, roles,
    // authentication, synchronize). Everything is the official V20/V21 Siemens.Engineering.Security / .Umac / .HW.Features API.
    public partial class Portal
    {
        // ---- syslog ---------------------------------------------------------------------------------------------------------
        private static JsonObject SyslogServerRow(SyslogServer server)
        {
            var row = new JsonObject { ["name"] = server.Name, ["address"] = server.Address, ["port"] = server.Port, ["tls"] = server.Tls, ["comment"] = server.Comment };
            try { row["assignedModules"] = new JsonArray(EngineeringGroupOperations.Items(server.AssignedModules).Cast<DeviceItem>().Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["ownerPath"] = HardwareOwnerPath(m) }).ToArray()); }
            catch (Exception ex) { row["assignedModulesError"] = ex.GetBaseException().Message; }
            return row;
        }
        private static JsonObject SyslogManagerRow(SysLogConfigurationManager manager, JsonObject meta)
        {
            var row = new JsonObject { ["enableSystemLogging"] = manager.EnableSystemLogging, ["transportProtocol"] = manager.TransportProtocol.ToString() };
            row["servers"] = new JsonArray(EngineeringGroupOperations.Items(manager.SysLogServerConfiguration).Cast<SysLogServerConfiguration>()
                .Select(s => (JsonNode)new JsonObject { ["address"] = s.SysLogServerAddress, ["port"] = s.SysLogServerPort }).ToArray());
            if (manager.OwnedBy is IEngineeringObject owner) row["attributes"] = DynamicAttributes(owner, SecurityDeepLogic.SyslogPlcAttributes);
            return row;
        }

        public ResponseMessage ManageSyslogServers(string scope = "project", string action = "read", string name = "", string propertiesJson = "{}", string attributesJson = "{}",
            string devicePathJson = "[]", string itemPathJson = "[]", string serverAddress = "", int serverPort = 0, bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageSyslogServers", meta => {
                var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); var attributes = HardwareNetworkLogic.ParseObject(attributesJson, "attributesJson");
                SecurityDeepLogic.ValidateSyslogRequest(scope, action, name, properties, devicePathJson, serverAddress, serverPort, confirmDelete, dryRun);
                if (attributes.Count > 0 && (scope != "plc" || action != "update")) throw new ArgumentException("attributesJson (SysLogAutoAcceptClient / SysLogClientCertificateId / SysLogTrustedCertificateIds) applies to scope=plc action=update only.");
                if (scope == "project" && action == "create" && !dryRun) throw new NotSupportedException(SecurityDeepLogic.ProjectSyslogCreateRefusal);
                bool write = action != "read" && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                meta["scope"] = scope; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (scope == "plc")
                {
                    var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                    var manager = RequireHardwareService<SysLogConfigurationManager>(owner, "itemPathJson");
                    meta["ownerPath"] = HardwareOwnerPath(owner); meta["before"] = SyslogManagerRow(manager, meta);
                    if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["readScope"] = "EnableSystemLogging, TransportProtocol, SysLogServerConfiguration rows and the three official dynamic attributes of the CPU item (failures listed per name)."; return "PLC syslog configuration read; no modification."; }
                    var existing = string.IsNullOrEmpty(serverAddress) ? null : manager.SysLogServerConfiguration.Find(serverAddress);
                    if (action == "createServer" && existing != null) throw new InvalidOperationException("A syslog server with this address exists on the CPU: " + serverAddress);
                    if (action == "deleteServer" && existing == null) throw new PortalException(PortalErrorCode.NotFound, "No syslog server with address " + serverAddress + " on the CPU (SysLogServerConfigurationComposition.Find).");
                    meta["requestedProperties"] = properties.DeepClone(); meta["requestedAttributes"] = attributes.DeepClone();
                    if (!write) return "PLC syslog " + action + " preview; nothing changed (S7-1500 firmware 3.1 or later; TLS protocols need certificate attributes).";
                    meta["mayHaveChanged"] = true;
                    switch (action)
                    {
                        case "update":
                            if (properties.TryGetPropertyValue("EnableSystemLogging", out var enable)) { manager.EnableSystemLogging = enable!.GetValue<bool>(); if (manager.EnableSystemLogging != enable.GetValue<bool>()) throw new InvalidOperationException("EnableSystemLogging readback differs."); }
                            if (properties.TryGetPropertyValue("TransportProtocol", out var protocol)) { var value = (SysLogTransportProtocolType)Enum.Parse(typeof(SysLogTransportProtocolType), protocol!.ToString()); manager.TransportProtocol = value; if (manager.TransportProtocol != value) throw new InvalidOperationException("TransportProtocol readback differs."); }
                            if (attributes.Count > 0) SetDynamicAttributes((IEngineeringObject)owner, attributes, meta);
                            break;
                        case "createServer":
                            var created = manager.SysLogServerConfiguration.Create(serverAddress, (ushort)serverPort);
                            meta["created"] = new JsonObject { ["address"] = created.SysLogServerAddress, ["port"] = created.SysLogServerPort };
                            if (manager.SysLogServerConfiguration.Find(serverAddress) == null) throw new InvalidOperationException("Create returned but Find(address) does not see the new server.");
                            break;
                        case "deleteServer":
                            existing!.Delete();
                            if (manager.SysLogServerConfiguration.Find(serverAddress) != null) throw new InvalidOperationException("Delete returned but the server is still found by address.");
                            meta["verifiedAbsent"] = true; break;
                    }
                    meta["after"] = SyslogManagerRow(manager, meta); meta["apiCallSuccess"] = true;
                    return "PLC syslog " + action + " executed and read back; no save/compile/download.";
                }
                // scope=project: Siemens.Engineering.Security.SyslogServerProvider (project service), servers are global objects assignable to modules.
                if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Attach a project first.");
                var provider = _project!.GetService<SyslogServerProvider>() ?? throw new NotSupportedException("SyslogServerProvider service unavailable on this project (project-global syslog servers need TIA V19 or later).");
                SyslogServerComposition composition = provider.Servers;
                Func<object> fresh = () => (_project!.GetService<SyslogServerProvider>() ?? provider).Servers;
                var servers = EngineeringGroupOperations.Items(composition).Cast<SyslogServer>().ToArray(); meta["countBefore"] = servers.Length;
                if (action == "read")
                {
                    var rows = string.IsNullOrEmpty(name) ? servers : new[] { servers.FirstOrDefault(s => s.Name == name) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact syslog server not found: " + name) };
                    meta["records"] = new JsonArray(rows.Select(s => (JsonNode)SyslogServerRow(s)).ToArray()); meta["actualCount"] = rows.Length;
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; meta["readScope"] = "Name, Address, Port, Tls, Comment and AssignedModules (DeviceItem names with owner paths) of every project-global syslog server.";
                    return "Project syslog servers read; no modification.";
                }
                var server = servers.FirstOrDefault(s => s.Name == name);
                if ((action == "create") == (server != null)) throw new InvalidOperationException(action == "create" ? "Syslog server exists: " + name : "Syslog server not found: " + name);
                if (server != null) meta["before"] = SyslogServerRow(server);
                meta["requestedProperties"] = properties.DeepClone();
                DeviceItem? module = null;
                if (action == "assignModule" || action == "unassignModule")
                {
                    module = ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("assignModule/unassignModule need a DeviceItem (non-empty itemPathJson).");
                    meta["module"] = new JsonObject { ["name"] = module.Name, ["ownerPath"] = HardwareOwnerPath(module) };
                    bool assigned = server!.AssignedModules.Contains(module); meta["assignedBefore"] = assigned;
                    if (action == "assignModule" && assigned) throw new InvalidOperationException("Module is already assigned to this syslog server.");
                    if (action == "unassignModule" && !assigned) throw new InvalidOperationException("Module is not assigned to this syslog server.");
                }
                var prepared = EngineeringScalarProperties.Prepare(typeof(SyslogServer), properties);
                if (!write) return action == "create" ? "Project syslog server create preview (real creation is disabled: " + SecurityDeepLogic.ProjectSyslogCreateRefusal + ")" : "Project syslog server " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "create":
                        server = composition.Create(name); meta["createdName"] = server.Name;
                        if (prepared.Count > 0) EngineeringScalarProperties.Apply(server, prepared, meta);
                        meta["foundByNameAfterCreate"] = FindOnFresh(fresh, server.Name, meta, "afterCreate") != null;
                        if (CountOnFresh(fresh, meta, "countAfter") is int grown && grown != servers.Length + 1) throw new InvalidOperationException("Create returned but the refreshed server count did not grow by one.");
                        break;
                    case "update":
                        EngineeringScalarProperties.Apply(server!, prepared, meta); break;
                    case "delete":
                        server!.Delete();
                        if (FindOnFresh(fresh, name, meta, "afterDelete") != null) throw new InvalidOperationException("Delete returned but the syslog server is still listed on the refreshed composition.");
                        meta["verifiedAbsent"] = true; CountOnFresh(fresh, meta, "countAfter");
                        return "Project syslog server deleted and verified absent; no save/compile/download.";
                    case "assignModule":
                        server!.AssignedModules.Add(module!);
                        if (!server.AssignedModules.Contains(module!)) throw new InvalidOperationException("Add returned but AssignedModules does not contain the module.");
                        break;
                    case "unassignModule":
                        if (!server!.AssignedModules.Remove(module!) || server.AssignedModules.Contains(module!)) throw new InvalidOperationException("Remove returned false or the module is still assigned.");
                        break;
                }
                meta["after"] = SyslogServerRow(server!); meta["apiCallSuccess"] = true;
                return "Project syslog server " + action + " executed and read back; no save/compile/download.";
            });

        // ---- password policies -------------------------------------------------------------------------------------------------
        private JsonObject PolicyRow(string target)
        {
            object? service = target switch
            {
                "umac" => _project!.GetService<PasswordPolicyConfigurator>(),
                "plc" => _project!.GetService<PlcPasswordPolicyService>(),
                _ => _project!.GetService<LegacyPlcPasswordPolicyService>()
            };
            var row = new JsonObject { ["target"] = target, ["service"] = target == "umac" ? "Umac.PasswordPolicyConfigurator" : target == "plc" ? "Security.PlcPasswordPolicyService" : "Security.LegacyPlcPasswordPolicyService", ["available"] = service != null };
            if (service == null) return row;
            try
            {
                row["values"] = service switch
                {
                    PasswordPolicyConfigurator umac => new JsonObject
                    {
                        ["IncludesLowerCaseAndUpperCaseCharacters"] = umac.IncludesLowerCaseAndUpperCaseCharacters, ["MinimumLength"] = umac.MinimumLength, ["MinimumNumericCharacterLength"] = umac.MinimumNumericCharacterLength,
                        ["MinimumSpecialCharacterLength"] = umac.MinimumSpecialCharacterLength, ["EnablePasswordAging"] = umac.EnablePasswordAging, ["MinimumUserPasswordsBlockedForReuse"] = umac.MinimumUserPasswordsBlockedForReuse,
                        ["PasswordValidity"] = umac.PasswordValidity, ["PasswordValidityPrewarningTime"] = umac.PasswordValidityPrewarningTime
                    },
                    PlcPasswordPolicyService plc => new JsonObject { ["PasswordPolicyEnabled"] = plc.PasswordPolicyEnabled },
                    LegacyPlcPasswordPolicyService legacy => new JsonObject
                    {
                        ["PasswordPolicyEnabled"] = legacy.PasswordPolicyEnabled, ["MinimumLength"] = legacy.MinimumLength, ["MinimumNumericCharacterLength"] = legacy.MinimumNumericCharacterLength,
                        ["MinimumSpecialCharacterLength"] = legacy.MinimumSpecialCharacterLength, ["IncludesLowerCaseAndUpperCaseCharacters"] = legacy.IncludesLowerCaseAndUpperCaseCharacters
                    },
                    _ => throw new NotSupportedException("Unexpected policy service " + service.GetType().FullName)
                };
            }
            catch (Exception ex) when (ex is not NotSupportedException) { row["valuesError"] = ex.GetBaseException().Message; }
            return row;
        }

        public ResponseMessage ManagePasswordPolicy(string action = "read", string target = "", string propertiesJson = "{}", bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("ManagePasswordPolicy", meta => {
                var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson");
                SecurityDeepLogic.ValidatePolicyRequest(action, target, properties, confirmChange, dryRun);
                bool write = action == "update" && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read")
                {
                    var targets = string.IsNullOrEmpty(target) ? SecurityDeepLogic.PolicyTargets : new[] { target };
                    meta["records"] = new JsonArray(targets.Select(t => (JsonNode)PolicyRow(t)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    meta["scope"] = "umac = project users (PasswordPolicyConfigurator, 8 properties), plc = S7-1200/1500 (PlcPasswordPolicyService.PasswordPolicyEnabled: the UMAC complexity applies to PLC passwords), legacyPlc = S7-300/400 / ET 200S / WinAC (LegacyPlcPasswordPolicyService, own ranges). Passwords themselves are never readable.";
                    return "Password policies read; no modification.";
                }
                object service = (target switch
                {
                    "umac" => (object?)_project!.GetService<PasswordPolicyConfigurator>(),
                    "plc" => _project!.GetService<PlcPasswordPolicyService>(),
                    _ => _project!.GetService<LegacyPlcPasswordPolicyService>()
                }) ?? throw new NotSupportedException("Password policy service for target=" + target + " is unavailable on this project.");
                meta["target"] = target; meta["before"] = PolicyRow(target); meta["requestedProperties"] = properties.DeepClone();
                var prepared = EngineeringScalarProperties.Prepare(service.GetType(), properties);
                if (!write) return "Password policy update preview; nothing changed (out-of-range values raise PasswordPolicySettingsException natively; legacy ranges are checked before the call).";
                meta["mayHaveChanged"] = true;
                EngineeringScalarProperties.Apply(service, prepared, meta);
                meta["after"] = PolicyRow(target); meta["apiCallSuccess"] = true;
                return "Password policy updated and read back; no save. Existing passwords are not re-validated by the API.";
            });

        // ---- UMC users / groups / server -----------------------------------------------------------------------------------------
        private static JsonObject UmcMemberRow(object member)
        {
            var row = UmacRow(member);
            if (member is UmcUser user) { row["name"] = user.Name; row["isActive"] = user.IsActive; try { row["domainId"] = user.DomainId; } catch (Exception ex) { row["domainIdError"] = ex.GetBaseException().Message; } }
            if (member is UmcUserGroup group) { row["name"] = group.Name; row["isActive"] = group.IsActive; row["description"] = group.Description; try { row["domainId"] = group.DomainId; } catch (Exception ex) { row["domainIdError"] = ex.GetBaseException().Message; } }
            return row;
        }
        private UmcServerConfigurator RequireUmcServerConfigurator()
            => _project!.GetService<UmcServerConfigurator>() ?? throw new NotSupportedException("UmcServerConfigurator service unavailable on this project (available on protected and unprotected projects from TIA V17; one preconfigured UMC server).");
        // UmcServer.GetUserByName / GetUserGroupByName raise the Authentication event; the credentials go in as SecureString and are never echoed.
        private static EventHandler<UmcAuthenticationEventArgs>? UmcAuthenticationHandler(string serverUserName, SecureString? password, JsonObject meta)
        {
            if (password == null) return null;
            return (sender, e) => { meta["authenticationEventRaised"] = true; e.UmcCredentials.Name = serverUserName; e.UmcCredentials.SetPassword(password); };
        }

        public ResponseMessage ManageUmcUsers(string kind = "user", string action = "read", string name = "", string newName = "", string roleName = "",
            string serverUserName = "", string serverPassword = "", bool confirmChange = false, bool dryRun = true, int offset = 0, int limit = 100)
            => RunHmiStepTool("ManageUmcUsers", meta => {
                var request = SecurityDeepLogic.ValidateUmcRequest(kind, action, name, newName, roleName, serverUserName, serverPassword, confirmChange, dryRun);
                HardwareServicesLogic.ValidatePagination(offset, limit);
                bool write = request.Writes && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                meta["kind"] = kind; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["credentialsProvided"] = !string.IsNullOrEmpty(serverPassword);
                SecureString? secure = string.IsNullOrEmpty(serverPassword) ? null : PlcBlockServicesLogic.ToSecureString(serverPassword);
                try
                {
                    if (kind == "server")
                    {
                        var configurator = RequireUmcServerConfigurator(); var server = configurator.UmcServer;
                        meta["umcServer"] = server == null ? null : EngineeringScalarProperties.Read(server);
                        if (action == "read")
                        {
                            try { meta["attributeNames"] = new JsonArray(server == null ? Array.Empty<JsonNode>() : EngineeringGroupOperations.Items(server.GetAttributeInfos()).Select(i => (JsonNode)new JsonObject { ["name"] = i.GetType().GetProperty("Name")?.GetValue(i)?.ToString(), ["accessMode"] = i.GetType().GetProperty("AccessMode")?.GetValue(i)?.ToString() }).ToArray()); }
                            catch (Exception ex) { meta["attributeNamesError"] = ex.GetBaseException().Message; }
                            meta["apiCallSuccess"] = true; meta["dataComplete"] = server != null;
                            return "UMC server configurator read; the server object carries no public scalars (users/groups are fetched by name with credentials).";
                        }
                        if (server == null) throw new PortalException(PortalErrorCode.NotFound, "UmcServerConfigurator.UmcServer is null; no UMC server is configured for this project.");
                        var handler = UmcAuthenticationHandler(serverUserName, secure, meta);
                        if (handler != null) server.Authentication += handler;
                        try
                        {
                            if (action == "checkConsistency") { meta["consistent"] = configurator.CheckConsistency(); meta["apiCallSuccess"] = true; return "UmcServerConfigurator.CheckConsistency executed (true = the project copy of the UMC data matches the server)."; }
                            if (!write) return "Synchronize preview; nothing changed (Synchronize refreshes the project's UMC users/groups from the UMC server and needs a reachable server).";
                            meta["mayHaveChanged"] = true; configurator.Synchronize(); meta["apiCallSuccess"] = true;
                            var umacAfter = RequireUmac();
                            meta["countsAfter"] = new JsonObject { ["umcUsers"] = EngineeringGroupOperations.Items(umacAfter.UmcUsers).Count(), ["umcUserGroups"] = EngineeringGroupOperations.Items(umacAfter.UmcUserGroups).Count() };
                            return "UMC data synchronized with the server; counts read back. No save.";
                        }
                        finally { if (handler != null) server.Authentication -= handler; }
                    }
                    var umac = RequireUmac(); bool users = kind == "user";
                    UmcUserComposition umcUsers = umac.UmcUsers; UmcUserGroupComposition umcGroups = umac.UmcUserGroups;
                    Func<object> fresh = () => users ? (object)RequireUmac().UmcUsers : RequireUmac().UmcUserGroups;
                    var members = EngineeringGroupOperations.Items(fresh()).ToArray(); meta["countBefore"] = members.Length;
                    if (action == "read")
                    {
                        var rows = string.IsNullOrEmpty(name) ? members : new[] { FindOrdinal(members, name, kind) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact UMC " + kind + " not found in the project: " + name) };
                        var page = rows.Skip(offset).Take(limit).Select(m => (JsonNode)UmcMemberRow(m)).ToArray();
                        ProjectSecurityLogic.PageMeta(meta, rows.Length, offset, limit, page.Length); meta["records"] = new JsonArray(page);
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && page.Length == rows.Length;
                        meta["scope"] = "UMC " + kind + "s present in the project: Name, IsActive, DomainId (empty for offline members), Description (groups) and role names.";
                        return "UMC " + kind + "s read; no modification.";
                    }
                    var existing = FindOrdinal(members, name, kind);
                    if (request.Creates && existing != null) throw new InvalidOperationException("UMC " + kind + " already exists in the project: " + name + " (offline creation of an existing name throws EngineeringTargetInvocationException natively).");
                    if (!request.Creates && existing == null) throw new PortalException(PortalErrorCode.NotFound, "Exact UMC " + kind + " not found in the project: " + name);
                    if (existing != null) meta["before"] = UmcMemberRow(existing);
                    Role? role = request.NeedsRole ? ExactUmacRole(umac, roleName) : null;
                    if (role != null) meta["role"] = new JsonObject { ["name"] = role.Name, ["roleClass"] = role.GetType().Name };
                    if (!write) return "UMC " + kind + " " + action + " preview; objects validated, nothing written.";
                    meta["mayHaveChanged"] = true;
                    var user = existing as UmcUser; var group = existing as UmcUserGroup; object? created = null;
                    switch (action)
                    {
                        case "createOffline":
                            if (users) created = umcUsers.CreateOfflineUmcUser(name);
                            else { var newGroup = umcGroups.CreateOfflineUmcUserGroup(); meta["nativeNameBeforeSetName"] = newGroup.Name; newGroup.SetName(name); created = newGroup; }
                            break;
                        case "importFromServer":
                        {
                            var server = RequireUmcServerConfigurator().UmcServer ?? throw new PortalException(PortalErrorCode.NotFound, "UmcServerConfigurator.UmcServer is null; no UMC server is configured for this project.");
                            var handler = UmcAuthenticationHandler(serverUserName, secure, meta)!; server.Authentication += handler;
                            try
                            {
                                if (users) { UmcUserInfo info = server.GetUserByName(name) ?? throw new PortalException(PortalErrorCode.NotFound, "UMC server returned no user named " + name); meta["serverInfo"] = new JsonObject { ["name"] = info.Name }; created = umcUsers.Create(info); }
                                else { UmcUserGroupInfo info = server.GetUserGroupByName(name) ?? throw new PortalException(PortalErrorCode.NotFound, "UMC server returned no user group named " + name); meta["serverInfo"] = new JsonObject { ["userGroupName"] = info.UserGroupName, ["description"] = info.Description }; created = umcGroups.Create(info); }
                            }
                            finally { server.Authentication -= handler; }
                            break;
                        }
                        case "rename": if (users) user!.SetName(newName); else group!.SetName(newName); break;
                        case "activate": if (users) user!.Activate(); else group!.Activate(); break;
                        case "deactivate": if (users) user!.Deactivate(); else group!.Deactivate(); break;
                        case "delete": if (users) user!.Delete(); else group!.Delete(); break;
                        case "assignRole": if (users) user!.Roles.Add(role!); else group!.Roles.Add(role!); break;
                        case "unassignRole": if (!(users ? user!.Roles.Remove(role!) : group!.Roles.Remove(role!))) throw new InvalidOperationException("Roles.Remove returned false; the role was not assigned."); break;
                    }
                    meta["apiCallSuccess"] = true;
                    string lookup = request.NeedsNewName ? newName : name;
                    if (request.Deletes)
                    {
                        if (FindOnFresh(fresh, name, meta, "afterDelete") != null) throw new InvalidOperationException("Delete returned but the UMC " + kind + " is still listed on the refreshed composition.");
                        meta["verifiedAbsent"] = true; CountOnFresh(fresh, meta, "countAfter");
                        return "UMC " + kind + " deleted from the project and verified absent; no save.";
                    }
                    var readback = created ?? FindOnFresh(fresh, lookup, meta, "afterChange") ?? throw new InvalidOperationException("Native call returned but the UMC " + kind + " cannot be read back as " + lookup + ".");
                    if (created != null) { meta["createdName"] = EngineeringGroupOperations.Get(created, "Name").ToString(); meta["foundByNameAfterCreate"] = FindOnFresh(fresh, lookup, meta, "afterCreate") != null; CountOnFresh(fresh, meta, "countAfter"); }
                    var after = UmcMemberRow(readback); meta["after"] = after;
                    bool verified = action switch
                    {
                        "activate" => after["isActive"]!.GetValue<bool>(),
                        "deactivate" => !after["isActive"]!.GetValue<bool>(),
                        "rename" => after["name"]!.GetValue<string>() == newName && FindOnFresh(fresh, name, meta, "oldName") == null,
                        "assignRole" => after["roles"]!.AsArray().Any(x => x!.GetValue<string>() == role!.Name),
                        "unassignRole" => !after["roles"]!.AsArray().Any(x => x!.GetValue<string>() == role!.Name),
                        _ => true
                    };
                    if (!verified) throw new InvalidOperationException("Native call returned but readback does not show the requested state for " + action + ".");
                    return "UMC " + kind + " " + action + " executed and read back; no save.";
                }
                finally { secure?.Dispose(); }
            });
    }
}
