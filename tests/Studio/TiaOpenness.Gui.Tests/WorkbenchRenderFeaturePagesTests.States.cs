using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class WorkbenchRenderFeaturePagesTests
{
    private static string WriteRetentionFixture(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "diagnostics")).FullName;
        Settings.UiSettings.SaveRetention(Path.Combine(root, "retention"), 5, 3);
        string journal = Path.Combine(directory, "calls-render.jsonl");
        // Populate an active and a rotated invocation journal so the real coverage reader measures both.
        for (int copy = 0; copy < 2; copy++)
        {
            using var writer = File.CreateText(copy == 0 ? journal : journal + ".1");
            for (int row = 0; row < 1024; row++)
            {
                int sequence = (1 - copy) * 1024 + row;
                string time = FeaturePageFixtures.Now.AddSeconds(sequence).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                string id = "review-" + sequence;
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    utc = time, startedUtc = time, phase = "AFTER", mcpProcessId = Environment.ProcessId, id, requestId = id,
                    host = "engine", releaseKey = "21", tool = "GetPlcBlockInfo", actor = "mcp", isWrite = false,
                    ok = true, outcome = "succeeded", execution = "read-only", completeness = "complete", durationMs = 12
                }));
            }
        }
        return directory;
    }

    private static void CaptureConfirmation(MainWindow window, FrameworkElement host, Button trigger, string state,
        string? directory, string suffix, bool apply = false)
    {
        Exception? failure = null;
        bool opened = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = window.OwnedWindows.OfType<FeatureConfirmation>().SingleOrDefault();
            if (dialog == null) return;
            try
            {
                opened = true;
                Assert.True(dialog.CancelAction.IsDefault);
                Assert.False(dialog.ApplyAction.IsDefault);
                Assert.True(window.OwnedDialogScrim.IsVisible);
                Assert.Equal("workbench-modal-open", window.ApplyControl(WorkbenchControlTests.Request()).Refusal?.Condition);
                Assert.Equal(2, dialog.Fields.Items.Count);
                if (directory == null) return;
                dialog.Left = window.Left + (window.ActualWidth - dialog.ActualWidth) / 2;
                dialog.Top = window.Top + (window.ActualHeight - dialog.ActualHeight) / 2;
                dialog.UpdateLayout(); host.UpdateLayout();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(DesktopCapture.Render(host, (FrameworkElement)dialog.Content)));
                using var output = File.Create(Path.Combine(directory, state + "-" + suffix + ".png"));
                encoder.Save(output);
            }
            catch (Exception ex) { failure = ex; }
            finally { UnifiedDesktopTests.ClickControl(apply && failure == null ? dialog.ApplyAction : dialog.CancelAction); }
        }));
        UnifiedDesktopTests.ClickControl(trigger);
        Assert.True(opened);
        Assert.Null(failure);
        Assert.False(window.OwnedDialogScrim.IsVisible);
    }

    private static void CaptureAuditVerification(MainWindow window, FeaturePageFixtures.Journal journal, FeaturePageFixtures.Environment environment,
        Action<string> capture)
    {
        var service = new FeaturePageFixtures.Audit();
        window.ConfigureFeaturePages(journal, service, environment, now: () => FeaturePageFixtures.Now);
        window.Navigate("Audit");
        using var finish = new ManualResetEventSlim();
        service.BeforeVerify = finish.Wait;
        var verifying = window.Features.VerifyAsync();
        try
        {
            Assert.True(window.Features.Verifying);
            Assert.False(window.AuditContent.VerifyButton.IsEnabled);
            capture("audit-verifying");
        }
        finally { finish.Set(); WpfContext.Complete(verifying); service.BeforeVerify = null; }
        using var fixture = new SafetySettingsServiceTests.Fixture();
        fixture.Audit.Append("request", "verification-fixture", "engine", "21", "WriteFixture");
        fixture.Audit.Approval("verification-fixture", "engine", "21", "WriteFixture", "granted");
        fixture.Audit.Append("start", "verification-fixture", "engine", "21", "WriteFixture");
        fixture.Audit.Append("end", "verification-fixture", "engine", "21", "WriteFixture", "succeeded");
        string directory = Path.Combine(fixture.Root, "audit");
        void OpenChain()
        {
            var audit = new Services.AuditLogService(directory, Path.Combine(fixture.Root, "diagnostics"), Path.Combine(fixture.Root, "retention"));
            WpfContext.Complete(audit.RefreshAsync());
            window.ConfigureFeaturePages(journal, audit, environment, now: () => FeaturePageFixtures.Now);
        }
        OpenChain();
        WpfContext.Complete(window.Features.VerifyAsync());
        Assert.True(window.Features.ShowVerification);
        Assert.False(window.Features.VerificationBroken);
        capture("audit-ok");
        string file = Directory.GetFiles(directory, "audit-*.jsonl").Single();
        var lines = File.ReadAllLines(file);
        var changed = System.Text.Json.Nodes.JsonNode.Parse(lines[2])!.AsObject();
        changed["previousHash"] = new string('0', 64);
        lines[2] = changed.ToJsonString();
        File.WriteAllLines(file, lines);
        OpenChain();
        WpfContext.Complete(window.Features.VerifyAsync());
        Assert.True(window.Features.VerificationBroken);
        Assert.Equal(3, Assert.Single(window.Features.AuditRows, row => row.Selected).Record.Index);
        UnifiedDesktopTests.ClickControl(window.AuditContent.JumpButton);
        Assert.Equal(window.Features.BreakIndex, window.Features.SelectedAudit?.Record.Index);
        Assert.True(window.Features.SelectedAudit!.HasMismatchedLink);
        capture("audit-broken");
        service.Events = [];
        window.ConfigureFeaturePages(journal, service, environment, now: () => FeaturePageFixtures.Now);
        capture("audit-empty");
        Assert.True(window.Features.AuditEmpty);
        Assert.False(window.AuditContent.VerifyButton.IsEnabled);
    }
}
