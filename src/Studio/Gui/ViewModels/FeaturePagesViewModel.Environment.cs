using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using TiaMcp.Versioning;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ViewModels;

public sealed partial class FeaturePagesViewModel
{
    public string LastEnvironmentCheck => Environment.LastChecked is { } when
        ? Loc.Current.T("Env.LastChecked", when.ToLocalTime().ToString("T", CultureInfo.GetCultureInfo(Loc.Current.IsChinese ? "zh-CN" : "en-US")))
        : Loc.Current["Env.NoCompletedCheck"];

    public IReadOnlyList<KeyValuePair<string, string>> EnvironmentSnapshot
    {
        get
        {
            var release = TiaVersionCatalog.Get(Release);
            var redactor = new DiagnosticRedactor();
            string Result(string id) => EnvironmentRows.FirstOrDefault(row => row.Check.Id == id)?.Result ?? Loc.Current["Env.Unchecked"];
            KeyValuePair<string, string> Row(string label, string value) => new(label, redactor.Redact(value).Text);
            return [
                Row(Loc.Current["Env.System"], RuntimeInformation.OSDescription),
                Row(Loc.Current["Env.Membership"], Result("membership")),
                Row(Loc.Current["Env.BundledRuntime"], Result("runtime")),
                Row(Loc.Current["Config.ReleaseTitle"], release.DisplayName + " · " + release.OwnerApartment),
                Row(Loc.Current["Env.DataDirectory"], DataLocations.Current.WorkbenchDataDirectory),
            ];
        }
    }

    public void CopyEnvironmentSummary()
    {
        var lines = new[] { EnvironmentSummary, LastEnvironmentCheck }
            .Concat(EnvironmentSnapshot.Select(row => row.Key + " · " + row.Value))
            .Concat(EnvironmentRows.Select(row => row.Name + " · " + row.Result));
        Copy(new DiagnosticRedactor().Redact(string.Join("\n", lines)).Text);
    }
}
