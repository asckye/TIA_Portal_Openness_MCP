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
            foreach (string name in new[] { "PlanLifetimeLabel", "TargetAddressLabel" })
                Assert.Same(view.FindResource("Primer.FormLabel"), ((Label)view.FindName(name)).Style);
            Assert.Same(view.FindResource("Primer.List"), ((ListBox)view.FindName("OnlineTargets")).Style);
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

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Editors_load_targets_and_keep_accessible_names(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var fixture = new SafetySettingsServiceTests.Fixture();
            fixture.Service.SaveTargets(new[] { "192.0.2.10" });
            var view = new SettingsView();
            view.SetSafetyService(fixture.Service);
            WpfContext.Complete(view.LoadSafetyAsync());
            var targets = (ListBox)view.FindName("OnlineTargets");
            Assert.Equal("192.0.2.10", Assert.Single(targets.Items));
            foreach (string name in new[] { "OnlineTargets", "OnlineTargetInput", "OnlinePlanLifetime" })
            {
                var control = (Control)view.FindName(name);
                Assert.True(control.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), name);
            }
            view.Dispose();
        });
    }
}
