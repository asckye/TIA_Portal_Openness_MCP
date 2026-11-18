using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.Views;

public partial class SettingsView
{
    private SafetySettingsService safety = new();
    private readonly ObservableCollection<string> targets = [];
    private bool safetyBusy;
    internal Func<string, bool>? SafetyConfirmationForTests { get; set; }
    internal Action<string>? SafetyErrorForTests { get; set; }

    private void InitializeSafetyEditors()
    {
        OnlineTargets.ItemsSource = targets;
        Unloaded += (_, _) => SecretValue.Clear();
    }

    internal void SetSafetyService(SafetySettingsService service) => safety = service;

    internal async Task LoadSafetyAsync()
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
                SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(list == null ? "Settings.TargetsUnconfigured"
                    : list.Entries.Count == 0 ? "Settings.TargetsEmpty" : "Settings.TargetsConfigured"));
            }
            else
            {
                SafetyStatus.Text = Loc.Current.T(safety.TargetsProblem == "invalid" ? "Settings.TargetFileInvalid" : "Settings.TargetFileUnreadable",
                    safety.TargetsPath);
            }
            UpdateTargetEditor();
            SecretNames.ItemsSource = await Task.Run(safety.SecretNames);
        }
        catch (Exception) /* swallow(privacy): editor errors never expose vault contents or credential values. */
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
        targets.Clear();
        UpdateTargetEditor();
        SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Settings.TargetsReplacementReady"));
        return true;
    }

    private void OnAddTarget(object sender, RoutedEventArgs e)
    {
        if (safety.TargetsReadOnly) return;
        try
        {
            string entry = SafetySettingsService.ValidateTarget(OnlineTargetInput.Text.Trim());
            if (!targets.Contains(entry)) targets.Add(entry);
            OnlineTargetInput.Clear();
            OnlineTargetInput.Focus();
            SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Settings.TargetsUnsaved"));
        }
        catch (ArgumentException) /* swallow(privacy): show the localized input rule instead of echoing the rejected text. */
        { ShowSafetyError("Settings.TargetInvalid"); }
    }

    private void OnRemoveTarget(object sender, RoutedEventArgs e)
    {
        if (safety.TargetsReadOnly) return;
        if (OnlineTargets.SelectedItem is not string entry) return;
        targets.Remove(entry);
        SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Settings.TargetsUnsaved"));
    }

    private async void OnSaveTargets(object sender, RoutedEventArgs e) => await SaveTargetsAsync();

    internal async Task SaveTargetsAsync()
    {
        if (safety.TargetsReadOnly) return;
        string[] entries = targets.ToArray();
        SetSafetyBusy(true);
        try
        {
            await Task.Run(() => safety.SaveTargets(entries));
            SafetyStatus.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(entries.Length == 0
                ? "Settings.TargetsEmpty" : "Settings.TargetsConfigured"));
        }
        catch (TiaOpenness.Shared.OnlineTargetSettingsChangedException) /* swallow(privacy): report a fixed reload prompt without file contents. */
        {
            TargetEditor.IsEnabled = false;
            ShowSafetyError("Settings.TargetsChanged");
        }
        catch (Exception) /* swallow(privacy): failed writes or audit appends are reported without exposing settings contents. */
        { ShowSafetyError("Settings.SafetySaveFailed"); }
        finally { SetSafetyBusy(false); }
    }

    private async void OnReloadSafety(object sender, RoutedEventArgs e) => await LoadSafetyAsync();

    private void OnSecretSelected(object sender, SelectionChangedEventArgs e)
    {
        SecretName.Text = SecretNames.SelectedItem as string ?? "";
        SecretValue.Clear();
        RemoveSecret.IsEnabled = SecretNames.SelectedItem != null;
    }

    private async void OnSetSecret(object sender, RoutedEventArgs e) => await SetSecretAsync();

    internal async Task SetSecretAsync()
    {
        string name = SecretName.Text;
        string value = SecretValue.Password;
        SecretValue.Clear();
        if (value.Length == 0) { ShowSafetyError("Settings.SecretEmpty"); return; }
        SetSafetyBusy(true);
        try
        {
            bool replacing = await Task.Run(() => safety.HasSecret(name));
            if (replacing && !ConfirmSafety("Settings.ConfirmReplaceSecret", name)) return;
            await Task.Run(() => safety.SetSecret(name, value, replaceConfirmed: replacing));
            SecretNames.ItemsSource = await Task.Run(safety.SecretNames);
            SecretName.Clear();
        }
        catch (ArgumentException) /* swallow(privacy): report the naming rule without echoing the name or secret. */
        { ShowSafetyError("Settings.SecretNameInvalid"); }
        catch (Exception) /* swallow(privacy): encryption, file and audit errors must not echo any supplied secret. */
        { ShowSafetyError("Settings.SafetySaveFailed"); }
        finally { SecretValue.Clear(); SetSafetyBusy(false); }
    }

    private async void OnRemoveSecret(object sender, RoutedEventArgs e) => await RemoveSecretAsync();

    internal async Task RemoveSecretAsync()
    {
        if (SecretNames.SelectedItem is not string name) return;
        SecretValue.Clear();
        if (!ConfirmSafety("Settings.ConfirmRemoveSecret", name)) return;
        SetSafetyBusy(true);
        try
        {
            await Task.Run(() => safety.RemoveSecret(name));
            SecretNames.ItemsSource = await Task.Run(safety.SecretNames);
            SecretName.Clear();
        }
        catch (Exception) /* swallow(privacy): only a fixed message is displayed if removal or its audit append fails. */
        { ShowSafetyError("Settings.SafetySaveFailed"); }
        finally { SetSafetyBusy(false); }
    }

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

    private void ShowSafetyError(string key)
    {
        if (SafetyErrorForTests != null) { SafetyErrorForTests(key); return; }
        Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), Loc.Current[key], Loc.Current["Dialog.Error.Caption"],
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
