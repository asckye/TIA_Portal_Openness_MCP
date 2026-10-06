using Microsoft.Extensions.Logging;
using TiaOpenness.Shared;

namespace TiaMcp.LegacyHost;

internal sealed class HostFileLogger : ILoggerProvider, ILogger
{
    private readonly string releaseKey;
    internal HostFileLogger(string releaseKey) { this.releaseKey = releaseKey; }
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Dispose() { }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Write("TiaMcpServer.log", DateTimeOffset.UtcNow.ToString("O") + " " + formatter(state, exception));
    }
    internal void Write(string name, string text)
    {
        try { DataLocations.Current.AppendLog(name, releaseKey, text); }
        catch (Exception ex) { DataLocations.ReportLogFailure(System.IO.Path.GetFileNameWithoutExtension(name), ex); }
    }
}
