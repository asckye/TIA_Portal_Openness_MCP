using System;

namespace TiaMcp.Adapters.Contracts;

public static class PlcTechnologyRefusal
{
    public static bool Record(HardwareAddressingReply result, string typeIdentifier, string version)
    {
        var meta = result.Meta;
        if (result.OutcomeUnknown || result.MayHaveChanged
            || !meta.TryGetValue("operationSuccess", out var success) || success is not false
            || !meta.TryGetValue("rollbackOutcome", out var outcome) || outcome as string != "restored"
            || !meta.TryGetValue("rollbackCompleted", out var completed) || completed is not true
            || !meta.TryGetValue("nativeMessage", out var native) || native is not string text) return false;
        string diagnostic = $"The type or version of the library element \"{typeIdentifier}.{version}\" does not exist or is not a valid technology object";
        if (text.IndexOf(diagnostic, StringComparison.OrdinalIgnoreCase) < 0) return false;
        meta["status"] = "PreconditionFailed";
        meta["precondition"] = "technology-type-version-unavailable";
        meta["nothingCreated"] = true;
        result.Message = $"TIA has no technology object '{typeIdentifier}' version {version} for this CPU. "
            + "Openness offers no list of available versions. In TIA open the PLC's Technology objects > Add new object, "
            + "select the type and use a version it offers (or read an existing object with action=read).";
        meta["refusal"] = result.Message;
        return true;
    }
}
