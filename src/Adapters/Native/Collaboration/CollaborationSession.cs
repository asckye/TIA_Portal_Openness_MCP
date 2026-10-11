using System;
using System.Collections.Generic;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Collaboration;

internal sealed class CollaborationSession
{
    private readonly PlcFoundationEngine engine;

    internal CollaborationSession(PlcFoundationEngine engine) => this.engine = engine;

    internal TiaPortal? CurrentPortal => engine.CollaborationPortal;
    internal IEngineeringServiceProvider? CurrentProject => engine.CollaborationProject;

    internal VersionControlResult RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
    {
        var reply = engine.RunHmiStep(tool, meta =>
        {
            var values = new HmiObject(meta);
            try { return action(values); }
            finally
            {
                meta.Clear();
                foreach (var pair in values) meta[pair.Key] = pair.Value;
            }
        }, requiresProject);
        return new VersionControlResult { Message = reply.Message, Meta = reply.Meta,
            RequiresSessionReset = reply.RequiresSessionReset
                || reply.Meta.TryGetValue("mayHaveChanged", out var changed) && changed is true
                    && reply.Meta.TryGetValue("operationSuccess", out var success) && success is false };
    }
}
