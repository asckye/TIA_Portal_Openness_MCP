using System;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Hardware-network family pure logic (IO systems, sync/MRP domains, transfer areas, channels, addressing, device
    // users, port interconnections): parameter gating, JSON parsing, enum name catalogs and permission flag joining.
    internal static class HardwareNetworkTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            // JSON objects: scalar-only, bounded, identifier keys.
            check(HardwareNetworkLogic.ParseObject("", "p").Count == 0 && HardwareNetworkLogic.ParseObject("{}", "p").Count == 0, "hwnet: empty propertiesJson accepted");
            check(HardwareNetworkLogic.ParseObject("{\"Name\":\"IO1\",\"Number\":101,\"MultipleUseIoSystem\":true}", "p").Count == 3, "hwnet: scalar object parsed");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseObject("[1]", "p")), "hwnet: array refused as object");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseObject("{\"A\":{\"b\":1}}", "p")), "hwnet: nested object refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseObject("{\"bad key\":1}", "p")), "hwnet: non-identifier key refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseObject("{" + string.Join(",", Enumerable.Range(0, 51).Select(i => "\"k" + i + "\":1")) + "}", "p")), "hwnet: more than 50 entries refused");
            // JSON name arrays.
            check(HardwareNetworkLogic.ParseNames("", "n").Length == 0 && HardwareNetworkLogic.ParseNames("[]", "n").Length == 0, "hwnet: empty name array");
            check(HardwareNetworkLogic.ParseNames("[\"ReadTag\",\"DoDiagnosis\"]", "n").SequenceEqual(new[] { "ReadTag", "DoDiagnosis" }), "hwnet: names parsed in order");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseNames("[\"a\",\"a\"]", "n")), "hwnet: duplicate names refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseNames("[1]", "n")), "hwnet: non-string name refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseNames("[\"\"]", "n")), "hwnet: empty name refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ParseNames("{\"a\":1}", "n")), "hwnet: object refused as name array");
            // Permission flags join for Enum.Parse; None cannot be combined; case-sensitive names.
            check(HardwareNetworkLogic.JoinFlags(new[] { "ReadTag", "ModifyTag" }, HardwareNetworkLogic.WebserverPermissions, "p") == "ReadTag, ModifyTag", "hwnet: flags joined with comma-space");
            check(HardwareNetworkLogic.JoinFlags(new[] { "None" }, HardwareNetworkLogic.WebserverPermissions, "p") == "None", "hwnet: None alone accepted");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.JoinFlags(new[] { "None", "ReadTag" }, HardwareNetworkLogic.WebserverPermissions, "p")), "hwnet: None combined refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.JoinFlags(new[] { "readtag" }, HardwareNetworkLogic.WebserverPermissions, "p")), "hwnet: permission names are case-sensitive");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.JoinFlags(Array.Empty<string>(), HardwareNetworkLogic.WebserverPermissions, "p")), "hwnet: empty permission list refused");
            check(HardwareNetworkLogic.WebserverPermissions.Length == 20 && HardwareNetworkLogic.SimpleWebserverPermissions.SequenceEqual(new[] { "None", "ReadOnly", "ReadWrite" }), "hwnet: permission catalogs match the PublicAPI enums");
            check(HardwareNetworkLogic.SplitFlags("ReadTag, ModifyTag").SequenceEqual(new[] { "ReadTag", "ModifyTag" }) && HardwareNetworkLogic.SplitFlags(null).Length == 0, "hwnet: enum flag text split into names");
            // Passwords: bounded, never blank; never part of any output (only the length rule is testable here).
            check(HardwareNetworkLogic.RequirePassword("Admin123", "password") == "Admin123", "hwnet: password accepted unchanged");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.RequirePassword("", "password")) && Fails<ArgumentException>(() => HardwareNetworkLogic.RequirePassword("   ", "password")), "hwnet: empty/blank password refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.RequirePassword(new string('x', 129), "password")), "hwnet: oversized password refused");
            // Channel identity: all three or none.
            check(!HardwareNetworkLogic.ChannelIdentityGiven("", "", -1), "hwnet: no channel identity = enumerate");
            check(HardwareNetworkLogic.ChannelIdentityGiven("Analog", "Input", 0), "hwnet: full channel identity accepted");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ChannelIdentityGiven("Analog", "", 0)), "hwnet: partial channel identity refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ChannelIdentityGiven("analog", "Input", 0)), "hwnet: channel type is case-sensitive");
            check(HardwareNetworkLogic.ChannelTypes.SequenceEqual(new[] { "None", "Analog", "Digital", "Technology" }) && HardwareNetworkLogic.ChannelIoTypes.SequenceEqual(new[] { "None", "Input", "Output", "Complex" }), "hwnet: channel enum catalogs");
            // Positions and lengths.
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.RequirePosition(-1, 3)), "hwnet: extended position without position refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.RequirePosition(70000, -1)), "hwnet: position above 65535 refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.RequireLength(2000, "length")), "hwnet: length above 1024 refused");
            // Transfer area request gating.
            check(HardwareNetworkLogic.TransferAreaTypes.Length == 30 && HardwareNetworkLogic.TransferAreaTypes.Contains("DDX") && HardwareNetworkLogic.TransferAreaTypes.Contains("F_CD"), "hwnet: TransferAreaType catalog complete (30 members incl. DDX/F_CD)");
            check(HardwareNetworkLogic.TransferAreaDirections.SequenceEqual(new[] { "None", "LocalToPartner", "PartnerToLocal", "Bidirectional" }), "hwnet: TransferAreaDirection catalog");
            HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "create", "Input CD", "CD", -1, -1, -1, -1);
            HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "create", "Input", "IN", 5, -1, -1, -1);
            HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "delete", "", "", 4, 3, -1, -1);
            HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "updateMappingRule", "TA", "", -1, -1, -1, 0);
            HardwareNetworkLogic.ValidateTransferAreaRequest("multicast", "create", "", "DDX", -1, -1, 32, -1);
            HardwareNetworkLogic.ValidateTransferAreaRequest("multicast", "createReceiver", "", "DDX", -1, -1, -1, -1);
            check(true, "hwnet: valid transfer area requests accepted");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "create", "", "CD", -1, -1, -1, -1)), "hwnet: standard create without name refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "create", "X", "cd", -1, -1, -1, -1)), "hwnet: unknown transfer area type refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "createReceiver", "X", "DDX", -1, -1, -1, -1)), "hwnet: createReceiver refused for standard kind");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("multicast", "createMappingRule", "X", "", -1, -1, -1, -1)), "hwnet: mapping rules refused for multicast kind");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "deleteMappingRule", "X", "", -1, -1, -1, -1)), "hwnet: deleteMappingRule without ruleIndex refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("standard", "update", "", "", -1, -1, -1, -1)), "hwnet: update without name or position refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateTransferAreaRequest("ccdx", "create", "X", "DDX", -1, -1, -1, -1)), "hwnet: unknown kind refused");
            // Device user request gating per family.
            HardwareNetworkLogic.ValidateUserRequest("webserver", "read", "", "", Array.Empty<string>());
            HardwareNetworkLogic.ValidateUserRequest("webserver", "create", "user1", "Secret1", new[] { "ReadTag", "DoDiagnosis" });
            HardwareNetworkLogic.ValidateUserRequest("opcUa", "create", "user1", "Secret1", Array.Empty<string>());
            HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "setPermissions", "user1", "", new[] { "ReadOnly" });
            HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "rename", "user1", "", Array.Empty<string>(), "operator");
            HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "setActive", "user1", "", Array.Empty<string>());
            check(true, "hwnet: valid device user requests accepted");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateUserRequest("webserver", "create", "user1", "", new[] { "ReadTag" })), "hwnet: create without password refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateUserRequest("webserver", "create", "user1", "Secret1", Array.Empty<string>())), "hwnet: webserver create without permissions refused");
            check(Fails<NotSupportedException>(() => HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "create", "user1", "Secret1", new[] { "ReadOnly" })), "hwnet: SIWAREX create refused (no Create in the API)");
            check(Fails<NotSupportedException>(() => HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "delete", "user1", "", Array.Empty<string>())), "hwnet: SIWAREX delete refused (no Delete in the API)");
            check(Fails<NotSupportedException>(() => HardwareNetworkLogic.ValidateUserRequest("webserver", "rename", "user1", "", Array.Empty<string>(), "x")), "hwnet: rename refused outside simpleWebserver (UserName read-only)");
            check(Fails<NotSupportedException>(() => HardwareNetworkLogic.ValidateUserRequest("opcUa", "setPermissions", "user1", "", new[] { "ReadTag" })), "hwnet: OPC UA permissions refused (none in the API)");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "setPermissions", "user1", "", new[] { "ReadOnly", "ReadWrite" })), "hwnet: SIWAREX permissions take exactly one value");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateUserRequest("simpleWebserver", "rename", "user1", "", Array.Empty<string>(), "")), "hwnet: rename without newName refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateUserRequest("webserver", "setPassword", " user ", "Secret1", Array.Empty<string>())), "hwnet: padded user name refused");
            // Domain / IO system gating.
            HardwareNetworkLogic.ValidateDomainRequest("sync", "create", "Sync-Domain_2"); HardwareNetworkLogic.ValidateDomainRequest("mrp", "addParticipant", "mrpdomain-1");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateDomainRequest("irt", "create", "x")), "hwnet: unknown domain kind refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateDomainRequest("sync", "removeParticipant", "x")), "hwnet: removeParticipant refused (API has no removal)");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateDomainRequest("sync", "create", "")), "hwnet: domain create without name refused");
            HardwareNetworkLogic.ValidateIoSystemRequest("create", "", "", ""); HardwareNetworkLogic.ValidateIoSystemRequest("connect", "", "PN/IE_1", "PROFINET IO-System");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateIoSystemRequest("connect", "", "PN/IE_1", "")), "hwnet: connect without ioSystemName refused");
            check(Fails<ArgumentException>(() => HardwareNetworkLogic.ValidateIoSystemRequest("attach", "", "", "")), "hwnet: unknown IO system action refused");
            check(HardwareNetworkLogic.IoSystemActions.SequenceEqual(new[] { "create", "delete", "update", "connect", "disconnect" }) && HardwareNetworkLogic.PortActions.SequenceEqual(new[] { "read", "connect", "disconnect" }), "hwnet: action catalogs");
            check(HardwareNetworkLogic.AddressIoTypes.SequenceEqual(new[] { "None", "Input", "Output", "Substitute", "Diagnosis" }), "hwnet: AddressIoType catalog");
            // Official dynamic attribute lists (names from the V21 chapters).
            check(HardwareNetworkLogic.IoSystemAttributes.Contains("MultipleUseIoSystem") && HardwareNetworkLogic.IoConnectorAttributes.Contains("PnUpdateTime") && HardwareNetworkLogic.IoControllerAttributes.SequenceEqual(new[] { "SyncRole", "PnDeviceNumber" }), "hwnet: IO system/controller/connector attribute names");
            check(HardwareNetworkLogic.SyncDomainAttributes.SequenceEqual(new[] { "HighPerformanceActive", "FastForwardingActive" }) && HardwareNetworkLogic.MrpDomainAttributes.Contains("ManagerOutsideOfProjectActive"), "hwnet: domain attribute names");
            check(HardwareNetworkLogic.TransferAreaAttributes.Contains("TransferUpdateTime") && HardwareNetworkLogic.ChannelAttributes.SequenceEqual(new[] { "ChannelAddress", "ChannelWidth" }) && HardwareNetworkLogic.AddressAttributes.Contains("ProcessImage"), "hwnet: transfer area/channel/address attribute names");
        }
    }
}
