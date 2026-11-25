using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaOpenness.Gui.Tests.Tooling;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class AppIconTests(WpfContext wpf)
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [Fact]
    public void Shipped_images_match_the_vectors() =>
        wpf.Run(() => AppIconGenerator.Generate(McpOperatorFixture.RepositoryRoot, check: true));

    [Fact]
    public void Icon_has_all_shell_sizes_with_transparent_corners_and_an_opaque_centre()
    {
        wpf.Run(() =>
        {
            using var file = File.OpenRead(Path.Combine(McpOperatorFixture.RepositoryRoot, "build-tools/assets/AppIcon.ico"));
            var frames = new IconBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
            Assert.Equal(Sizes, frames.Select(f => f.PixelWidth));
            foreach (var frame in frames)
            {
                int size = frame.PixelWidth;
                Assert.Equal(size, frame.PixelHeight);
                Assert.Equal(32, frame.Format.BitsPerPixel);
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4];
                byte Alpha(int x, int y)
                {
                    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    return pixel[3];
                }
                foreach (int x in new[] { 0, size - 1 })
                    foreach (int y in new[] { 0, size - 1 }) Assert.Equal(0, Alpha(x, y));
                Assert.Equal(255, Alpha(size / 2, size / 2));
            }
            using var directory = new BinaryReader(file);
            file.Position = 6 + 16 * (Sizes.Length - 1) + 12;
            file.Position = directory.ReadUInt32();
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, directory.ReadBytes(8));
        });
    }

    [Theory]
    [InlineData("TiaOpenness.exe")]
    [InlineData("TiaMcp.Updater.exe")]
    [InlineData("TiaMcp.PluginLauncher.exe")]
    public void Built_executable_carries_the_application_icon_group(string exe)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "app-icons", exe));
        using var pe = new PEReader(file);
        var resources = pe.GetSectionData(pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress);
        var groupType = Entries(resources, 0).Single(e => e.Id == 14); // RT_GROUP_ICON
        var name = Assert.Single(Entries(resources, groupType.Offset));
        var language = Assert.Single(Entries(resources, name.Offset));
        var data = resources.GetReader();
        data.Offset = language.Offset;
        var group = pe.GetSectionData(data.ReadInt32()).GetReader();
        Assert.Equal(0, group.ReadUInt16());
        Assert.Equal(1, group.ReadUInt16());
        Assert.Equal(Sizes.Length, group.ReadUInt16());
        foreach (int size in Sizes)
        {
            Assert.Equal((byte)(size % 256), group.ReadByte());
            Assert.Equal((byte)(size % 256), group.ReadByte());
            group.Offset += 4;
            Assert.Equal(32, group.ReadUInt16());
            group.Offset += 6;
        }
    }

    private static List<(int Id, int Offset)> Entries(PEMemoryBlock resources, int directory)
    {
        var reader = resources.GetReader();
        reader.Offset = directory + 12;
        int count = reader.ReadUInt16() + reader.ReadUInt16();
        var entries = new List<(int, int)>(count);
        for (int i = 0; i < count; i++) entries.Add((reader.ReadInt32(), reader.ReadInt32() & int.MaxValue));
        return entries;
    }

    [Fact]
    public void Native_tray_icon_draws_its_16_pixel_frame()
    {
        string path = Path.Combine(McpOperatorFixture.RepositoryRoot, "build-tools/assets/AppIcon.ico");
        using var icon = new System.Drawing.Icon(path, 16, 16);
        Assert.Equal(new System.Drawing.Size(16, 16), icon.Size);
        using var bitmap = new System.Drawing.Bitmap(16, 16);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.DrawIconUnstretched(icon, new System.Drawing.Rectangle(0, 0, 16, 16));
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(System.Drawing.Color.White.ToArgb(), bitmap.GetPixel(8, 5).ToArgb());
        Assert.Equal(System.Drawing.Color.FromArgb(0x20, 0x4D, 0xB4).ToArgb(), bitmap.GetPixel(1, 8).ToArgb());
        using var decoded = icon.ToBitmap();
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var pixel = decoded.GetPixel(x, y);
                if (pixel.A == 255) Assert.Equal(pixel.ToArgb(), bitmap.GetPixel(x, y).ToArgb());
            }
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(20, false)]
    [InlineData(24, true)]
    [InlineData(32, true)]
    public void Small_frames_use_the_size_specific_top_plate(int size, bool hasDot)
    {
        wpf.Run(() =>
        {
            using var file = File.OpenRead(Path.Combine(McpOperatorFixture.RepositoryRoot, "build-tools/assets/AppIcon.ico"));
            var frame = new IconBitmapDecoder(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single(f => f.PixelWidth == size);
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(size / 2, size / 3, 1, 1), pixel, 4, 0);
            Assert.Equal(hasDot ? new byte[] { 0xBC, 0x53, 0x1D, 255 } : new byte[] { 255, 255, 255, 255 }, pixel);
        });
    }

    [Fact]
    public void Approval_notification_uses_the_application_icon_with_a_warning_balloon()
    {
        wpf.Run(() =>
        {
            using var notification = new FeaturePageFixtures.Notification();
            notification.Show("Review", "Approval requested", () => { });
            var balloon = Assert.IsType<System.Windows.Forms.NotifyIcon>(notification.Balloon);
            Assert.Equal(System.Windows.Forms.ToolTipIcon.Warning, balloon.BalloonTipIcon);
            using var icon = new MemoryStream();
            Assert.IsType<System.Drawing.Icon>(balloon.Icon).Save(icon);
            Assert.Equal(File.ReadAllBytes(Path.Combine(McpOperatorFixture.RepositoryRoot, "build-tools/assets/AppIcon.ico")), icon.ToArray());
        });
    }

    [Fact]
    public void Workbench_dialogs_inherit_the_shared_window_icon()
    {
        wpf.Run(() =>
        {
            using var picker = new ViewModels.PortalProcessPickerViewModel([]);
            Window[] dialogs = [new Controls.PortalProcessPicker(picker), new Views.FeatureConfirmation("Review", "Approval requested", "OK"),
                new TiaMcpConfigurator.ClientsWriteConfirmation(1, "Client", "config.json", "", false),
                new Controls.WorkbenchMessageBox("Approval requested", "Review", MessageBoxButton.OK, MessageBoxImage.Warning)];
            try
            {
                foreach (var dialog in dialogs)
                {
                    Assert.Same(Application.Current.FindResource("Ui.Window"), dialog.Style);
                    Assert.IsAssignableFrom<BitmapFrame>(dialog.Icon);
                }
            }
            finally { foreach (var dialog in dialogs) dialog.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Dark, true)]
    public void Window_and_title_bar_load_the_icon(AppTheme mode, bool highContrast)
    {
        wpf.Run(() =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.HighContrastOverride);
            try
            {
                themes.Theme = mode;
                themes.HighContrastOverride = highContrast;
                var window = new MainWindow(ProjectPageTestSupport.Model(), false);
                try
                {
                    var host = WorkbenchRenderTests.CreateHost(window);
                    Assert.IsAssignableFrom<BitmapFrame>(window.Icon);
                    Assert.Equal(20, window.BrandIcon.ActualWidth);
                    Assert.Equal(20, window.BrandIcon.ActualHeight);
                    var image = Assert.IsType<DrawingImage>(window.BrandIcon.Source);
                    Assert.Same(window.FindResource(highContrast ? "AppIcon.HighContrast" : "AppIcon.Small"), image);
                    var about = window.SettingsContent.AboutIcon;
                    Assert.Same(window.FindResource("AppIcon.Large"), Assert.IsType<DrawingImage>(about.Source));
                    Assert.Equal(new Size(40, 40), new Size(about.Width, about.Height));
                    if (highContrast)
                    {
                        var group = Assert.IsType<DrawingGroup>(image.Drawing);
                        var inks = group.Children.Cast<GeometryDrawing>().Select(p => Assert.IsType<SolidColorBrush>(p.Brush).Color).ToHashSet();
                        Color PaletteColor(string key) => Assert.IsType<SolidColorBrush>(window.FindResource(key)).Color;
                        Assert.True(inks.SetEquals([PaletteColor("Primer.brandGradient"), PaletteColor("Primer.onPrimary")]));
                        Assert.Same(window.FindResource("Primer.hcOutline"), window.BrandOutline.BorderBrush);
                        Assert.Equal(new Thickness(1), window.BrandOutline.BorderThickness);
                    }
                    if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { } folder)
                    {
                        Directory.CreateDirectory(folder);
                        string theme = highContrast ? "HighContrast" : mode.ToString();
                        WorkbenchRenderTests.SaveRender(host, Path.Combine(folder, $"title-bar-{theme}.png"), (int)host.Width, 39);
                    }
                }
                finally { window.Close(); }
            }
            finally { (themes.Theme, themes.HighContrastOverride) = previous; }
        });
    }
}
