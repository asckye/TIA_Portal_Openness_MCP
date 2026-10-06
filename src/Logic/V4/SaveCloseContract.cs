using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class SaveCloseContract : IBehaviorCandidateContract
    {
        public static readonly string[] Entries = { "SaveProject", "SaveProjectCopy", "CloseProject", "DisconnectPortal" };
        public const string Description = "[Project][CLOSE] Explicit save/close candidate. Default preview; apply requires confirm, expectedPlanHash and expectedProjectFile. Close defaults saveChanges=false/discardChanges=false, never saves and refuses borrowed projects. Dirty close needs an explicit save first, or fresh discard preview plus confirmDiscard. LocalSession uses local Save/Close; copy is ordinary projects only. Disconnect only detaches a non-owning attachment, with owned projects closed explicitly first. One native action, no retry; unknown requires session reset. Test/accepted behaviorPolicy=safe-v4.";
        protected virtual string Entry => "SaveProject";
        public JsonElement InputSchema => Schema(Entry);
        public static string Action(string entry) => entry == "SaveProject" ? "save" : entry == "SaveProjectCopy" ? "save-copy" : entry == "CloseProject" ? "close" : "disconnect";
        public static JsonElement Schema(string tool)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            void Flag(string key) => properties[key] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            void Text(string key, bool needed = false) { properties[key] = new JsonObject { ["type"] = "string" }; if (needed) required.Add(key); else properties[key]!["default"] = ""; }
            if (tool == "CloseProject") { Flag("saveChanges"); Flag("discardChanges"); Flag("confirmDiscard"); }
            if (tool == "SaveProjectCopy") Text("newProjectPath", true);
            properties["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" };
            Flag("confirm"); Text("expectedPlanHash"); Text("expectedProjectFile");
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required });
        }
    }
    public sealed class SaveCopyContract : SaveCloseContract { protected override string Entry => "SaveProjectCopy"; }
    public sealed class CloseCandidateContract : SaveCloseContract { protected override string Entry => "CloseProject"; }
    public sealed class DisconnectCandidateContract : SaveCloseContract { protected override string Entry => "DisconnectPortal"; }
}
