using System;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class LauncherTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(461808, false)]
    [InlineData(528039, false)]
    [InlineData(528040, true)]
    [InlineData(528049, true)]
    [InlineData(533320, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData("528040", false)]
    public void Missing_key_or_release_below_48_is_rejected(object? release, bool expected)
    {
        Assert.Equal(expected, DesktopLauncher.HasRequiredFramework(release!));
    }

    [Fact]
    public void Missing_framework_message_is_bilingual_and_names_the_runtime_to_install()
    {
        Assert.Contains("缺少 Microsoft .NET Framework 4.8", DesktopLauncher.MissingFrameworkMessage, StringComparison.Ordinal);
        Assert.Contains("Install the Runtime", DesktopLauncher.MissingFrameworkMessage, StringComparison.Ordinal);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet-framework/net48", DesktopLauncher.MissingFrameworkMessage, StringComparison.Ordinal);
    }
}
