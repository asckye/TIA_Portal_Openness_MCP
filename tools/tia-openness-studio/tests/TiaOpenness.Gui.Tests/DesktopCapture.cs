using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

internal static class DesktopCapture
{
    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    internal static RenderTargetBitmap Render(FrameworkElement root, params FrameworkElement[] popups)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(root.RenderSize);
            context.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
            // Popups have their own HWND: render their real child at its screen-relative position.
            foreach (var popup in popups)
            {
                var source = new Rect(-8, -8, popup.ActualWidth + 16, popup.ActualHeight + 16);
                var origin = root.PointFromScreen(popup.PointToScreen(source.TopLeft));
                context.DrawRectangle(new VisualBrush(popup) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = source }, null, new Rect(origin, source.Size));
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    internal static void Save(FrameworkElement root, string name, params FrameworkElement[] popups)
    {
        string? output = Environment.GetEnvironmentVariable("TIA_GLASS_SCREENSHOTS");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(root, popups)));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }

    internal static void AssertCards(FrameworkElement root, AppTheme theme, params Border[] cards)
    {
        var bitmap = Render(root);
        foreach (var card in cards)
        {
            Assert.True(card.ActualWidth > 40 && card.ActualHeight > 40, card.Name + " did not render");
            var origin = card.TransformToAncestor(root).Transform(new Point(12, 12));
            var rect = new Int32Rect((int)origin.X, (int)origin.Y, (int)card.ActualWidth - 24, (int)card.ActualHeight - 24);
            Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= bitmap.PixelWidth && rect.Y + rect.Height <= bitmap.PixelHeight,
                card.Name + " is clipped");
            var pixels = new byte[rect.Width * rect.Height * 4];
            bitmap.CopyPixels(rect, pixels, rect.Width * 4, 0);
            double sum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                sum += ThemeSurfaceTests.Luminance(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));
            double mean = sum / (rect.Width * rect.Height);
            Assert.True(theme == AppTheme.Light ? mean > 0.55 : mean < 0.15, $"{theme} {card.Name}: mean luminance {mean:F3}");
        }
    }
}
