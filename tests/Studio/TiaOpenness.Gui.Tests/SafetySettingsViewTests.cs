using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class SafetySettingsViewTests(WpfContext wpf)
{
    [Fact]
    public void Safety_labels_and_lists_use_the_project_Primer_styles()
    {
        wpf.Run(() =>
        {
            var view = new SettingsView();
            foreach (string name in new[] { "PlanLifetimeLabel", "TargetAddressLabel", "SecretNameLabel", "SecretValueLabel" })
                Assert.Same(view.FindResource("Primer.FormLabel"), ((Label)view.FindName(name)).Style);
            foreach (string name in new[] { "OnlineTargets", "SecretNames" })
                Assert.Same(view.FindResource("Primer.List"), ((ListBox)view.FindName(name)).Style);
            view.Dispose();
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Problem_files_lock_the_editor_and_preserve_entries_until_replacement_is_confirmed(bool unreadable)
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            File.WriteAllText(fixture.TargetsPath, "invalid original entries");
            using var held = unreadable ? new FileStream(fixture.TargetsPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            WpfContext.Complete(view.LoadSafetyAsync());
            Assert.False(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            Assert.True(((Button)view.FindName("ReloadSafety")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((Button)view.FindName("ReplaceTargetFile")).Visibility);
            Assert.Contains(fixture.TargetsPath, ((TextBlock)view.FindName("SafetyStatus")).Text, StringComparison.Ordinal);
            var entries = (ListBox)view.FindName("OnlineTargets");
            Assert.Equal("192.0.2.10", Assert.Single(entries.Items));
            view.SafetyConfirmationForTests = key => { Assert.Equal("Settings.ConfirmReplaceTargets", key); return false; };
            Assert.False(view.ConfirmTargetFileReplacement());
            Assert.Single(entries.Items);
            view.SafetyConfirmationForTests = _ => true;
            Assert.True(view.ConfirmTargetFileReplacement());
            Assert.Empty(entries.Items);
            Assert.True(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            view.Dispose();
        });
    }

    [Fact]
    public void Concurrent_changes_prompt_reload_and_leave_the_file_untouched()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            File.WriteAllText(fixture.TargetsPath, "target=198.51.100.10\n");
            string? error = null;
            view.SafetyErrorForTests = key => error = key;
            WpfContext.Complete(view.SaveTargetsAsync());
            Assert.Equal("Settings.TargetsChanged", error);
            Assert.Equal("target=198.51.100.10\n", File.ReadAllText(fixture.TargetsPath));
            Assert.False(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            Assert.True(((Button)view.FindName("ReloadSafety")).IsEnabled);
            WpfContext.Complete(view.LoadSafetyAsync());
            Assert.True(((StackPanel)view.FindName("TargetEditor")).IsEnabled);
            view.Dispose();
        });
    }

    [Fact]
    public void Empty_values_and_cancelled_secret_replacements_or_removals_do_not_write()
    {
        wpf.Run(() =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SetSecret("plc1", "synthetic-original");
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            var name = (TextBox)view.FindName("SecretName");
            var value = (PasswordBox)view.FindName("SecretValue");
            var names = (ListBox)view.FindName("SecretNames");
            string? error = null;
            int confirmations = 0;
            view.SafetyErrorForTests = key => error = key;
            view.SafetyConfirmationForTests = _ => { confirmations++; return false; };
            name.Text = "plc1";
            WpfContext.Complete(view.SetSecretAsync());
            Assert.Equal("Settings.SecretEmpty", error);
            Assert.Equal(0, confirmations);
            value.Password = "synthetic-replacement";
            WpfContext.Complete(view.SetSecretAsync());
            Assert.Empty(value.Password);
            Assert.Equal(1, confirmations);
            names.SelectedIndex = 0;
            WpfContext.Complete(view.RemoveSecretAsync());
            Assert.Equal(2, confirmations);
            Assert.Equal("plc1", Assert.Single(fixture.Service.SecretNames()));
            Assert.Single(fixture.Audit.Read());
            view.Dispose();
        });
    }
    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Editors_load_names_only_and_clear_password_when_selection_changes(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            fixture.Service.SetSecret("plc1", "synthetic-private-value");
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            var targets = (ListBox)view.FindName("OnlineTargets");
            var secrets = (ListBox)view.FindName("SecretNames");
            var value = (PasswordBox)view.FindName("SecretValue");
            Assert.Equal("192.0.2.10", Assert.Single(targets.Items));
            Assert.Equal("plc1", Assert.Single(secrets.Items));
            Assert.Empty(value.Password);
            value.Password = "synthetic-unsaved-value";
            secrets.SelectedIndex = 0;
            Assert.Empty(value.Password);
            Assert.Equal("plc1", ((TextBox)view.FindName("SecretName")).Text);
            foreach (string name in new[] { "OnlineTargets", "OnlineTargetInput", "SecretNames", "SecretName", "SecretValue", "OnlinePlanLifetime" })
            {
                var control = (Control)view.FindName(name);
                Assert.True(control.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), name);
                Assert.DoesNotContain("synthetic-private-value", AutomationProperties.GetName(control));
            }
            view.Dispose();
        });
    }
}
