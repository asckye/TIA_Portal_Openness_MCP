using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.Runtime
{
    // RUNTIME channel to WinCC Unified Runtime over "WinCC Unified Open Pipe": the
    // OpennessManager of the Runtime serves a local named pipe (\\.\pipe\HmiRuntime);
    // clients write one UTF-8 JSON line per request and read JSON lines back on the
    // same pipe instance (manual A5E48117715, "Using expert syntax"). No ODK license,
    // no TIA Openness. Local only: the caller must be in the "SIMATIC HMI" group.
    //
    // Each tool call opens its own pipe instance and closes it afterwards, so nothing
    // (no subscription) outlives the call.

    public sealed class OpenPipeExchange
    {
        public bool Connected;
        public string RequestLine = "";
        public string? ResponseLine;
        public OpenPipeResponse? Response;
        public List<string> SkippedLines = new List<string>();   // lines for other cookies (stray notifications)
        public string? Error;
        public long ElapsedMs;
    }

    public static class UnifiedOpenPipeChannel
    {
        // Sends one line and waits for the first line carrying the expected ClientCookie
        // (and, when command is given, the matching Notify<command>/Error<command> message).
        public static OpenPipeExchange Exchange(string pipeName, string requestLine, string expectedCookie, string? command, int timeoutMs)
        {
            var ex = new OpenPipeExchange { RequestLine = requestLine };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int budget = RuntimeChannelsLogic.ClampTimeout(timeoutMs);
            var (server, name) = RuntimeChannelsLogic.ParsePipeName(pipeName);
            try
            {
                var task = ExchangeAsync(server, name, requestLine, expectedCookie, command, budget, ex);
                if (!task.Wait(budget + 2000) && ex.Error == null)
                    ex.Error = $"Open Pipe '{name}' did not answer within {budget} ms.";
            }
            catch (AggregateException ae)
            {
                ex.Error = ex.Error ?? DescribeFailure(ae.InnerException ?? ae, name);
            }
            catch (Exception e)
            {
                ex.Error = ex.Error ?? DescribeFailure(e, name);
            }
            finally { sw.Stop(); ex.ElapsedMs = sw.ElapsedMilliseconds; }
            return ex;
        }

        private static async Task ExchangeAsync(string server, string name, string requestLine, string expectedCookie, string? command, int budget, OpenPipeExchange ex)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(budget);
            using var pipe = new NamedPipeClientStream(server, name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(budget);
            }
            catch (TimeoutException)
            {
                ex.Error = $"Open Pipe '{name}' is not available (connect timed out after {budget} ms). Is WinCC Unified Runtime running on this machine with a loaded project?";
                return;
            }
            ex.Connected = true;

            var utf8 = new UTF8Encoding(false);
            var payload = utf8.GetBytes(requestLine + "\n");
            await pipe.WriteAsync(payload, 0, payload.Length);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, utf8, false, 64 * 1024, leaveOpen: true);
            while (true)
            {
                int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var readTask = reader.ReadLineAsync();
                if (await Task.WhenAny(readTask, Task.Delay(remaining)) != readTask)
                {
                    ex.Error = $"Open Pipe '{name}' accepted the request but sent no matching response within {budget} ms.";
                    return;
                }
                string? line = await readTask;
                if (line == null)
                {
                    ex.Error = $"Open Pipe '{name}' closed the connection without a response (malformed request or Runtime shutting down).";
                    return;
                }
                var parsed = RuntimeChannelsLogic.ParseResponseLine(line, expectedCookie);
                if (parsed == null || (command != null && !RuntimeChannelsLogic.IsResponseFor(parsed, command)))
                {
                    if (ex.SkippedLines.Count < 20) ex.SkippedLines.Add(RuntimeChannelsLogic.Truncate(line, 300));
                    continue;
                }
                ex.ResponseLine = line;
                ex.Response = parsed;
                return;
            }
        }

        private static string DescribeFailure(Exception e, string name)
        {
            if (e is TimeoutException) return $"Open Pipe '{name}' timed out: {e.Message}";
            if (e is UnauthorizedAccessException) return $"Access to Open Pipe '{name}' was denied. The user running this MCP server must be a member of the 'SIMATIC HMI' group.";
            if (e is FileNotFoundException || (e is IOException io && io.Message.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0))
                return $"Open Pipe '{name}' does not exist on this machine. WinCC Unified Runtime (OpennessManager) must be running locally.";
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
