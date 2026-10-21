using System.Collections.Generic;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class SessionCandidateContract : IBehaviorCandidateContract
    {
        public static readonly IReadOnlyList<string> Entries = Array.AsReadOnly(new string[] { "ConnectPortal", "ConnectIsolatedPortal", "ConnectProject", "AttachOpenProject", "OpenProject" });
        public const string Description = "[Portal][SESSION] Explicit PID/start-time session candidate. Default preview; attach never binds or starts TIA. Bind an exact open project with explicit reuseOpen=true. Open only in an empty attached process. Upgrade defaults reject; allow requires a complete independent copyPath and separate confirmUpgrade. Apply needs confirm and the reviewed hash/project; one native action, no close or retry. Unknown requires session reset. Test/accepted behaviorPolicy=safe-v4.";
        protected virtual string Entry => "ConnectPortal";
        public JsonElement InputSchema => Schema(Entry);
        public static string Action(string entry) => entry == "OpenProject" ? "open" : entry == "AttachOpenProject" || entry == "ConnectProject" ? "bind" : "attach";
        public static JsonElement Schema(string tool)
        {
            var properties = new JsonObject { ["processId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }, ["processStartUtc"] = new JsonObject { ["type"] = "string" } };
            var required = new JsonArray("processId", "processStartUtc");
            void Text(string key, bool needed = false) { properties[key] = new JsonObject { ["type"] = "string" }; if (needed) required.Add(key); else properties[key]!["default"] = ""; }
            void Flag(string key) => properties[key] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            if (Action(tool) != "attach") Text("projectPath", true);
            Flag("startNew"); Flag("reuseOpen");
            properties["upgrade"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("reject", "allow"), ["default"] = "reject" };
            Text("copyPath"); Flag("confirmUpgrade");
            properties["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" };
            Flag("confirm"); Text("expectedPlanHash"); Text("expectedProjectFile");
            // Protected projects (V15.1+): credentials travel beside the reviewed request, never inside it, its plan or its hash.
            if (Action(tool) == "open") { Text("umacUserName"); Text("umacPassword"); Text("umacUserType"); }
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required });
        }
    }
    public sealed class SessionIsolatedContract : SessionCandidateContract { protected override string Entry => "ConnectIsolatedPortal"; }
    public sealed class SessionBindContract : SessionCandidateContract { protected override string Entry => "ConnectProject"; }
    public sealed class SessionAttachProjectContract : SessionCandidateContract { protected override string Entry => "AttachOpenProject"; }
    public sealed class SessionOpenContract : SessionCandidateContract { protected override string Entry => "OpenProject"; }
}
