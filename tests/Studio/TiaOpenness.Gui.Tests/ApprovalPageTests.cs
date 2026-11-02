extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ApprovalPageTests(WpfContext wpf)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Enter_never_approves_write_and_Escape_does_not_reject(bool focusApprove)
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture();
            var approve = fixture.Button("ApproveRequest");
            var target = focusApprove ? (UIElement)approve : (UIElement)fixture.View.FindName("RequestTitle");
            target.Focus();
            Assert.True(RaiseKey(target, Keyboard.PreviewKeyDownEvent, Key.Enter));
            Assert.Equal(ApprovalState.Pending, fixture.Service.Requests.Single().State);
            RaiseKey(target, Keyboard.PreviewKeyDownEvent, Key.Escape);
            Assert.Equal(ApprovalState.Pending, fixture.Service.Requests.Single().State);
            Assert.False(approve.IsDefault); Assert.False(approve.IsCancel);
            approve.Focus(); Space(approve);
            Assert.Equal(ApprovalState.Approved, fixture.Service.Requests.Single().State);
        });
    }

    [Fact]
    public void Focused_Reject_keeps_its_normal_Enter_activation_for_write_requests()
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture();
            var reject = fixture.Button("DenyRequest"); reject.Focus();
            Assert.False(reject.IsDefault);
            RaiseKey(reject, Keyboard.PreviewKeyDownEvent, Key.Enter);
            Assert.Equal(ApprovalState.Rejected, fixture.Service.Requests.Single().State);
        });
    }

    [Fact]
    public void Destructive_rejection_is_default_and_Enter_on_Approve_rejects()
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture(ApprovalRisk.Destructive);
            var reject = fixture.Button("DenyRequest");
            Assert.True(reject.IsDefault); Assert.False(reject.IsCancel);
            Assert.Equal(44, reject.ActualHeight);
            Assert.Same(Application.Current.FindResource("Primer.primaryBg"), reject.Background);
            Assert.Equal(Loc.Current["Approval.ApproveDelete"], fixture.Button("ApproveRequest").Content);
            RaiseKey(fixture.Button("ApproveRequest"), Keyboard.PreviewKeyDownEvent, Key.Enter);
            Assert.Equal(ApprovalState.Rejected, fixture.Service.Requests.Single().State);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, false)] [InlineData(AppLanguage.Chinese, false)]
    [InlineData(AppLanguage.English, true)] [InlineData(AppLanguage.Chinese, true)]
    public void Destructive_sheet_requires_Space_or_access_key_and_Enter_cancels(AppLanguage language, bool accessKey)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var fixture = new Fixture(ApprovalRisk.Destructive);
            var sheet = (Grid)fixture.View.FindName("ConfirmationSheet");
            var confirm = fixture.Button("ConfirmDestructive");
            var cancel = fixture.Button("CancelDestructive");
            Open();
            Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel);
            Assert.Same(cancel, Keyboard.FocusedElement);
            Assert.False(confirm.IsDefault); Assert.False(confirm.IsCancel);
            Assert.True(AutomationProperties.GetIsDialog(sheet));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(sheet));
            Assert.False(((FrameworkElement)fixture.View.FindName("PageBody")).IsEnabled);
            confirm.Focus(); RaiseKey(confirm, Keyboard.PreviewKeyDownEvent, Key.Enter);
            Assert.Equal(Visibility.Collapsed, sheet.Visibility);
            Assert.Equal(ApprovalState.Pending, fixture.Service.Requests.Single().State);
            Open(); RaiseKey(cancel, Keyboard.PreviewKeyDownEvent, Key.Escape);
            Assert.Equal(Visibility.Collapsed, sheet.Visibility);
            Assert.Equal(ApprovalState.Pending, fixture.Service.Requests.Single().State);
            Open(); cancel.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.Same(confirm, Keyboard.FocusedElement);
            if (accessKey)
            {
                Assert.True(AccessKeyManager.IsKeyRegistered(PresentationSource.FromVisual(confirm), "a"));
                AccessKeyManager.ProcessKey(PresentationSource.FromVisual(confirm), "a", false);
            }
            else Space(confirm);
            Assert.Equal(ApprovalState.Approved, fixture.Service.Requests.Single().State);
            Assert.Equal(Visibility.Collapsed, sheet.Visibility);
            void Open()
            {
                UnifiedDesktopTests.ClickControl(fixture.Button("ApproveRequest")); WpfContext.Drain();
                Assert.Equal(Visibility.Visible, sheet.Visibility);
                Assert.Equal(ApprovalState.Pending, fixture.Service.Requests.Single().State);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)] [InlineData(AppTheme.Dark)]
    public void Destructive_answers_keep_AA_text_and_an_accent_Reject_hover(AppTheme theme)
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme; var contrast = ThemeManager.Current.HighContrastOverride;
            ThemeManager.Current.HighContrastOverride = false; ThemeManager.Current.Theme = theme;
            try
            {
                using var fixture = new Fixture(ApprovalRisk.Destructive);
                var reject = fixture.Button("DenyRequest");
                Assert.Equal("Primary", Ux.GetVariant(reject));
                Assert.Same(Application.Current.FindResource("Ant.colorPrimaryHover"), ((Border)reject.Template.FindName("Hover", reject)).Background);
                CheckContrast(reject); CheckContrast(fixture.Button("ApproveRequest"));
                UnifiedDesktopTests.ClickControl(fixture.Button("ApproveRequest")); WpfContext.Drain();
                var confirm = fixture.Button("ConfirmDestructive");
                var label = DesktopCapture.Descendants<AccessText>(confirm).Single();
                Assert.Same(Application.Current.FindResource("Primer.destructiveText"), label.Foreground);
                CheckContrast(confirm);
                void CheckContrast(Button button)
                {
                    var foreground = ((SolidColorBrush)button.Foreground).Color;
                    var hover = ((SolidColorBrush)((Border)button.Template.FindName("Hover", button)).Background).Color;
                    double first = ThemeSurfaceTests.Luminance(foreground), second = ThemeSurfaceTests.Luminance(hover);
                    Assert.True((Math.Max(first, second) + .05) / (Math.Min(first, second) + .05) >= 4.5, theme + ": " + button.Name);
                }
            }
            finally { ThemeManager.Current.HighContrastOverride = contrast; ThemeManager.Current.Theme = previous; }
        });
    }

    [Fact]
    public void Multi_operation_title_stays_a_summary_and_keeps_the_complete_operation_list()
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
            wpf.RunWithLanguage(language, () =>
            {
                using var fixture = new Fixture();
                var request = FeaturePageFixtures.Request() with { Operations = Enumerable.Range(0, 50)
                    .Select(i => new ApprovalOperation(LocalizedText.Literal("Import"), "PLC1/block/block-" + i, "ImportPlcBlock")).ToArray() };
                var row = new ApprovalRow(request, fixture.Model);
                Assert.InRange(row.Summary.Length, 1, 400);
                Assert.Contains("50", row.Summary); Assert.Contains(request.Project, row.Summary);
                Assert.DoesNotContain("block-49", row.Summary); Assert.Equal(50, row.Request.Operations.Count);
            });
    }

    [Fact]
    public void Generation_plan_keeps_differences_steps_and_complete_parameters_in_the_detail()
    {
        wpf.Run(() =>
        {
            string hash = new('a', 64);
            using var arguments = System.Text.Json.JsonDocument.Parse("{}");
            string digest = PlcRendering::TiaMcp.Logic.Generation.CanonicalJson.ArgumentHash(arguments.RootElement);
            string plan = $$$"""
                {"schema":"tiamcp.plan/1","planId":"gen-fixture","planHash":"{{{hash}}}",
                 "package":{"id":"fixture.approval","version":"1.0.0","hash":"sha256:{{{hash}}}"},"machineHash":"sha256:{{{hash}}}",
                 "target":{"release":"21","projectIdentity":"C:/fixture/Line.ap21","mode":"existing"},"phases":["plcLibrary","hmi"],
                 "steps":[{"id":"s1","phase":"plcLibrary","key":"PLC1/block/FB_AutoSpeedCtrl","op":"create","tool":"ImportPlcBlock","arguments":{},
                 "dependsOn":[],"argumentDigest":"{{{digest}}}","availability":{"tool":"present","behaviorPolicy":"current","native":"NOT RUN"},"expect":{"readback":"ListPlcBlocks"}}],
                 "skipped":[{"key":"PLC1/block/Legacy","reason":"not selected"}],"conflicts":[{"key":"PLC1/block/Main","reason":"changed","detail":"review first"}],
                 "unavailable":[{"phase":"hmi","reason":"not available in fixture"}],"artifacts":[],"selfCheck":{"errors":0,"warnings":0},"observed":{}}
                """;
            var document = PlcRendering::TiaMcp.Logic.Generation.GenerationDocuments.Load<PlcRendering::TiaMcp.Logic.Generation.GenerationPlan>(plan);
            document.PlanHash = PlcRendering::TiaMcp.Logic.Generation.GenerationDocuments.PlanHash(plan);
            plan = PlcRendering::TiaMcp.Logic.Generation.GenerationDocuments.Canonical(document);
            using var fixture = new Fixture(plan: plan);
            var row = fixture.Model.SelectedRequest!;
            Assert.True(row.IsGenerationPlan); Assert.False(row.IsToolRequest);
            Assert.Equal(Visibility.Collapsed, ((ItemsControl)fixture.View.FindName("ToolOperations")).Visibility);
            var differences = row.GenerationDifferences.ToArray();
            Assert.Equal(3, differences.Length); Assert.All(differences, group => Assert.Single(group.Children));
            Assert.Single(row.GenerationSteps);
            Assert.Single(DesktopCapture.Descendants<DataGrid>(fixture.View).Single().Items);
            UnifiedDesktopTests.ClickControl(DesktopCapture.Descendants<Button>(fixture.View).Single(b => Equals(b.Content, Loc.Current["Approval.CompletePlan"])));
            WpfContext.Drain();
            Assert.True(row.PlanOpen); Assert.Equal(plan, ((TextBox)fixture.View.FindName("AccessibleField1")).Text);
        });
    }

    [Fact]
    public void Selection_history_title_focus_feedback_and_redaction_stay_per_request()
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture();
            var feedback = new List<string>();
            fixture.Model.Feedback += (_, text) => feedback.Add(text.Resolve());
            fixture.View.FocusRequest("first"); WpfContext.Drain();
            Assert.Same(fixture.View.FindName("RequestTitle"), Keyboard.FocusedElement);
            Assert.DoesNotContain("fixture-private-key", fixture.Model.SelectedRequest!.Parameters);
            fixture.Service.Receive(FeaturePageFixtures.Request("second")); WpfContext.Drain();
            UnifiedDesktopTests.ClickControl(fixture.Button("DenyRequest")); WpfContext.Drain();
            Assert.Equal(ApprovalState.Rejected, fixture.Service.Requests[0].State);
            Assert.Equal(ApprovalState.Pending, fixture.Service.Requests[1].State);
            Assert.Equal("second", fixture.Model.SelectedRequest!.Request.Id);
            Assert.Contains(Loc.Current["Approval.Rejected"], feedback.Single());
            fixture.Model.HandledRequests = true;
            Assert.Equal("first", Assert.Single(fixture.Model.VisibleRequests).Request.Id);
            Assert.False(fixture.Model.SelectedRequest!.CanDecide);
            fixture.Model.HandledRequests = false;
            fixture.Model.Decide(fixture.Model.SelectedRequest!, true); WpfContext.Drain();
            Assert.Equal(Loc.Current["Approval.ApprovedToast"], feedback.Last());
        });
    }

    [Fact]
    public void Countdown_announces_only_30_and_10_once_and_timeout_rejects_without_real_waits()
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture(ApprovalRisk.Destructive);
            var announcements = new List<string>();
            void Announced(FrameworkElement element)
            { if (ReferenceEquals(element, fixture.View.FindName("CountdownCaption"))) announcements.Add(AutomationProperties.GetName(element)); }
            AccessibleStatus.Announced += Announced;
            try
            {
                var row = fixture.Model.SelectedRequest!;
                foreach (int remaining in new[] { 90, 31, 30, 30, 29, 11, 10, 10, 9, 1 })
                {
                    fixture.Now = row.Request.Deadline.AddSeconds(-remaining);
                    fixture.Model.RefreshApprovalCountdowns(); WpfContext.Drain();
                    Assert.Equal(remaining < 30 ? FeatureTone.Warning : FeatureTone.Accent, row.CountdownTone);
                }
                Assert.Equal(new[] { Loc.Current.T("Approval.CountdownAnnouncement", 30), Loc.Current.T("Approval.CountdownAnnouncement", 10) }, announcements);
                UnifiedDesktopTests.ClickControl(fixture.Button("ApproveRequest")); WpfContext.Drain();
                fixture.Now = row.Request.Deadline;
                fixture.Model.RefreshApprovalCountdowns(); WpfContext.Drain();
                Assert.Equal(ApprovalState.TimedOut, fixture.Service.Requests.Single().State);
                Assert.False(fixture.Service.Approve("first"));
                Assert.Equal(Visibility.Collapsed, ((Grid)fixture.View.FindName("ConfirmationSheet")).Visibility);
                Assert.Equal(2, announcements.Count);
            }
            finally { AccessibleStatus.Announced -= Announced; }
        });
    }

    [Theory]
    [InlineData(29, 30)] [InlineData(9, 10)]
    public void Delayed_dispatcher_announces_the_current_threshold_without_an_obsolete_30_second_warning(int remaining, int expected)
    {
        wpf.Run(() =>
        {
            using var fixture = new Fixture();
            var thresholds = new List<int>();
            var row = fixture.Model.SelectedRequest!;
            row.CountdownThreshold += (_, threshold) => thresholds.Add(threshold);
            fixture.Now = row.Request.Deadline.AddSeconds(-remaining);
            fixture.Model.RefreshApprovalCountdowns(); fixture.Model.RefreshApprovalCountdowns();
            Assert.Equal(new[] { expected }, thresholds);
        });
    }

    [Theory]
    [InlineData(1024, 640)] [InlineData(1366, 860)]
    public void Page_renders_both_risks_and_languages_with_caption_clear_of_ring_and_HC_selection(int width, int height)
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
        foreach (var risk in new[] { ApprovalRisk.Write, ApprovalRisk.Destructive })
            wpf.RunWithLanguage(language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var oldTheme = ThemeManager.Current.Theme; var oldContrast = ThemeManager.Current.HighContrastOverride;
                ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
                ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
                var service = FeaturePageFixtures.Approvals(false);
                service.Receive(ReviewRequest(risk));
                var window = new MainWindow(ProjectPageTestSupport.Model(release: "21"), false, service, new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
                        new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    window.Navigate("Approvals");
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    var view = (ApprovalsDrawer)window.FindName("ApprovalsContent");
                    Assert.Equal(320, ((Border)view.FindName("RequestListPane")).ActualWidth);
                    var caption = (TextBlock)view.FindName("CountdownCaption");
                    Assert.Equal(120, caption.ActualWidth); Assert.Equal(TextWrapping.Wrap, caption.TextWrapping);
                    var probe = new TextBlock { Text = caption.Text, FontFamily = caption.FontFamily, FontSize = caption.FontSize, TextWrapping = TextWrapping.Wrap };
                    probe.Measure(new Size(120, double.PositiveInfinity));
                    Assert.True(caption.ActualHeight >= probe.DesiredSize.Height - 1);
                    var ring = DesktopCapture.Descendants<Grid>(view).Single(g => g.Width == 64 && g.Height == 64);
                    Assert.True(caption.TranslatePoint(new Point(), view).Y >= ring.TranslatePoint(new Point(), view).Y + 64);
                    var approve = (Button)view.FindName("ApproveRequest"); var reject = (Button)view.FindName("DenyRequest");
                    var selectedItem = (ListBoxItem)((ListBox)view.FindName("RequestList")).ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.Contains(window.Features.SelectedRequest!.Risk, AutomationProperties.GetName(selectedItem));
                    Assert.Equal(approve.ActualWidth, reject.ActualWidth, 3);
                    Assert.Equal(44, approve.ActualHeight); Assert.Equal(44, reject.ActualHeight);
                    foreach (var button in new[] { approve, reject })
                    {
                        var origin = button.TranslatePoint(new Point(), host);
                        Assert.InRange(origin.X, 0, width - button.ActualWidth);
                        Assert.InRange(origin.Y, 0, height - button.ActualHeight);
                    }
                    Assert.Equal(risk == ApprovalRisk.Destructive, reject.IsDefault);
                    Assert.False(reject.IsCancel); Assert.False(approve.IsDefault); Assert.False(approve.IsCancel);
                    if (theme == "HighContrast")
                    {
                        var item = (ListBoxItem)((ListBox)view.FindName("RequestList")).ItemContainerGenerator.ContainerFromIndex(0);
                        Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)item.Background).Color);
                        Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)item.Foreground).Color);
                        Assert.Contains(DesktopCapture.Descendants<Border>(item), b => b.BorderThickness == new Thickness(1)
                            && b.BorderBrush is SolidColorBrush brush && brush.Color == SystemColors.WindowTextColor);
                    }
                    string output = Path.GetFullPath("bin-build/ui2c-renders"); Directory.CreateDirectory(output);
                    string suffix = $"{language}-{theme}-{risk}-{width}x{height}";
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(output, suffix + ".png"), width, height);
                    if (risk == ApprovalRisk.Destructive)
                    {
                        UnifiedDesktopTests.ClickControl(approve); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                        WorkbenchRenderTests.SaveRender(host, Path.Combine(output, "sheet-" + suffix + ".png"), width, height);
                    }
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                }
                finally { window.Close(); ThemeManager.Current.HighContrastOverride = oldContrast; ThemeManager.Current.Theme = oldTheme; }
            });
    }

    private static bool RaiseKey(UIElement element, RoutedEvent routedEvent, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = routedEvent };
        element.RaiseEvent(args); return args.Handled;
    }
    private static void Space(Button button) { RaiseKey(button, Keyboard.KeyDownEvent, System.Windows.Input.Key.Space); RaiseKey(button, Keyboard.KeyUpEvent, System.Windows.Input.Key.Space); }
    private static ApprovalRequest ReviewRequest(ApprovalRisk risk, string id = "req-f379") => FeaturePageFixtures.Request(id) with
    {
        Risk = risk, RiskAction = risk == ApprovalRisk.Destructive ? "delete" : "execute",
        Operations = risk == ApprovalRisk.Destructive ? [new(LocalizedText.Literal(Loc.Current.IsChinese ? "删除块" : "Delete block"), "FC_Legacy_Speed", "DeletePlcBlock")]
            : [new(LocalizedText.Literal(Loc.Current.IsChinese ? "导入块" : "Import block"), "FB_AutoSpeedCtrl", "ImportPlcBlock")],
        Risks = risk == ApprovalRisk.Destructive ? [] : [LocalizedText.Key("Approval.OverwritesExisting")],
    };

    private sealed class Fixture : IDisposable
    {
        internal DateTimeOffset Now = FeaturePageFixtures.Now;
        internal readonly ApprovalServiceStub Service;
        internal readonly FeaturePagesViewModel Model;
        internal readonly ApprovalsDrawer View;
        private readonly Window window;
        internal Fixture(ApprovalRisk risk = ApprovalRisk.Write, string? plan = null)
        {
            Service = new ApprovalServiceStub(() => Now);
            var request = ReviewRequest(risk, "first");
            if (plan != null)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(plan)!;
                request = request with { GenerationPlan = plan, PlanHash = (string)node["planHash"]!,
                    ParametersJson = new System.Text.Json.Nodes.JsonObject { ["expectedPlanHash"] = (string)node["planHash"]!, ["planId"] = (string)node["planId"]! }.ToJsonString(),
                    Operations = node["steps"]!.AsArray().Select(s => new ApprovalOperation(LocalizedText.Literal((string)s!["phase"]!), (string)s["key"]!, (string)s["tool"]!)).ToArray() };
            }
            Service.Receive(request);
            Model = new FeaturePagesViewModel(Service, new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(),
                new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => Now);
            View = new ApprovalsDrawer { DataContext = Model };
            window = new Window { Content = View, Width = 1100, Height = 900 };
            window.Show(); WpfContext.Drain(); window.UpdateLayout();
        }
        internal Button Button(string name) => (Button)View.FindName(name);
        public void Dispose() { window.Close(); Model.Dispose(); }
    }
}
