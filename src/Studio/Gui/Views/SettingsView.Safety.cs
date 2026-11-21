using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class SettingsView
{
    private SafetySettingsService safety = new();
    private readonly ObservableCollection<string> targets = [];
    private bool safetyBusy;
    private Task? loadingTargets;
    internal Func<string, bool>? SafetyConfirmationForTests { get; set; }
    internal Action<string>? SafetyErrorForTests { get; set; }

    private void InitializeSafetyEditors()
    {
        OnlineTargets.ItemsSource = targets;
    }

    internal void SetSafetyService(SafetySettingsService service) => safety = service;

    internal Task LoadSafetyAsync() => loadingTargets is { IsCompleted: false } ? loadingTargets : loadingTargets = LoadSafetyCoreAsync();

    private async Task LoadSafetyCoreAsync()
    {
        if (safetyBusy) return;
        SetSafetyBusy(true);
        try
        {
            var list = await Task.Run(safety.LoadTargets);
            if (safety.TargetsProblem == null)
            {
                targets.Clear();
                if (list != null) foreach (string entry in list.Entries) targets.Add(entry);
                SaveTargetChanges.Visibility = list == null ? Visibility.Visible : Visibility.Collapsed;
                SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(list == null ? "Settings.TargetsUnconfigured"
                    : list.Entries.Count == 0 ? "Settings.TargetsEmpty" : "Settings.TargetsConfigured"));
            }
            else
            {
                SafetyStatus.Text = Loc.Current.T(safety.TargetsProblem == "invalid" ? "Settings.TargetFileInvalid" : "Settings.TargetFileUnreadable",
                    safety.TargetsPath);
            }
            UpdateTargetEditor();
        }
        catch (Exception) /* swallow(privacy): editor errors never expose settings file contents. */
        { ShowSafetyError("Settings.SafetyLoadFailed"); }
        finally { SetSafetyBusy(false); }
    }

    private void SetSafetyBusy(bool value)
    {
        safetyBusy = value;
        SafetyEditors.IsEnabled = !value;
    }

    private void UpdateTargetEditor()
    {
        TargetEditor.IsEnabled = !safety.TargetsReadOnly;
        ReplaceTargetFile.Visibility = safety.TargetsReadOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReplaceTargetFile(object sender, RoutedEventArgs e) => ConfirmTargetFileReplacement();

    internal bool ConfirmTargetFileReplacement()
    {
        if (!safety.TargetsReadOnly || !ConfirmSafety("Settings.ConfirmReplaceTargets", safety.TargetsPath)) return false;
        safety.ConfirmTargetReplacement();
        SaveTargetChanges.Visibility = Visibility.Visible;
        targets.Clear();
        UpdateTargetEditor();
        SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Settings.TargetsReplacementReady"));
        return true;
    }

    private async void OnAddTarget(object sender, RoutedEventArgs e)
    {
        if (safety.TargetsReadOnly) return;
        try
        {
            string entry = SafetySettingsService.ValidateTarget(OnlineTargetInput.Text.Trim());
            bool add = !targets.Contains(entry);
            if (!ConfirmTargets(add ? targets.Append(entry) : targets)) return;
            if (add) targets.Add(entry);
            SaveTargetChanges.Visibility = Visibility.Visible;
            if (await SaveTargetsAsync())
            {
                OnlineTargetInput.Clear();
                OnlineTargetInput.Focus();
            }
        }
        catch (ArgumentException) /* swallow(privacy): show the localized input rule instead of echoing the rejected text. */
        { ShowSafetyError("Settings.TargetInvalid"); }
    }

    private void OnRemoveTarget(object sender, RoutedEventArgs e)
    {
        if (safety.TargetsReadOnly) return;
        if (((Button)sender).DataContext is not string entry) return;
        targets.Remove(entry);
        SaveTargetChanges.Visibility = Visibility.Visible;
        SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Settings.TargetsUnsaved"));
    }

    private bool ConfirmTargets(IEnumerable<string> proposed)
    {
        if (SafetyConfirmationForTests != null) return SafetyConfirmationForTests("Settings.ApplySafety");
        string entries = string.Join(", ", proposed);
        var dialog = new FeatureConfirmation(Loc.Current["Settings.ApplySafety"], Loc.Current["Settings.ApplySafetyHelp"], Loc.Current["Settings.ApplyAndSave"],
            new(Loc.Current["Settings.OnlineTargets"], entries.Length == 0 ? Loc.Current["Settings.TargetsEmpty"] : entries),
            new(Loc.Current["Settings.Timeout"], _approvals == null ? "—" : Loc.Current.T("Common.Seconds", _approvals.TimeoutSeconds)));
        return dialog.Confirm(this);
    }

    private async void OnSaveTargets(object sender, RoutedEventArgs e)
    {
        if (ConfirmTargets(targets)) await SaveTargetsAsync();
    }

    internal async Task<bool> SaveTargetsAsync()
    {
        if (safetyBusy || safety.TargetsReadOnly) return false;
        string[] entries = targets.ToArray();
        SetSafetyBusy(true);
        try
        {
            await Task.Run(() => safety.SaveTargets(entries));
            SaveTargetChanges.Visibility = Visibility.Collapsed;
            SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(entries.Length == 0
                ? "Settings.TargetsEmpty" : "Settings.TargetsConfigured"));
            NotifySettings(ToastKind.Success, LocalizedText.Key("Toast.Saved"));
            return true;
        }
        catch (TiaOpenness.Shared.OnlineTargetSettingsChangedException) /* swallow(privacy): report a fixed reload prompt without file contents. */
        {
            TargetEditor.IsEnabled = false;
            ShowSafetyError("Settings.TargetsChanged");
        }
        catch (Exception) /* swallow(privacy): failed writes or audit appends are reported without exposing settings contents. */
        { ShowSafetyError("Settings.SafetySaveFailed"); }
        finally { SetSafetyBusy(false); }
        return false;
    }

    private async void OnReloadSafety(object sender, RoutedEventArgs e) => await LoadSafetyAsync();

    private async void OnSavePlanLifetime(object sender, RoutedEventArgs e)
    {
        if (_approvals == null) return;
        if (!int.TryParse(OnlinePlanLifetime.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds < 1 || seconds > 86400)
        { ShowSafetyError("Settings.PlanLifetimeInvalid"); return; }
        await SaveApproval(() => _approvals.OnlinePlanLifetimeSeconds = seconds);
    }

    private bool ConfirmSafety(string key, params object[] arguments)
        => SafetyConfirmationForTests?.Invoke(key) ?? Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), Loc.Current.T(key, arguments),
            Loc.Current["Settings.HumanOnly"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private void NotifySettings(ToastKind kind, LocalizedText text) => (Window.GetWindow(this) as MainWindow)?.ShowToast(kind, text, "settings");

    private TextBlock? InputError(object sender) => sender == CustomTimeout ? CustomTimeoutError
        : sender == OnlinePlanLifetime ? OnlinePlanLifetimeError : OnlineTargetInputError;

    private void OnSafetyInputChanged(object sender, TextChangedEventArgs e)
    {
        if (InputError(sender) is not { } label) return;
        label.Text = "";
        label.Visibility = Visibility.Collapsed;
    }

    private void ShowSafetyError(string key)
    {
        TextBlock? label = key switch
        {
            "Settings.TimeoutInvalid" => CustomTimeoutError,
            "Settings.PlanLifetimeInvalid" => OnlinePlanLifetimeError,
            "Settings.TargetInvalid" => OnlineTargetInputError,
            _ => null
        };
        if (label != null)
        {
            label.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(key));
            label.Visibility = Visibility.Visible;
        }
        SafetyErrorForTests?.Invoke(key);
        NotifySettings(ToastKind.Error, LocalizedText.Key(key));
    }
}
