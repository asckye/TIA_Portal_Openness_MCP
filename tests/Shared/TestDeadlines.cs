#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaMcp.Engine.Tests.Shared;

public static class TestDeadlines
{
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(120);
    public const int HangMilliseconds = 120_000;
    private static readonly object ReportSync = new object();

    public static void Report(string metric, double value, string unit)
    {
        string number = value.ToString("R", CultureInfo.InvariantCulture);
        Console.WriteLine("TREND " + metric + " " + number + " " + unit);
        lock (ReportSync)
        {
            string? directory = Environment.GetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            using var process = Process.GetCurrentProcess();
            string path = Path.Combine(directory, "trend-" + process.Id.ToString(CultureInfo.InvariantCulture) + ".jsonl");
            string row = "{\"metric\":" + Quote(metric) + ",\"value\":" + number + ",\"unit\":" + Quote(unit) + "}";
            File.AppendAllText(path, row + "\n", new UTF8Encoding(false));
        }
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char character in value)
        {
            if (character == '"' || character == '\\') text.Append('\\').Append(character);
            else if (character < ' ') text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else text.Append(character);
        }
        return text.Append('"').ToString();
    }
}
