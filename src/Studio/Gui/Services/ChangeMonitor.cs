using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;

namespace TiaOpenness.Gui.Services;

// File notifications only schedule work; filesystem reads run on a pool thread after a burst.
internal sealed class ChangeMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly FileSystemWatcher[] _watchers;
    private readonly Timer _defer;
    private readonly Action _refresh;
    private readonly Action? _idleCheck;
    private const int IdleIntervalMs = 5000;
    private bool _active = true, _disposed, _running, _pending;
    internal ChangeMonitor(Action refresh, params string[] paths) : this(refresh, null, paths) { }

    internal ChangeMonitor(Action refresh, Action? idleCheck, params string[] paths)
    {
        _idleCheck = idleCheck;
        _refresh = refresh;
        _defer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
        _watchers = paths.Select(Path.GetFullPath).Select(path =>
        {
            string? root = path;
            while (root != null && !Directory.Exists(root)) root = Path.GetDirectoryName(root);
            if (root == null) return null;
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? sender, FileSystemEventArgs e)
            {
                if (e.FullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return;
                if (e.FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase) || path
                        .StartsWith(e.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    Request();
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
            watcher.Renamed += (sender, e) => { Changed(sender, e); if (e.OldFullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) Request(); };
            watcher.Error += (_, _) => Request();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }).Where(w => w != null).Cast<FileSystemWatcher>().ToArray();
    }
    internal void Request()
    {
        lock (_sync)
        {
            if (_disposed) return;
            bool scheduled = _pending; _pending = true;
            if (_active && !_running && !scheduled) _defer.Change(75, Timeout.Infinite);
        }
    }
    internal void SetActive(bool active)
    {
        lock (_sync)
        {
            if (_disposed || _active == active) return;
            _active = active;
            foreach (var watcher in _watchers) watcher.EnableRaisingEvents = active;
            if (active) { _pending = true; _defer.Change(1, Timeout.Infinite); }
            else _defer.Change(_idleCheck == null ? Timeout.Infinite : IdleIntervalMs, Timeout.Infinite);
        }
    }
    private void Run()
    {
        Action? work;
        lock (_sync)
        {
            work = _active ? _refresh : _idleCheck;
            if (_disposed || _running || work == null) return;
            _running = true; _pending = false;
        }
        try { work(); }
        catch (Exception ex) { Trace.TraceWarning("Workbench refresh unavailable: " + ex.GetType().Name); }
        finally
        {
            lock (_sync)
            {
                _running = false;
                if (!_disposed && _active && _pending) _defer.Change(75, Timeout.Infinite);
                if (!_disposed && !_active && _idleCheck != null) _defer.Change(IdleIntervalMs, Timeout.Infinite);
            }
        }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _defer.Dispose(); foreach (var watcher in _watchers) watcher.Dispose(); }
    }
}
