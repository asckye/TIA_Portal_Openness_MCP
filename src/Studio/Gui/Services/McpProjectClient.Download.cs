using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

internal sealed record ProjectDownloadPlan(string SessionKey, string Epoch, string Project, string Plc, string Address,
    string Adapter, string Scope, string Hash);

public sealed partial class McpProjectClient
{
    private string? BoundProject => (string?)Session.State?["binding"]?["projectFile"];
    private string BindingEpoch => Session.State?["binding"]?["bindingEpoch"]?.ToString() ?? "";
    internal (string? Session, string? Project, string Epoch) DownloadBinding => (Session.SessionKey, BoundProject, BindingEpoch);
    internal bool CanPreviewDownload => Session.SessionKey != null && !string.IsNullOrWhiteSpace(BoundProject)
        && Supports("ListTransferRoutes", "softwarePath") && Supports("CheckDownloadReadiness", "softwarePath")
        && Supports("DownloadPlc", "confirmPhysicalTarget")
        && Supports("DownloadPlc", "expectedPlanHash") && Supports("DownloadPlc", "expectedProjectFile");
    internal bool SupportsScope(string scope) => OnlineCapabilities.Unsupported((string?)Session.State?["releaseKey"] ?? "",
        new OnlineRequest { Operation = "DownloadPlc", DownloadScope = scope }) == null;

    internal async Task<IReadOnlyList<string>> DownloadAdaptersAsync(string plc)
    {
        var routes = await Read("ListTransferRoutes", new() { ["softwarePath"] = plc });
        return (routes["modes"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(mode => (mode["pcInterfaces"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(item => (string?)item["name"]).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, object?> DownloadArguments(ProjectDownloadPlan plan, bool preview)
    {
        var arguments = new Dictionary<string, object?> { ["softwarePath"] = plan.Plc, ["targetIpAddress"] = plan.Address,
            ["pgPcInterface"] = plan.Adapter, ["downloadScope"] = plan.Scope, ["confirmPhysicalTarget"] = true,
            ["expectedProjectFile"] = plan.Project, ["dryRun"] = preview };
        if (!preview) arguments["expectedPlanHash"] = plan.Hash;
        return arguments;
    }

    internal async Task<ProjectDownloadPlan> PreviewDownloadAsync(string plc, string address, string adapter, string scope)
    {
        if (!CanPreviewDownload || !SupportsScope(scope)) throw new InvalidOperationException(Loc.Current["Project.DownloadUnavailable"]);
        var target = new ProjectDownloadPlan(Session.SessionKey!, BindingEpoch, BoundProject!, plc, address, adapter, scope, "");
        var readiness = await Read("CheckDownloadReadiness", new() { ["softwarePath"] = plc });
        if ((bool?)readiness["ready"] != true)
            throw new InvalidOperationException(string.Join(" ", new[] { Loc.Current["Project.NotReady"] }
                .Concat((readiness["issues"] as JsonArray ?? []).Select(issue => issue?.ToString()))));
        VerifyDownloadBinding(target);
        var preview = await Read("DownloadPlc", DownloadArguments(target, true));
        var identity = preview["targetIdentity"];
        string? project = (string?)(identity?["identity"]?["projectPath"] ?? identity?["projectFile"]
            ?? identity?["projectPath"] ?? identity?["project"] ?? preview["projectFile"]);
        string? hash = (string?)preview["planHash"];
        if (string.IsNullOrWhiteSpace(hash) || !string.Equals(project, target.Project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.Current["Project.StaleDownload"]);
        VerifyDownloadBinding(target);
        return target with { Hash = hash };
    }

    internal async Task SubmitDownloadAsync(ProjectDownloadPlan plan)
    {
        VerifyDownloadBinding(plan);
        await Read("DownloadPlc", DownloadArguments(plan, false));
    }

    private void VerifyDownloadBinding(ProjectDownloadPlan plan)
    {
        if (Session.SessionKey != plan.SessionKey || BindingEpoch != plan.Epoch
            || !string.Equals(plan.Project, BoundProject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Current["Project.StaleDownload"]);
    }
}
