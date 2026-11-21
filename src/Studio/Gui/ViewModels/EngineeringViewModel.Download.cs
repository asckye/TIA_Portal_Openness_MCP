using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

public sealed partial class EngineeringViewModel
{
    private McpProjectClient? DownloadHost => _client as McpProjectClient;
    public bool DownloadAvailable => SelectedDevice?.Category == "Plc" && DownloadHost?.CanPreviewDownload == true;
    public bool ChangesSupported => DownloadAvailable && DownloadHost!.SupportsScope("softwareOnlyChanges");
    private bool _allSoftware;
    public bool AllSoftware
    {
        get => _allSoftware || !ChangesSupported;
        set { if (Set(ref _allSoftware, value)) { Raise(nameof(OnlyChanges)); ClearDownloadPlan(); } }
    }
    public bool OnlyChanges => !AllSoftware;
    private string _downloadAddress = "", _downloadAdapter = "";
    public string DownloadAddress { get => _downloadAddress; set { if (Set(ref _downloadAddress, value)) ClearDownloadPlan(); } }
    public string DownloadAdapter { get => _downloadAdapter; set { if (Set(ref _downloadAdapter, value ?? "")) ClearDownloadPlan(); } }
    public IReadOnlyList<string> DownloadAdapters { get; private set; } = [];
    private ProjectDownloadPlan? _downloadPlan;
    private int _downloadGeneration;
    public bool CanPreviewDownload => DownloadAvailable && !_activity.Busy && DownloadAdapters.Contains(DownloadAdapter, StringComparer.Ordinal)
        && IPAddress.TryParse(DownloadAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == DownloadAddress;
    public bool HasDownloadPlan => _downloadPlan != null;
    public string DownloadReview => _downloadPlan is { } p ? string.Join("\n", p.Project, p.Plc + " · " + p.Address + " · " + p.Adapter, p.Scope, p.Hash) : "";
    internal void ClearDownloadPlan()
    {
        _downloadGeneration++;
        _downloadPlan = null;
        Raise(nameof(DownloadReview)); Raise(nameof(HasDownloadPlan)); Raise(nameof(CanPreviewDownload));
    }
    private void ResetDownloadTarget()
    {
        ClearDownloadPlan();
        DownloadAdapter = DownloadAddress = "";
        DownloadAdapters = [];
        Raise(nameof(DownloadAdapters)); Raise(nameof(ChangesSupported)); Raise(nameof(DownloadAvailable));
        Raise(nameof(AllSoftware)); Raise(nameof(OnlyChanges));
    }
    internal async Task ReadDownloadAdaptersAsync()
    {
        if (_activity.Busy || !DownloadAvailable) return;
        var device = SelectedDevice!;
        await _activity.Guarded("Project.Adapter", async () =>
        {
            var routes = await DownloadHost!.DownloadAdaptersAsync(device.Id);
            if (SelectedDevice != device) return;
            ClearDownloadPlan();
            DownloadAdapters = routes; Raise(nameof(DownloadAdapters));
            if (!routes.Contains(DownloadAdapter, StringComparer.Ordinal)) DownloadAdapter = "";
            Raise(nameof(CanPreviewDownload));
        });
    }
    internal async Task PreviewDownloadAsync()
    {
        if (!CanPreviewDownload) return;
        ClearDownloadPlan();
        var device = SelectedDevice!;
        var host = DownloadHost!;
        var binding = host.DownloadBinding;
        int generation = _downloadGeneration;
        bool Current() => generation == _downloadGeneration && host.DownloadBinding == binding;
        if (_dialogs.ShowMessage(Loc.Current.T("Project.PreflightConsent", binding.Project, device.Id),
            Loc.Current["Dialog.Save.Caption"], DialogButtons.OKCancel, DialogIcon.Warning, new(ConfirmationAction.SaveProject)) != DialogResult.OK) return;
        await _activity.Guarded("Project.Preflight", async () =>
        {
            if (!Current()) return;
            var compiled = await _client.CompileAsync(device.Id);
            if (!Current()) return;
            PresentCompileResult(compiled);
            if (compiled.ErrorCount != 0 || compiled.State is not ("Success" or "Warning")) return;
            await _client.SaveProjectAsync();
            if (!Current()) return;
            var plan = await host.PreviewDownloadAsync(device.Id, DownloadAddress, DownloadAdapter, AllSoftware ? "software" : "softwareOnlyChanges");
            if (!Current()) return;
            _downloadPlan = plan;
            Raise(nameof(DownloadReview)); Raise(nameof(HasDownloadPlan));
        });
    }
    internal async Task SubmitDownloadAsync(Action close)
    {
        if (!CanPreviewDownload || _downloadPlan is not { } plan || DownloadHost == null) return;
        if (_dialogs.ShowMessage(Loc.Current["Project.DownloadReviewWarning"] + "\n\n" + DownloadReview,
            Loc.Current["Project.RequestDownload"], DialogButtons.OKCancel, DialogIcon.Warning) != DialogResult.OK
            || _downloadPlan != plan || !CanPreviewDownload) return;
        ClearDownloadPlan();
        close();
        await _activity.Guarded("Project.RequestDownload", async () =>
        {
            await DownloadHost.SubmitDownloadAsync(plan);
            _activity.SetStatus("Project.DownloadCompleted", ToastKind.Success);
        });
    }
}
