using System.Text.Json;

return await TiaMcp.Engine.Tests.Shared.FixtureCrashGuard.RunAsync(async () =>
{
    if (args.Length < 2 || args[0] != "--release-key") return 99;
    if (args.Contains("--fixture-wait"))
    {
        Console.WriteLine(Environment.ProcessId);
        await Task.Delay(Timeout.Infinite);
        return 98;
    }
    Console.Error.WriteLine(JsonSerializer.Serialize(new { args, root = Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT") }));
    await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
    int exit = Array.IndexOf(args, "--fixture-exit-code");
    return exit < 0 ? 0 : int.Parse(args[exit + 1], System.Globalization.CultureInfo.InvariantCulture);

});
