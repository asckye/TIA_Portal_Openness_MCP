using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public class ReleasePickerTests(WpfContext wpf)
{
    [Fact]
    public void Every_release_is_selectable_and_survives_language_changes()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var window = new MainWindow(new TiaOpenness.Gui.ViewModels.MainViewModel(), false);
            try
            {
                var model = (MainViewModel)window.DataContext;
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Child = content, DataContext = model };
                host.Measure(new Size(1200, 780));
                host.Arrange(new Rect(0, 0, 1200, 780));
                host.UpdateLayout();
                var picker = (ComboBox)ProjectPageTestSupport.Find(window, "ReleasePicker");
                Assert.Equal(8, picker.Items.Count);
                foreach (var release in model.Releases)
                {
                    picker.SelectedValue = release.Key;
                    Assert.Equal(release.Key, model.SelectedReleaseKey);
                }
                picker.SelectedValue = "15.1";
                window.ShowConfiguration(false);
                var configuration = window.Configuration!;
                var mcpPicker = (ComboBox)configuration.FindName("Version");
                Assert.True(mcpPicker.IsEnabled);
                Assert.True(mcpPicker.IsHitTestVisible);
                Assert.Equal("15.1", configuration.SelectedReleaseKey);
                mcpPicker.SelectedValue = "20";
                Assert.Equal("20", model.SelectedReleaseKey);
                Assert.Equal("20", picker.SelectedValue);
                picker.SelectedValue = "15.1";
                Assert.Equal("15.1", configuration.SelectedReleaseKey);
                Loc.Current.Language = AppLanguage.Chinese;
                Assert.Equal("15.1", model.SelectedReleaseKey);
                Assert.Equal("15.1", configuration.SelectedReleaseKey);
                Assert.True(model.CanSelectRelease);
            }
            finally { window.Close(); }
        });
    }
}
