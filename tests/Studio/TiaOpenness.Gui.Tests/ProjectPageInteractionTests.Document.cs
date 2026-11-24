using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class ProjectPageInteractionTests
{
    [Fact]
    public async Task Reading_source_uses_the_exact_encoded_path_and_only_the_logic_read()
    {
        const string payload = """
            {"ok":true,"data":{"blockPath":"Mix/A%2FB","language":"SCL","readable":"── 程序段 1  [SCL]\n   #Ready := TRUE;\n",
             "summary":"Logic decoded.","evidence":{"language":"SCL"}}}
            """;
        var session = new TagSession(false, reply: payload);
        using var client = new McpProjectClient(session);
        Assert.Equal("── 程序段 1  [SCL]\n   #Ready := TRUE;\n", await client.DescribeBlockLogicAsync("devices/Station/CPU", "Mix/A%2FB"));
        var call = Assert.Single(session.Calls);
        Assert.Equal("DescribePlcBlockLogic", call.Tool);
        Assert.Equal(2, call.Args.Count);
        Assert.Equal("devices/Station/CPU", call.Args["softwarePath"]);
        Assert.Equal("Mix/A%2FB", call.Args["blockPath"]);
    }

    [Theory]
    [InlineData("""{"ok":false,"data":null,"error":{"code":"NOT_FOUND","message":"Block missing.","details":{"target":"Mix/A"}}}""",
        "Block missing.")]
    [InlineData("""{"ok":true,"data":{"readable":"Warning","evidence":{"xmlContent":"interface-only"},"summary":"Only declarations were exported."}}""",
        "Only declarations were exported.")]
    public async Task A_refused_or_interface_only_logic_read_preserves_the_reason(string reply, string reason)
    {
        var session = new TagSession(false, reply: reply);
        using var client = new McpProjectClient(session);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DescribeBlockLogicAsync("CPU", "Mix/A"));
        Assert.Contains(reason, error.Message);
        Assert.Equal("DescribePlcBlockLogic", Assert.Single(session.Calls).Tool);
    }

    [Fact]
    public async Task Offline_source_reads_use_the_session_refusal()
    {
        await using var offline = new OfflineSessionClient();
        using var client = new McpProjectClient(offline);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DescribeBlockLogicAsync("CPU", "Start"));
        Assert.Contains("Offline demo", error.Message);
        Assert.Empty(client.Receipts);
    }

    [Fact]
    public void Csv_export_preserves_the_five_display_fields_and_escapes_cell_content()
    {
        using var writer = new StringWriter();
        PlcTagsView.WriteCsv(writer, [new() { Name = "=speed", DataType = "Real", LogicalAddress = "%QD2",
            TableName = "Drives", Comment = "Run, \"fast\"\r\nnow" }]);
        Assert.Equal("Name,DataType,LogicalAddress,Table,Comment" + writer.NewLine +
            "\"'=speed\",\"Real\",\"%QD2\",\"Drives\",\"Run, \"\"fast\"\"\r\nnow\"" + writer.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Completed_compile_errors_keep_the_leaf_diagnostics()
    {
        const string reply = """
            {"ok":false,"error":{"code":"COMPILE_ERRORS","message":"Compile completed with errors.",
             "details":{"errorCount":2,"warningCount":0,"errors":[{"path":"Mixer (DB1)","description":"Input address missing."}]}},
             "data":{"state":"Error","errorCount":2,"warningCount":0,"messages":[]}}
            """;
        var session = new TagSession(false, reply: reply);
        using var client = new McpProjectClient(session);
        var result = await client.CompileAsync("CPU");
        Assert.Equal(("Error", 2, 0), (result.State, result.ErrorCount, result.WarningCount));
        var error = Assert.Single(result.Messages);
        Assert.Equal((CompileSeverity.Error, "Mixer (DB1)", "Input address missing."), (error.Severity, error.Target, error.Description));
        Assert.Equal(new[] { true, false }, session.Calls.Select(c => (bool)c.Args["dryRun"]!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refused_compile_is_never_displayed_as_a_completed_compile(bool refusePreview)
    {
        const string refusal = """
            {"ok":false,"data":null,"error":{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}}}
            """;
        var session = new TagSession(false, reply: refusal, previewReply: refusePreview ? refusal : null);
        using var client = new McpProjectClient(session);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompileAsync("CPU"));
        Assert.Contains("PROJECT_NOT_BOUND", error.Message);
        Assert.Equal(refusePreview ? new[] { true } : new[] { true, false }, session.Calls.Select(c => (bool)c.Args["dryRun"]!));
    }

    [Fact]
    public async Task Flat_native_compiler_children_inherit_the_object_path_and_keep_host_error_counts()
    {
        const string reply = """
            {"ok":false,"error":{"code":"COMPILE_ERRORS","message":"Compile errors.",
             "details":{"errorCount":1,"warningCount":1,"errors":[{"path":"Mixer (DB1)","description":"Address missing."}]}},
             "data":{"errorCount":0,"warningCount":1,"evidence":{"nodes":[
               {"treePath":"messages/0","parentPath":"messages","State":"Error","Description":"Summary","Path":"Mixer (DB1)"},
               {"treePath":"messages/0/messages/0","parentPath":"messages/0/messages","State":"Error","Description":"Address missing.","ErrorCode":"E10"},
               {"treePath":"messages/0/messages/1","parentPath":"messages/0/messages","State":"Warning","Description":"Unused input."}]}}}
            """;
        using var client = new McpProjectClient(new TagSession(false, reply: reply));
        var result = await client.CompileAsync("CPU");
        Assert.Equal((1, 1), (result.ErrorCount, result.WarningCount));
        Assert.Equal(new[] { "Address missing.", "Unused input." }, result.Messages.Select(row => row.Description));
        Assert.Equal("E10", result.Messages[0].ErrorCode);
        Assert.All(result.Messages, row => Assert.Equal("Mixer (DB1)", row.Target));
    }

    [Fact]
    public async Task Compile_attempt_diagnostics_preserve_the_existing_summary_fields()
    {
        const string payload = """
            {"ok":true,"data":{"attempt":{"diagnostics":{"state":"Warning","rootErrorCount":0,"rootWarningCount":3,"messages":[]}}}}
            """;
        using var client = new McpProjectClient(new TagSession(false, reply: payload));
        var result = await client.CompileAsync("CPU");
        Assert.Equal(("Warning", 0, 3), (result.State, result.ErrorCount, result.WarningCount));
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task Compiler_messages_keep_native_counts_severity_path_and_description()
    {
        const string reply = """
            {"ok":true,"data":{"state":"Warning","errorCount":0,"warningCount":1,"messages":[
              "State=Warning; Description=Unused variable; remove declaration.; Path=CPU/Mix; DateTime=2026-11-20T01:02:03Z; ErrorCount=0; WarningCount=1"]}}
            """;
        var session = new TagSession(false, reply: reply);
        using var client = new McpProjectClient(session);
        var result = await client.CompileAsync("CPU");
        var row = Assert.Single(result.Messages);
        Assert.Equal((0, 1, "Warning"), (result.ErrorCount, result.WarningCount, result.State));
        Assert.Equal((CompileSeverity.Warning, "CPU/Mix", "Unused variable; remove declaration."), (row.Severity, row.Target, row.Description));
        Assert.Equal(new[] { true, false }, session.Calls.Select(c => (bool)c.Args["dryRun"]!));
        Assert.All(session.Calls, c => Assert.Equal("CompilePlcSoftware", c.Tool));
    }

    [Fact]
    public void Late_source_reads_cannot_replace_a_new_block_and_protected_blocks_are_never_read()
    {
        wpf.Run(() =>
        {
            var pending = new TaskCompletionSource<string>();
            var client = new FakeProjectClient { LogicHandler = (_, _) => pending.Task };
            using var model = new MainViewModel(client, new FakeDialogService());
            WpfContext.Complete(model.Engineering.LoadDevicesAsync("Line"));
            var editor = model.Engineering;
            editor.FocusedBlock = editor.Blocks.Single();
            var read = editor.ReadFocusedBlockAsync();
            Assert.True(model.Busy);
            Assert.False(editor.Compile.CanExecute(null));
            Assert.False(editor.Save.CanExecute(null));
            Assert.Empty(editor.SourceLines);
            Assert.Equal(Loc.Current["Project.LoadingCode"], editor.SourceStatus);
            foreach (bool protectedBlock in new[] { true, false })
            {
                editor.FocusedBlock = new BlockRow(new() { Name = "Unavailable", IsConsistent = false, IsKnowHowProtected = protectedBlock });
                WpfContext.Complete(editor.ReadFocusedBlockAsync());
                Assert.Equal(Loc.Current[protectedBlock ? "Block.Protected" : "Block.NeedsCompiling"], editor.SourceStatus);
            }
            pending.SetResult("old source"); WpfContext.Complete(read);
            Assert.False(model.Busy);
            Assert.Empty(editor.SourceLines);
            Assert.Single(client.Calls, c => c.Name == "DescribePlcBlockLogic");
            editor.FocusedBlock = editor.Blocks.Single();
            client.LogicHandler = (_, _) => Task.FromException<string>(new IOException("Read interrupted."));
            WpfContext.Complete(editor.ReadFocusedBlockAsync());
            Assert.Equal("Read interrupted.", model.Activity.Status);
            Assert.Equal("Read interrupted.", editor.SourceStatus);
            Assert.Empty(editor.SourceLines);
            client.LogicHandler = (_, _) => Task.FromResult("A\r\nB\n\n");
            WpfContext.Complete(editor.ReadFocusedBlockAsync());
            Assert.Equal(new[] { "A", "B", "" }, editor.SourceLines);
        });
    }

    [Fact]
    public void Project_documents_keep_the_pending_prefill_in_the_shell_notification_stack()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                var request = WorkbenchControlTests.Request(TiaOpenness.Shared.WorkbenchControlOperation.PrefillForm);
                Assert.Equal(TiaOpenness.Shared.WorkbenchControlStatus.Done, window.ApplyControl(request).Status);
                var notice = window.FindToastCard("ai-prefill")!.Notice;
                void CheckCard()
                {
                    var card = window.FindToastCard("ai-prefill")!;
                    Assert.Same(notice, card.Notice);
                    Assert.Same(window.ToastStack, card.Host.Parent);
                    Assert.True(card.Surface.IsVisible);
                }
                window.Navigate("Log");
                CheckCard();
                window.Navigate("Engineering");
                CheckCard();
                ((MainViewModel)window.DataContext).Engineering.Document = ProjectDocument.Tags;
                CheckCard();
                window.Navigate("VersionControl");
                Assert.Equal("VersionControl", window.CurrentPage);
                CheckCard();
                window.Navigate("Log");
                CheckCard();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Only_SCL_tokens_get_syntax_brushes_and_the_read_text_is_preserved()
    {
        wpf.Run(() =>
        {
            const string source = "IF #Ready THEN #State := 'RUN'; #Count := 10; // Keep running";
            var line = new ProjectSourceLine { SourceText = source, LanguageName = "SCL" };
            var runs = line.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
            Assert.Equal(source, string.Concat(runs.Select(run => run.Text)));
            var expected = new[] { ("IF", "Keyword"), ("#Ready", "Variable"), ("'RUN'", "String"), ("10", "Number"), ("// Keep running", "Comment") };
            foreach (var (text, token) in expected)
                Assert.Same(line.FindResource("Primer.syntax" + token), runs.Single(run => run.Text == text).Foreground);
            line.LanguageName = "LAD";
            Assert.Equal(source, Assert.Single(line.Inlines.OfType<System.Windows.Documents.Run>()).Text);
        });
    }

    [Fact]
    public void Tree_keyboard_and_mouse_select_blocks_without_exporting()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model();
            model.Engineering.SelectAll(false);
            var window = new MainWindow(model, false) { Width = 1366, Height = 860 };
            try
            {
                WpfContext.ShowWindow(window); WpfContext.Drain();
                var leaves = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>(window.ProjectExplorer.Nodes)
                    .Where(item => item.Header is BlockNode { Row: not null }).ToList();
                Assert.Equal(model.Engineering.Blocks.Count, leaves.Count);
                static BlockRow Row(TreeViewItem item) => ((BlockNode)item.Header).Row!;
                WpfContext.WithControlPressed(() => ClickNode(leaves[0]));
                Assert.True(Row(leaves[0]).Selected);
                Assert.True(leaves[1].Focus());
                PressTreeKey(leaves[1], Key.Space);
                Assert.True(Row(leaves[1]).Selected);
                PressTreeKey(leaves[1], Key.Down);
                Assert.Same(Row(leaves[1]), model.Engineering.FocusedBlock);
                Assert.Equal(2, model.Engineering.Blocks.Count(row => row.Selected));
                window.ProjectExplorer.SelectRangeOrToggle(model.Engineering, Row(leaves[3]), ModifierKeys.Shift);
                Assert.All(leaves.Take(4), item => Assert.True(Row(item).Selected));
                Assert.All(leaves.Skip(4), item => Assert.False(Row(item).Selected));
                var folder = (TreeViewItem)ItemsControl.ItemsControlFromItemContainer(leaves[0]);
                Assert.True(folder.Focus());
                var menu = window.ProjectExplorer.Nodes.ContextMenu;
                menu.PlacementTarget = window.ProjectExplorer.Nodes;
                menu.IsOpen = true;
                WpfContext.Drain();
                Assert.True(window.ProjectExplorer.ClearGroup.IsEnabled);
                window.ProjectExplorer.ClearGroup.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.All(((BlockNode)folder.Header).Leaves(), row => Assert.False(row.Selected));
                window.ProjectExplorer.SelectGroup.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.All(((BlockNode)folder.Header).Leaves(), row => Assert.True(row.Selected));
                menu.IsOpen = false;
                WpfContext.WithControlPressed(() => PressTreeKey(leaves[1], Key.A));
                Assert.All(model.Engineering.Blocks, row => Assert.True(row.Selected));
            }
            finally { window.Close(); }
        });
    }
}
