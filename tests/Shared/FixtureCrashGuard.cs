using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcp.Engine.Tests.Shared
{
    internal static class FixtureCrashGuard
    {
        internal const int UnhandledExceptionExitCode = 86;
        internal const string Prefix = "FIXTURE_UNHANDLED_EXCEPTION ";
        private static readonly object gate = new object();
        private static int registered;
        private static bool reported;

        internal static void Register()
        {
            if (Interlocked.Exchange(ref registered, 1) != 0) return;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                // net48's legacy policy deliberately permits the PlcWorker background fault to continue.
                if (args.IsTerminating) Exit(args.ExceptionObject as Exception ?? new Exception("Non-Exception fixture fault"));
            };
        }

        internal static int Run(Func<int> body)
        {
            Register();
            try { return body(); }
            catch (Exception error) { Exit(error); return UnhandledExceptionExitCode; }
        }

        internal static async Task<int> RunAsync(Func<Task<int>> body)
        {
            Register();
            try { return await body().ConfigureAwait(false); }
            catch (Exception error) { Exit(error); return UnhandledExceptionExitCode; }
        }

        internal static string Format(Exception error)
        {
            while ((error is AggregateException || error is TargetInvocationException) && error.InnerException != null)
                error = error.InnerException;
            string message = error.Message ?? "";
            int end = message.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) message = message.Substring(0, end);
            if (message.Length > 500) message = message.Substring(0, 500);
            return Prefix + error.GetType().FullName + ": " + message;
        }

        private static void Exit(Exception error)
        {
            lock (gate)
            {
                try
                {
                    if (!reported) { reported = true; Console.Error.WriteLine(Format(error)); Console.Error.Flush(); }
                }
                catch (Exception) { /* A closed stderr must still exit without reaching the runtime crash handler. */ }
                finally { Environment.Exit(UnhandledExceptionExitCode); }
            }
        }
    }
}
