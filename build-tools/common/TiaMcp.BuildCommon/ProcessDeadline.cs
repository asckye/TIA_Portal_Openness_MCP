using System.Diagnostics;

namespace TiaMcp.BuildCommon;

// One budget covers both process exit and EOF on the redirected pipes.
public sealed class ProcessDeadline
{
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    public const int QueryMilliseconds = 5 * 60 * 1000;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly int? limit;
    public bool IsQuery { get; }
    private readonly ProcessStartInfo start;

    public ProcessDeadline(ProcessStartInfo start, int? timeoutMilliseconds = null, TimeProvider? clock = null)
    {
        this.start = start;
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
        var args = start.ArgumentList;
        IsQuery = args.FirstOrDefault() == "msbuild" && args.Any(arg =>
            arg.StartsWith("-getItem:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-getProperty:", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("-t:Validate", StringComparison.OrdinalIgnoreCase));
        limit = IsQuery ? Math.Min(QueryMilliseconds, timeoutMilliseconds ?? QueryMilliseconds) : timeoutMilliseconds;
    }

    public void Wait(Func<int, bool> wait, Action stopTree)
    {
        var remaining = limit is int ms ? (int)Math.Clamp(Math.Ceiling(ms - clock.GetElapsedTime(started).TotalMilliseconds), 0, int.MaxValue)
            : Timeout.Infinite;
        if (wait(remaining)) return;
        string command = start.ArgumentList.FirstOrDefault() == "msbuild"
            ? string.Join(' ', start.ArgumentList.Prepend(start.FileName)) : start.FileName;
        var elapsed = clock.GetElapsedTime(started);
        var message = $"{command} timed out after {limit} ms (elapsed {elapsed:c}).";
        try { stopTree(); }
        catch (Exception ex) { throw new TimeoutException($"{message} Process tree cleanup failed: {ex.Message}", ex); }
        throw new TimeoutException(message);
    }

    public static void StopTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited)
        { /* swallow(cleanup): the owned process exited between the wait and termination. */ }
        process.WaitForExit();
    }

    public static (int ExitCode, string StandardOutput, string StandardError) Capture(ProcessStartInfo start, int? timeoutMilliseconds = null)
    {
        var deadline = new ProcessDeadline(start, timeoutMilliseconds);
        using var tree = OperatingSystem.IsWindows() && deadline.limit != null ? WindowsProcessTree.Start(start) : null;
        using var child = tree == null ? new Process { StartInfo = start } : null;
        if (child != null) WindowsProcessTree.StartRegular(child);
        tree?.StandardInput.Close();
        var stdout = (tree?.StandardOutput ?? child!.StandardOutput).ReadToEndAsync();
        var stderr = (tree?.StandardError ?? child!.StandardError).ReadToEndAsync();
        void Stop()
        {
            if (tree != null) tree.Finish();
            else StopTree(child!);
        }
        deadline.Wait(ms => tree?.WaitForExit(ms) ?? child!.WaitForExit(ms), Stop);
        tree?.Finish();
        deadline.Wait(Task.WhenAll(stdout, stderr).Wait, Stop);
        return (tree?.GetExitCode() ?? child!.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    public static (int ExitCode, string Output) RunValidation(string program, IReadOnlyList<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        var result = Capture(start, arguments[0] == "msbuild" ? QueryMilliseconds : null);
        return (result.ExitCode, result.StandardOutput + result.StandardError);
    }
}
