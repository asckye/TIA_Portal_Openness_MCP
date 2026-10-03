using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Core.Tests;

[CollectionDefinition("Console input encoding", DisableParallelization = true)]
public sealed class ConsoleInputEncodingCollection { }

[Collection("Console input encoding")]
public sealed class LocalProcessTests
{
    [Theory]
    [InlineData(65001, "{\"value\":\"中文🟦\"}\nsecond\n")]
    [InlineData(65001, "")]
    [InlineData(65001, null)]
    [InlineData(936, "{\"value\":\"中文🟦\"}\nsecond\n")]
    [InlineData(936, "")]
    [InlineData(936, null)]
    public async Task Child_stdin_is_exact_UTF8_without_BOM_and_closes_at_EOF(int codePage, string? input)
    {
        var previous = Console.InputEncoding;
        try
        {
            Console.InputEncoding = codePage == 65001 ? Encoding.UTF8 : CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
            string script = "$inputStream=[Console]::OpenStandardInput(); $bytes=New-Object IO.MemoryStream; " +
                "$inputStream.CopyTo($bytes); [Console]::WriteLine([BitConverter]::ToString($bytes.ToArray()))";
            string child = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await LocalProcess.Run(child, new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, System.Environment.CurrentDirectory, input, 10);
            Assert.True(result.Success, result.Stderr);
            Assert.Equal(BitConverter.ToString(new UTF8Encoding(false).GetBytes(input ?? "")), result.Stdout.Trim());
        }
        finally { Console.InputEncoding = previous; }
    }
}
