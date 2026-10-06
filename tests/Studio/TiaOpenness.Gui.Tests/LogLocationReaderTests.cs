using System;
using System.IO;
using System.Linq;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class LogLocationReaderTests(WpfContext wpf)
{
    [Fact]
    public void Environment_tail_diagnostic_bundle_and_audit_reader_find_process_files()
    {
        wpf.Run(() =>
        {
            string root = Path.GetFullPath(Path.Combine("bin-build", "P6-39", "reader-fixtures", Guid.NewGuid().ToString("N")));
            var data = DataLocations.Resolve(root, Path.Combine(root, "data"), Path.Combine(root, "local"), Path.Combine(root, "temp"));
            data.AppendLog("TiaMcpServer.log", "20", "engine-started");
            data.AppendLog("TiaOpenness.crash.log", "studio", "studio-crashed");
            string chainA = data.AuditDirectory;
            string chainB = data.AuditDirectory;
            new AuditLog(chainA).Append("request", "a", "engine", "20", "WriteA");
            new AuditLog(chainB).Append("request", "b", "engine", "21", "WriteB");
            var context = new EnvironmentCheckContext { DataRoot = data.Root, ConfigDirectory = data.ConfigDirectory,
                LogsDirectory = data.LogsDirectory, DiagnosticsDirectory = data.DiagnosticsDirectory, AuditDirectory = data.AuditDirectory };
            var tail = EnvironmentCheckService.ReadLogTail(context.LogsDirectory);
            Assert.Contains(tail, row => row.Contains("engine-started"));
            Assert.Contains(tail, row => row.Contains("studio-crashed"));
            var inputs = DiagnosticBundleService.Inputs(context);
            Assert.Contains(inputs, input => input.Path == data.LogFile("TiaMcpServer.log", "20"));
            Assert.Contains(inputs, input => input.Path == data.LogFile("TiaOpenness.crash.log", "studio"));
            Assert.Single(inputs, input => input.Path.Contains("audit-"));
            Assert.Equal(inputs.Count, inputs.Select(input => input.Entry).Distinct().Count());
            using var service = new AuditLogService(data.AuditDirectory, data.DiagnosticsDirectory,
                Path.Combine(data.ConfigDirectory, "journal-retention.settings"), _ => { });
            Assert.Equal(2, service.TotalCount); Assert.True(service.Verify().Passed);
            Assert.Equal(2, service.Verify().Count);
        });
    }
    [Fact]
    public void Primary_and_fallback_audit_chains_verify_separately_and_identify_the_break()
    {
        wpf.Run(() =>
        {
            string root = Path.GetFullPath(Path.Combine("bin-build", "P6-39", "reader-fixtures", Guid.NewGuid().ToString("N")));
            var data = DataLocations.Resolve(root, Path.Combine(root, "data"), Path.Combine(root, "local"), Path.Combine(root, "temp"));
            string primary = data.AuditDirectory, fallback = Path.Combine(root, "local", "TiaMcp", "logs", "audit");
            data.AppendLog("TiaMcpServer.log", "21", "primary-tail");
            string fallbackLogs = Path.Combine(root, "temp", "TiaMcp", "logs");
            Directory.CreateDirectory(Path.Combine(fallbackLogs, "21"));
            string mainLog = data.LogFile("TiaMcpServer.log", "21");
            File.WriteAllText(Path.Combine(fallbackLogs, "21", Path.GetFileName(mainLog)), "fallback-tail");
            var first = new AuditLog(primary); var second = new AuditLog(fallback);
            first.Append("request", "a", "engine", "20", "WriteA");
            second.Append("request", "b", "engine", "21", "WriteB");
            second.Append("start", "b", "engine", "21", "WriteB");
            var context = new EnvironmentCheckContext { DataRoot = data.Root, ConfigDirectory = data.ConfigDirectory,
                LogsDirectory = data.LogsDirectory, DiagnosticsDirectory = data.DiagnosticsDirectory,
                AuditDirectory = primary, AuditReadRoots = new[] { primary, fallback }, LogReadRoots = new[] { data.LogsDirectory, fallbackLogs } };
            var inputs = DiagnosticBundleService.Inputs(context);
            Assert.Equal(2, inputs.Count(input => Path.GetFileName(input.Path) == Path.GetFileName(mainLog)));
            var tail = EnvironmentCheckService.ReadLogTail(context.LogReadRoots);
            Assert.Contains(tail, row => row.Contains("primary-tail")); Assert.Contains(tail, row => row.Contains("fallback-tail"));
            Assert.Equal(2, inputs.Count(input => input.Path.Contains("audit-")));
            Assert.Equal(inputs.Count, inputs.Select(input => input.Entry).Distinct().Count());
            using var service = new AuditLogService(primary, data.DiagnosticsDirectory,
                Path.Combine(data.ConfigDirectory, "journal-retention.settings"), _ => { }, new[] { primary, fallback });
            Assert.Single(service.Events); Assert.True(service.Verify().Passed); Assert.Equal(3, service.Verify().Count);
            string file = Directory.GetFiles(fallback, "audit-*.jsonl").Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("WriteB", "Changed", StringComparison.Ordinal));
            var broken = service.Verify();
            Assert.False(broken.Passed); Assert.Equal(fallback, broken.Chain); Assert.Equal(Path.GetFileName(file), broken.File);
            Assert.Equal(2, broken.BreakIndex); Assert.Equal(2, service.Events.Count);
            Assert.Equal(2, broken.Chains.Count); Assert.True(broken.Chains[0].Passed); Assert.False(broken.Chains[1].Passed);
            Assert.Equal("Changed", service.Events.Last().ToolObject);
            // An unreadable chain must not leave rows from the previously selected chain addressable.
            string primaryLock = Path.Combine(primary, ".audit.lock");
            File.Delete(primaryLock); Directory.CreateDirectory(primaryLock);
            var unreadable = service.Verify();
            Assert.False(unreadable.Passed); Assert.Equal(primary, unreadable.Chain); Assert.Empty(service.Events);
        });
    }

}
