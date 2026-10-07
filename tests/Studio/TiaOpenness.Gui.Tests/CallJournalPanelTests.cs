using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchRenderFeaturePagesTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class CallJournalPanelTests(WpfContext wpf)
{
    private static FeaturePagesViewModel Model(IApprovalService? approvals = null, ICallJournalService? journal = null)
        => new(approvals ?? FeaturePageFixtures.Approvals(), journal ?? new FeaturePageFixtures.Journal(),
            new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);

    [Fact]
    public void Real_service_uses_saved_endpoint_and_ClientProfiles_without_decrypting_keys()
    {
        wpf.Run(() =>
        {
            string root = Path.Combine(AppContext.BaseDirectory, "panel-journal", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "http-v21.json"), JsonSerializer.Serialize(new
                { Address = "127.0.0.1", Port = 8765, ProtectedKey = "private-invalid-protected-key" }));
                using var service = new CallJournalService("21", root, root, false);
                Assert.Equal("http://127.0.0.1:8765/mcp", service.Connection.Address);
                Assert.Equal("HTTP", service.Connection.Transport);
                Assert.Equal(TiaMcpConfigurator.ClientProfiles.ConnectionSnippet("127.0.0.1", 8765), service.Connection.ConfigurationJson);
                Assert.Contains("Bearer ••••", service.Connection.ConfigurationJson); Assert.DoesNotContain("private-", service.Connection.ConfigurationJson);
                File.WriteAllText(Path.Combine(root, "http-v20.json"), "{broken private-key");
                service.SetRelease("20"); service.Refresh(); Assert.Empty(service.Connection.Address);
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void Pending_approvals_remain_live_on_top_of_a_paused_journal_and_filters_work()
    {
        wpf.Run(() =>
        {
            var approvals = FeaturePageFixtures.Approvals(false);
            var journal = new FeaturePageFixtures.Journal(); journal.Replace(journal.Calls.Where(c => c.Result != CallResult.Pending).ToArray());
            using var model = Model(approvals, journal);
            model.ToggleFollow(); approvals.Receive(FeaturePageFixtures.Request("fresh")); WpfContext.Drain();
            Assert.True(model.Calls[0].Pending); Assert.Equal("fresh", model.Calls[0].Record.RequestId);
            model.WriteOnly = true; Assert.Single(model.Calls);
            model.FailOnly = true; Assert.Empty(model.Calls);
            model.WriteOnly = false; Assert.Single(model.Calls);
            model.FailOnly = false; model.ReleaseOnly = true; model.Release = "21"; Assert.Empty(model.Calls);
            model.Release = "14sp1"; Assert.Equal(4, model.Calls.Count);
            approvals.Deny("fresh"); WpfContext.Drain(); Assert.Equal(3, model.Calls.Count);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, "PARTIAL_FAILURE", "execution", "Partial", "preview limited")]
    [InlineData(AppLanguage.Chinese, "OUTCOME_UNKNOWN", "执行", "未知", "预览最多")]
    public void Detail_and_error_text_follow_language_and_keep_sanitized_truncated_previews(AppLanguage language,
        string code, string label, string status, string note)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var original = new FeaturePageFixtures.Journal().Calls[0];
            var call = new CallRow(original with
            {
                Result = language == AppLanguage.English ? CallResult.Partial : CallResult.Unknown,
                Outcome = "partial", Execution = "partial", Completeness = "partial", ErrorCode = code,
                ResultJson = new string('x', 4050) + TiaOpenness.Shared.CallJournalPayload.Truncated, ResultTruncated = true
            });
            Assert.Contains(label, call.Summary); Assert.Contains(status, call.Result);
            Assert.Contains(EngineResultText.Error(code), call.Error); Assert.Contains(note, call.PreviewNote);
            Assert.Contains("truncated", call.ResultJson); Assert.True(call.ResultJson.Length <= 4096);
            Assert.Equal(FeatureTone.Warning, call.Tone);
        });
    }

    [Fact]
    public void Same_request_id_in_different_host_processes_opens_the_selected_record()
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal();
            var first = journal.Calls[0] with { RequestId = "same", JournalKey = "host-one" };
            var second = first with { Host = "other", JournalKey = "host-two", ResultJson = "{\"value\":42}" };
            journal.Replace([first, second]);
            using var model = Model(journal: journal);
            model.OpenCall(model.Calls.Single(c => c.Host.StartsWith("other", StringComparison.Ordinal)));
            Assert.Equal("host-two", model.SelectedCall!.Record.JournalKey); Assert.Contains("42", model.SelectedCall.ResultJson);
        });
    }
}
