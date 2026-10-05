using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpenness.Core;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Tests;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class EngineResultTextTests(WpfContext wpf)
{
    public static IEnumerable<object[]> Errors() => V4Fixtures.Supplemental().Skip(1)
        .Select(e => new object[] { e.GetRawText() });

    [Theory, MemberData(nameof(Errors))]
    public void EveryV4CodeIsLocalizedInBothLanguagesAndInTheCallRow(string json)
    {
        var result = EngineToolResult.Read(json);
        string english = "", chinese = "";
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
            wpf.RunWithLanguage(language, () =>
            {
                string text = EngineResultText.Error(result);
                Assert.NotEmpty(text); Assert.DoesNotContain("[Engine.", text);
                Assert.NotEqual(result.Error.Message, text);
                if (language == AppLanguage.English) english = text; else chinese = text;
                var row = new CallRow(new CallRecord("r", DateTimeOffset.UtcNow, "host", "21", "tool", false,
                    CallResult.Failed, 1, "target", "{}", LocalizedText.Empty, result.ErrorCode,
                    LocalizedText.Literal("Untranslated native detail"), LocalizedText.Empty));
                Assert.Contains(text, row.Error);
            });
        Assert.NotEqual(english, chinese);
        Assert.Contains(chinese, c => c >= '\u4e00' && c <= '\u9fff');
    }

    [Theory]
    [InlineData("PARTIAL_FAILURE", "Partial result", "部分结果")]
    [InlineData("OUTCOME_UNKNOWN", "Unknown outcome", "结果未知")]
    public void IncompleteOutcomesNeverDisplayAsSuccess(string code, string english, string chinese)
    {
        var result = V4Fixtures.Supplemental().Select(e => EngineToolResult.Read(e.GetRawText())).Single(r => r.ErrorCode == code);
        var text = EngineResultText.Outcome(result);
        Assert.False(result.IsCompleteSuccess);
        wpf.RunWithLanguage(AppLanguage.English, () => Assert.Equal(english, text.Resolve()));
        wpf.RunWithLanguage(AppLanguage.Chinese, () => Assert.Equal(chinese, text.Resolve()));
    }
}
