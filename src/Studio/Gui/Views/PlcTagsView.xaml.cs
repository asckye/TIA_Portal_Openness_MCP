using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public sealed class PlcTagTableLabelConverter : IValueConverter
{
    internal static string Format(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string[] segments = path.Split('/');
        string table = ObjectPathEncoding.DecodeSegment(segments[^1]);
        return segments.Length >= 4 && segments[0] == "units" && segments[2] == "tag-tables"
            ? ObjectPathEncoding.DecodeSegment(segments[1]) + " / " + table : table;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class PlcTagsView : UserControl
{
    public PlcTagsView()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is MainViewModel old) old.Engineering.PropertyChanged -= OnRowsChanged;
            if (change.NewValue is MainViewModel model) model.Engineering.PropertyChanged += OnRowsChanged;
        };
    }
    private void OnRowsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EngineeringViewModel.PlcTags)) ApplyFilter();
    }
    private async void OnRead(object sender, RoutedEventArgs e)
    {
        var engineering = ((MainViewModel)DataContext).Engineering;
        engineering.Document = ProjectDocument.Tags;
        await engineering.ReadTagsAsync();
    }
    private void OnFilter(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        string query = TagSearch.Text.Trim();
        bool Matches(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        if (TagRows != null) TagRows.Items.Filter = query.Length == 0 ? null : item => item is TagInfo tag
            && (Matches(tag.Name) || Matches(tag.LogicalAddress) || Matches(tag.Comment) || Matches(tag.TableName)
                || Matches(PlcTagTableLabelConverter.Format(tag.TableName)));
    }
    private void OnCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "plc-tags.csv", Filter = "CSV (*.csv)|*.csv", Title = Loc.Current["Project.ExportCsv"] };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SaveCsv(dialog.FileName);
    }

    internal void SaveCsv(string path)
    {
        try
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                WriteCsv(writer, TagRows.Items.Cast<TagInfo>());
            ((MainViewModel)DataContext).Activity.Notify(ToastKind.Success, LocalizedText.Key("Toast.Exported", path), "tag-export");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ((MainViewModel)DataContext).Activity.Notify(ToastKind.Error, LocalizedText.Literal(error.Message), "tag-export");
            ((MainViewModel)DataContext).Activity.AppendLocalized("Log.Error", error.Message);
        }
    }

    internal static void WriteCsv(TextWriter writer, IEnumerable<TagInfo> rows)
    {
        writer.WriteLine("Name,DataType,LogicalAddress,Table,Comment");
        foreach (var tag in rows)
            writer.WriteLine(string.Join(",", new[] { tag.Name, tag.DataType, tag.LogicalAddress, tag.TableName, tag.Comment }.Select(CsvCell)));
    }

    private static string CsvCell(string? value)
    {
        string text = value ?? "";
        if (text.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
