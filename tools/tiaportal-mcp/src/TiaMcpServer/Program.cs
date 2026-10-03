using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Xml;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using McpProtocol = global::ModelContextProtocol.Protocol;

namespace TiaMcpServer
{
    public partial class Program
    {
        private static readonly string DiagLogPath = Path.Combine(Path.GetTempPath(), "TiaMcpServer.log");
        private static readonly string DiagLogPathLocal = Path.Combine(AppContext.BaseDirectory, "TiaMcpServer.startup.log");
        private delegate void StructuredTextLine(StringBuilder st, params string[] parts);

        private static void ConfigureResourceDiscovery(IMcpServerBuilder builder)
        {
            // Engineering data is exposed through tools. The MCP resource catalog
            // is empty; register both list handlers so discovery succeeds on either
            // transport. The SDK advertises resources without subscriptions or
            // list-change notifications when these handlers are registered.
            builder.WithListResourcesHandler((request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<McpProtocol.ListResourcesResult>(new McpProtocol.ListResourcesResult
                {
                    Resources = Array.Empty<McpProtocol.Resource>()
                });
            });
            builder.WithListResourceTemplatesHandler((request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<McpProtocol.ListResourceTemplatesResult>(new McpProtocol.ListResourceTemplatesResult
                {
                    ResourceTemplates = Array.Empty<McpProtocol.ResourceTemplate>()
                });
            });
        }

        public static async Task Main(string[] args)
        {
            // Reject obsolete or misspelled verbs before any TIA initialization.
            // Only no arguments or option-led arguments select the MCP server mode.
            if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal)
                && !Cli.CliCommands.IsVerb(args[0]))
            {
                Console.Error.WriteLine("Unknown command. Run tia help for supported commands.");
                Environment.ExitCode = 2;
                return;
            }
            try
            {
                // Force stdin/stdout to UTF-8 (no BOM). Without this, on zh-CN Windows the
                // default Console encoding is GBK (CP936), which mangles Chinese project
                // names, comments, HMI labels, and any non-ASCII characters in JSON-RPC.
                // Child stdin selects its own UTF-8 encoding independently of this console setup.
                try
                {
                    var utf8NoBom = new UTF8Encoding(false);
                    Console.InputEncoding  = utf8NoBom;
                    Console.OutputEncoding = utf8NoBom;
                }
                catch (Exception encEx)
                {
                    LogDiag("WARN: failed to set Console UTF-8 encoding: " + encEx.Message);
                }

                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromBaseDir;
                // With legacyUnhandledExceptionPolicy (App.config), background-thread exceptions must be recorded here.
                // Do not first call ToString()/Message on the exception: after a TIA Portal exit, Openness exceptions have
                // been observed to fail inside ToString(), leaving only "Exception.ToString() failed" in the crash report.
                AppDomain.CurrentDomain.UnhandledException += (_, e) => LogDiag("UNHANDLED (" + (e.IsTerminating ? "terminating" : "non-terminating, process kept alive") + "): " + DescribeSafely(e.ExceptionObject));
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { LogDiag("UNOBSERVED TASK: " + DescribeSafely(e.Exception)); e.SetObserved(); };

                LogDiag($"=== {DateTime.Now:O} PID={System.Diagnostics.Process.GetCurrentProcess().Id} ===");
                LogDiag($"BaseDir: {AppContext.BaseDirectory}");
                LogDiag($"Exe: {Assembly.GetExecutingAssembly().Location}");
                LogDiag($"Args: {string.Join(" ", args.Select((arg, i) => i > 0 && string.Equals(args[i - 1], "--http-api-key", StringComparison.OrdinalIgnoreCase) ? "[redacted]" : arg))}");

                if (CliOptions.IsInformationalCommand(args))
                {
                    Environment.ExitCode = Cli.CliCommands.Run(args);
                    return;
                }

                var options = CliOptions.ParseArgs(args);

                // Default logging to stderr (mode 1) when the user doesn't pass --logging,
                // so errors are visible out of the box. Users can opt out with --logging 0
                // (treated as "no logging" by the switch statements below).
                if (options.Logging == null)
                {
                    options.Logging = 1;
                    LogDiag("Logging defaulted to stderr (--logging 1). Pass --logging 0 to silence, 2 for Debug output, 3 for EventLog.");
                }

                // Wire CLI --tia-portal-location into the assembly resolver. Must happen BEFORE
                // DetectTiaMajorVersion so the override participates in version detection.
                if (!string.IsNullOrWhiteSpace(options.TiaPortalLocation))
                {
                    Engineering.TiaPortalLocationOverride = options.TiaPortalLocation;
                    LogDiag($"TIA Portal location (from CLI): {options.TiaPortalLocation}");
                }

                int tiaMajorVersion;
                bool tiaVersionReliable; // explicit CLI arg or a positive registry detection (not the blind fallback)
                if (options.TiaMajorVersion.HasValue)
                {
                    tiaMajorVersion = options.TiaMajorVersion.Value;
                    tiaVersionReliable = true;
                    LogDiag($"TIA major version (from CLI): {tiaMajorVersion}");
                }
                else
                {
                    var detected = Engineering.DetectTiaMajorVersion();
                    tiaMajorVersion = detected ?? EngineRouter.CompiledTiaMajorVersion;
                    tiaVersionReliable = detected.HasValue;
                    LogDiag(detected.HasValue
                        ? $"TIA major version (auto-detected): {tiaMajorVersion}"
                        : $"TIA major version (default fallback): {tiaMajorVersion} — install not detected, specify --tia-major-version if wrong");
                }
                Engineering.TiaMajorVersion = tiaMajorVersion;
                // Diagnostics must remain reachable even when an old installation is detected.
                // No native engine is started; doctor reports unsupported versions as failures.
                if (args.Length > 0 && string.Equals(args[0], "doctor", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = Cli.CliCommands.Run(args);
                    return;
                }
                TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(tiaMajorVersion);

                // Version-aware self-routing: this exe's IL is bound to one TIA major
                // version; when the machine actually wants a different one, re-exec the sibling
                // exe built for it instead of crashing at the first Siemens assembly load.
                // stdio is inherited, so MCP hosts and CLI callers are unaffected.
                if (tiaVersionReliable && tiaMajorVersion != EngineRouter.CompiledTiaMajorVersion)
                {
                    if (EngineRouter.TryRedirect(tiaMajorVersion, args, LogDiag, out int routedExit))
                    {
                        Environment.Exit(routedExit);
                        return;
                    }
                    LogDiag($"ERROR: No usable V{tiaMajorVersion} sibling engine was found, or redirect was blocked.");
                }

                TiaMcp.Versioning.TiaVersionCatalog.RequireMatchingEngine(tiaMajorVersion, EngineRouter.CompiledTiaMajorVersion);

                // 静态自检也会枚举 MCP 工具特性，方法签名里引用的 Siemens 程序集需要先能被解析。
                // 这里只注册程序集解析器，不初始化 Openness，也不连接或打开 TIA 项目。
                AppDomain.CurrentDomain.AssemblyResolve += Engineering.Resolver;

                // Headless by default for fast startup; --with-ui launches the full GUI for inspection.
                // Flag lives on Engineering (Siemens-free) so setting it here doesn't force the CLR to
                // load the Portal type (its Siemens.Engineering fields) at Main's JIT time.
                // Tool roster size. --profile wins over TIA_MCP_PROFILE; default is lite.
                // Must be applied before the MCP host is built, since it decides tools/list.
                ModelContextProtocol.McpServer.SetProfileOverride(options.Profile);

                Engineering.LaunchWithUserInterface = options.PortalWithUserInterface;
                LogDiag(options.PortalWithUserInterface
                    ? "TIA Portal will launch WITH user interface (--with-ui); slower cold start."
                    : "TIA Portal will launch headless (WithoutUserInterface) for faster startup; pass --with-ui to show the GUI.");

                // Dispatch the verbs registered by CliCommands.
                // Engine config (assembly resolver, version, headless) is already applied above, so verb
                // handlers can connect immediately. Falls through to MCP host when args[0] isn't a verb.
                if (args.Length > 0 && Cli.CliCommands.IsVerb(args[0]))
                {
                    EngineServices.InitializeStandalone();
                    Environment.Exit(Cli.CliCommands.Run(args));
                    return;
                }

                if (options.AnalyzeReferenceAssets)
                {
                    RunAnalyzeReferenceAssets(options);
                    return;
                }

                if (options.AnalyzeGlobalLibraryPackage)
                {
                    RunAnalyzeGlobalLibraryPackage(options);
                    return;
                }

                if (options.AnalyzeHmiTemplateReference)
                {
                    RunAnalyzeHmiTemplateReference(options);
                    return;
                }

                if (options.AnalyzeHmiComponentCatalog)
                {
                    RunAnalyzeHmiComponentCatalog(options);
                    return;
                }

                if (options.RunHmiActionScriptRecipeProbe)
                {
                    RunHmiActionScriptRecipeProbe(options);
                    return;
                }

                if (options.RunHmiActionScriptRecipeSafetySelfTest)
                {
                    RunHmiActionScriptRecipeSafetySelfTest();
                    return;
                }

                if (options.RunHmiTemplateLayoutProbe)
                {
                    RunHmiTemplateLayoutProbe(options);
                    return;
                }

                if (options.RunClassicHmiMinimalPackageProbe)
                {
                    RunClassicHmiMinimalPackageProbe(options);
                    return;
                }

                if (options.RunClassicHmiOfflineSuite)
                {
                    RunClassicHmiOfflineSuite(options);
                    return;
                }

                if (options.RunClassicHmiTemporaryImportPreflight)
                {
                    RunClassicHmiTemporaryImportPreflight(options);
                    return;
                }

                if (options.RunPlcSymbolManifestProbe)
                {
                    RunPlcSymbolManifestProbe(options);
                    return;
                }

                if (options.RunOfflineReleaseSuite)
                {
                    RunOfflineReleaseSuite(options);
                    return;
                }

                if (options.RebuildReleaseHandoff)
                {
                    RunRebuildReleaseHandoff(options);
                    return;
                }

                if (options.RunHmiTemplatePlcSyncPrecheckSuite)
                {
                    RunHmiTemplatePlcSyncPrecheckSuite(options);
                    return;
                }

                if (options.AnalyzeHmiTemplatePlcMapping)
                {
                    RunAnalyzeHmiTemplatePlcMapping(options);
                    return;
                }

                if (options.GenerateHmiTemplateMappingSkeleton)
                {
                    RunGenerateHmiTemplateMappingSkeleton(options);
                    return;
                }

                if (options.GenerateHmiTemplateSyncPrecheck &&
                    string.IsNullOrWhiteSpace(options.PlcTagTableRegex) &&
                    Math.Max(0, options.MaxPlcTagTablesToExport ?? 0) == 0)
                {
                    RunGenerateHmiTemplateSyncPrecheck(options);
                    return;
                }

                if (options.GeneratePlcBuilderFixtureReadiness)
                {
                    RunGeneratePlcBuilderFixtureReadiness(options);
                    return;
                }

                if (options.RunPlcBuilderOfflineSuite)
                {
                    RunPlcBuilderOfflineSuite(options);
                    return;
                }

                if (options.RunPlcTagTableBuilderProbe)
                {
                    RunPlcTagTableBuilderProbe(options);
                    return;
                }

                if (options.RunPlcUdtBuilderProbe)
                {
                    RunPlcUdtBuilderProbe(options);
                    return;
                }

                if (options.RunStructuredTextBuilderProbe)
                {
                    RunStructuredTextBuilderProbe(options);
                    return;
                }

                if (options.RunPlcFcBlockComposerProbe)
                {
                    RunPlcFcBlockComposerProbe(options);
                    return;
                }

                if (options.RunPlcGlobalDbBuilderProbe)
                {
                    RunPlcGlobalDbBuilderProbe(options);
                    return;
                }

                if (options.RunFlgNetCallBuilderProbe)
                {
                    RunFlgNetCallBuilderProbe(options);
                    return;
                }

                if (options.ValidateMappedHmiTemplateBindings &&
                    options.MappedHmiTemplateOfflineOnly)
                {
                    RunValidateMappedHmiTemplateBindings(options);
                    return;
                }

                if (options.RunOnlineMonitoringSafetySelfTest)
                {
                    RunOnlineMonitoringSafetySelfTest();
                    return;
                }

                if (options.IsolateOpenness)
                {
                    // Parent owns protocol/diagnostics only; Openness initialization happens in the child.
                    Isolation.IsolatedWorkerHost.Configure(options);
                    try
                    {
                        if (string.Equals(options.Transport, "http", StringComparison.OrdinalIgnoreCase)) await RunHttpHost(options);
                        else await RunStdioHost(options);
                    }
                    finally { Isolation.IsolatedWorkerHost.Stop(); }
                    return;
                }
                if (options.OpennessWorkerChild) Isolation.IsolatedWorkerHost.BeginChild(options);

                if (Engineering.TiaMajorVersion >= 20)
                {
                    try
                    {
                        LogDiag($"Initializing TIA Openness API for V{Engineering.TiaMajorVersion}");
                        Openness.Initialize(Engineering.TiaMajorVersion);
                        LogDiag("TIA Openness API initialized");
                    }
                    catch (FileNotFoundException ex)
                    {
                        LogDiag("Openness.Initialize failed: FileNotFoundException");
                        LogDiag($"FIX: TIA Portal V{Engineering.TiaMajorVersion} (with the Openness option) was not found on this machine. Install it, or pass --tia-major-version <n> matching the installed version, or set the TiaPortalLocation environment variable to the install path. Run `tia.cmd doctor` for a full check.");
                        LogDiag($"修复：本机未找到 TIA Portal V{Engineering.TiaMajorVersion}（含 Openness 组件）。请安装对应版本，或用 --tia-major-version 指定已装版本，或设置 TiaPortalLocation 环境变量指向安装目录。可运行 tia.cmd doctor 一键体检。");
                        LogDiag($"FileName: {ex.FileName}");
                        if (!string.IsNullOrWhiteSpace(ex.FusionLog))
                        {
                            LogDiag("FusionLog:");
                            LogDiag(ex.FusionLog);
                        }
                        throw;
                    }
                    catch (BadImageFormatException ex)
                    {
                        LogDiag("Openness.Initialize failed: BadImageFormatException (x86/x64 mismatch or corrupt dll)");
                        LogDiag(ex.ToString());
                        throw;
                    }
                }

                // Check only; permission repair is an explicit doctor --fix or tool action.
                LogDiag("Checking Windows group membership: Siemens TIA Openness");
                var opennessUserOk = Openness.IsUserInGroupNoFix();
                LogDiag($"Siemens TIA Openness group membership: {opennessUserOk}");
                if (opennessUserOk)
                {
                    if (options.RunFlowLightTest)
                    {
                        RunFlowLightTest(options);
                        return;
                    }

                    if (options.FixCurrentFlowBinding)
                    {
                        RunFixCurrentFlowBinding(options);
                        return;
                    }

                    if (options.ProbeS71200Device)
                    {
                        RunProbeS71200Device(options);
                        return;
                    }

                    if (options.Add1511CToCurrentProject)
                    {
                        RunAdd1511CToCurrentProject(options);
                        return;
                    }

                    if (options.ValidatePlcSclSyntax)
                    {
                        RunValidatePlcSclSyntax(options);
                        return;
                    }

                    if (options.RunMotorMinimalTest)
                    {
                        RunMotorMinimalTest(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiTemplates)
                    {
                        RunValidateUnifiedHmiTemplates(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiActionSyntaxCheck)
                    {
                        RunValidateUnifiedHmiActionSyntaxCheck(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiTemplateBindings)
                    {
                        RunValidateUnifiedHmiTemplateBindings(options);
                        return;
                    }

                    if (options.ValidateMappedHmiTemplateBindings)
                    {
                        RunValidateMappedHmiTemplateBindings(options);
                        return;
                    }

                    if (options.ValidatePlcHmiSyncMinimal)
                    {
                        RunValidatePlcHmiSyncMinimal(options);
                        return;
                    }

                    if (options.ValidatePlcChineseCommentsMinimal)
                    {
                        RunValidatePlcChineseCommentsMinimal(options);
                        return;
                    }

                    if (options.ProbeKtp700Basic)
                    {
                        RunProbeKtp700Basic(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiImport)
                    {
                        RunProbeKtp700BasicHmiImport(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiTags)
                    {
                        RunProbeKtp700BasicHmiTags(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiConnection)
                    {
                        RunProbeKtp700BasicHmiConnection(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiSymbolicTags)
                    {
                        RunProbeKtp700BasicHmiSymbolicTags(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicNetworking)
                    {
                        RunProbeKtp700BasicNetworking(options);
                        return;
                    }

                    if (options.ProbeCurrentKtp700HardwareHmiConnection)
                    {
                        RunProbeCurrentKtp700HardwareHmiConnection(options);
                        return;
                    }

                    if (options.ListPortalProcessProjects)
                    {
                        RunListPortalProcessProjects(options);
                        return;
                    }

                    if (options.RunCapabilitySelfTest)
                    {
                        await RunCapabilitySelfTest(options);
                        return;
                    }

                    if (options.GenerateAcceptanceReport)
                    {
                        await RunGenerateAcceptanceReport(options);
                        return;
                    }

                    if (options.GenerateErrorReport)
                    {
                        RunGenerateErrorReport(options);
                        return;
                    }

                    if (options.GenerateMonitoringReadOnlyReport)
                    {
                        RunGenerateMonitoringReadOnlyReport(options);
                        return;
                    }

                    if (options.GenerateGlobalLibraryProbeReport)
                    {
                        RunGenerateGlobalLibraryProbeReport(options);
                        return;
                    }

                    if (options.ValidateGlobalLibraryMasterCopyImport)
                    {
                        RunValidateGlobalLibraryMasterCopyImport(options);
                        return;
                    }

                    if (options.GenerateHmiTemplateSyncPrecheck)
                    {
                        RunGenerateHmiTemplateSyncPrecheck(options);
                        return;
                    }

                    if (options.ProbeHardwareHmiConnectionOwnerCandidates)
                    {
                        RunProbeHardwareHmiConnectionOwnerCandidates(options);
                        return;
                    }

                    if (options.ProbeHardwareHmiConnectionWhitelistedServices)
                    {
                        RunProbeHardwareHmiConnectionWhitelistedServices(options);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(options.SearchGsdKeyword))
                    {
                        RunSearchGsd(options);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(options.SearchHardwareCatalogKeyword))
                    {
                        RunSearchHardwareCatalog(options);
                        return;
                    }

                    if (string.Equals(options.Transport, "http", StringComparison.OrdinalIgnoreCase))
                    {
                        await RunHttpHost(options);
                    }
                    else
                    {
                        await RunStdioHost(options);
                    }
                }
                else
                {
                    LogDiag("User is not in the required group 'Siemens TIA Openness'. Exiting.");
                    LogDiag("FIX: run this exe with `doctor` (e.g. tia.cmd doctor --fix) or add your Windows user to the local group 'Siemens TIA Openness' (lusrmgr.msc), then sign out/in and restart the AI client.");
                    LogDiag("修复：运行 tia.cmd doctor --fix，或手动把当前 Windows 用户加入本地组 'Siemens TIA Openness'（lusrmgr.msc），注销重登后重启 AI 客户端。");
                    Environment.ExitCode = 2;
                }
            }
            catch (Exception ex)
            {
                LogDiag("FATAL:");
                LogExceptionSafe(ex);
                if (ex is ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
                {
                    foreach (var le in rtle.LoaderExceptions)
                    {
                        if (le == null) continue;
                        LogDiag("LoaderException:");
                        LogExceptionSafe(le);
                    }
                }
                // Re-throw so host surfaces failure, but we still have the log on disk.
                throw;
            }
        }

        public static async Task RunStdioHost(CliOptions? options)
        {
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            if (builder != null)
            {
                if (options != null && options.Logging != null)
                {
                    switch (options.Logging)
                    {
                        case 1:
                            // ATTENTION: For STDIO, logs must go to stderr!
                            builder.Logging.AddConsole(options =>
                            {
                                options.LogToStandardErrorThreshold = LogLevel.Trace;
                            });
                            break;

                        case 2:
                            // Visual Studio Debug Output / Sysinternals.DebugView
                            builder.Logging.AddDebug();
                            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
                            builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
                            builder.Logging.AddFilter("TiaMcpServer", LogLevel.Debug);

                            // Log Level for Debug Output
                            builder.Logging.SetMinimumLevel(LogLevel.Debug);
                            break;

                        case 3:
                            // Windows Event Log
                            builder.Logging.AddEventLog();
                            break;

                        default:
                            // no logging
                            break;
                    }
                }

                try
                {
                    var mcp = builder.Services
                        .AddMcpServer(o =>
                        {
                            // Injected into the model's context by the host at initialize time —
                            // reaches EVERY MCP client, including ones that never load SKILL.md.
                            o.ServerInstructions = ModelContextProtocol.McpGuides.ServerInstructions;
                        })
                        .WithStdioServerTransport();
                    // TIA_MCP_PROFILE=lite → only [L0]/[L1] essentials (weak models / capped hosts).
                    //
                    // 两个分支都必须先取得工具列表再走 WrapTools，保证每个工具都有参数诊断和大响应分页。
                    // 诊断必须在 SDK 参数绑定前执行；超阈值的响应必须能通过寄存句柄读取剩余内容。
                    mcp.WithTools(ModelContextProtocol.McpServer.WrapTools(
                        ModelContextProtocol.McpServer.IsLiteProfile()
                            ? ModelContextProtocol.McpServer.GetLiteTools()
                            : ModelContextProtocol.McpServer.GetAllTools()));
                    ModelContextProtocol.McpPromptRegistration.Configure(mcp);
                    ConfigureResourceDiscovery(mcp);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    LogDiag("MCP registration failed: ReflectionTypeLoadException");
                    LogDiag(ex.ToString());
                    if (ex.LoaderExceptions != null)
                    {
                        foreach (var le in ex.LoaderExceptions)
                        {
                            if (le == null) continue;
                            LogDiag("LoaderException:");
                            LogDiag(le.ToString());
                        }
                    }
                    throw;
                }

                // The isolated parent owns only protocol services; the child uses this same host with a session.
                builder.Services.AddEngine(includeSession: Isolation.IsolatedWorkerHost.Current == null);

                var host = builder.Build();

                // Set the service provider for the MCP server, to retrieve Portal with injected logger
                McpServer.SetServiceProvider(host.Services);

                // Set the logger for the MCP server
                McpServer.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");
                var swallowedLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TiaMcpServer.Swallowed");
                // TIA_MCP_LOG_SWALLOWED=1 writes to stderr like the bridge and the workers; otherwise only Debug logging shows it.
                TiaMcp.Shared.SwallowedExceptions.Sink = Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1"
                    ? message => Console.Error.WriteLine(message)
                    : message => swallowedLogger.LogDebug("{SwallowedException}", message);

                // log a bit of information about the server start
                if (options != null && options.Logging != null && options.Logging > 0)
                {
                    var logger = host.Services.GetRequiredService<ILogger<Program>>();

                    logger.LogInformation($"=== TIA Portal MCP Server '{DateTime.Now.ToShortTimeString()}' ===");

                    switch (options.Logging)
                    {
                        case 1:
                            logger.LogInformation("Logging to stderr");
                            break;
                        case 2:
                            logger.LogInformation("Logging to debug output");
                            break;
                        case 3:
                            logger.LogInformation("Logging to Windows event log");
                            break;
                    }
                }

                await host.RunAsync();
            }

        }

        public static async Task RunHttpHost(CliOptions? options)
        {
            McpHostReadiness.Set("Initializing");
            // Two blocking streams form the bidirectional channel between HTTP and the MCP server.
            var httpToMcp = new McpBlockingStream();
            var mcpToHttp = new McpBlockingStream();
            using var transportLifetime = new CancellationTokenSource();

            var mcpTask = Task.Run(async () =>
            {
                try
                {
                    var builder = Host.CreateEmptyApplicationBuilder(settings: null);

                    if (options?.Logging != null)
                    {
                        switch (options.Logging)
                        {
                            case 1:
                                builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                                break;
                            case 2:
                                builder.Logging.AddDebug();
                                builder.Logging.SetMinimumLevel(LogLevel.Debug);
                                break;
                            case 3:
                                builder.Logging.AddEventLog();
                                break;
                        }
                    }

                    var mcpHttp = builder.Services
                        .AddMcpServer(o =>
                        {
                            o.ServerInstructions = ModelContextProtocol.McpGuides.ServerInstructions;
                        })
                        .WithStreamServerTransport(httpToMcp, mcpToHttp);
                    // 同 stdio 那处：两个分支都走 WrapTools，别让 HTTP 这条路少一层能力。
                    mcpHttp.WithTools(ModelContextProtocol.McpServer.WrapTools(
                        ModelContextProtocol.McpServer.IsLiteProfile()
                            ? ModelContextProtocol.McpServer.GetLiteTools()
                            : ModelContextProtocol.McpServer.GetAllTools()));
                    ModelContextProtocol.McpPromptRegistration.Configure(mcpHttp);
                    ConfigureResourceDiscovery(mcpHttp);

                    builder.Services.AddEngine(includeSession: Isolation.IsolatedWorkerHost.Current == null);

                    using var host = builder.Build();
                    McpServer.SetServiceProvider(host.Services);
                    McpServer.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");
                    var swallowedLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TiaMcpServer.Swallowed");
                    // TIA_MCP_LOG_SWALLOWED=1 writes to stderr like the bridge and the workers; otherwise only Debug logging shows it.
                    TiaMcp.Shared.SwallowedExceptions.Sink = Environment.GetEnvironmentVariable("TIA_MCP_LOG_SWALLOWED") == "1"
                        ? message => Console.Error.WriteLine(message)
                        : message => swallowedLogger.LogDebug("{SwallowedException}", message);
                    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => McpHostReadiness.Set("Ready"));
                    await host.RunAsync(transportLifetime.Token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && transportLifetime.IsCancellationRequested))
                {
                    var error = ex.Message;
                    if (!string.IsNullOrEmpty(options?.HttpApiKey)) error = error.Replace(options!.HttpApiKey!, "[REDACTED]");
                    McpHostReadiness.Set("Failed", error);
                    throw;
                }
                finally
                {
                    if (McpHostReadiness.Snapshot()["phase"]?.ToString() != "Failed") McpHostReadiness.Set("Stopped");
                    mcpToHttp.CompleteWriting();
                    transportLifetime.Cancel();
                }
            });

            try
            {
                await HttpMcpServer.Run(options, httpToMcp, mcpToHttp, LogDiag, transportLifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                transportLifetime.Cancel();
                httpToMcp.CompleteWriting();
                mcpToHttp.CompleteWriting();
                await mcpTask.ConfigureAwait(false);
            }
        }

        private static Assembly? ResolveFromBaseDir(object? sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var name = requested.Name ?? string.Empty;
                if (!name.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                // Engineering assemblies must come from the selected installation,
                // with identity checks in Engineering.Resolver (Copy Local=False).
                if (name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)) return null;

                var candidate = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch /* swallow(env-probe): failure of the local Siemens dependency probe leaves resolution to the assembly loader */
            {
                return null;
            }
        }

        private static string DescribeSafely(object? exception)
        {
            if (exception == null) return "<null>";
            var text = exception.GetType().FullName ?? "<unknown type>";
            try { if (exception is Exception ex) text += ": " + ex.Message; } catch /* swallow(logging-failure): an exception message getter must not prevent reporting the original exception type */ { text += " (Message unavailable)"; }
            try { if (exception is Exception ex && ex.InnerException != null) text += " <- " + (ex.InnerException.GetType().FullName ?? ""); } catch /* swallow(logging-failure): unavailable inner-exception details must not prevent reporting the outer exception */ { }
            return text;
        }
        private static void LogDiag(string message)
        {
            // Console may be swallowed by host; always persist to %TEMP%.
            try { Console.Error.WriteLine(message); } catch /* swallow(logging-failure): a closed stderr stream must not interrupt diagnostic file writes */ { }
            try { File.AppendAllText(DiagLogPath, message + Environment.NewLine); } catch /* swallow(logging-failure): failure to append the temporary diagnostic log must not interrupt the local log attempt */ { }
            try { File.AppendAllText(DiagLogPathLocal, message + Environment.NewLine); } catch /* swallow(logging-failure): failure to append the local diagnostic log must not escape the logging helper */ { }
        }

        private static void LogExceptionSafe(Exception ex)
        {
            try
            {
                LogDiag(ex.GetType().FullName + ": " + (ex.Message ?? ""));
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    LogDiag(ex.StackTrace);
                }
                if (ex.InnerException != null)
                {
                    LogDiag("InnerException:");
                    LogExceptionSafe(ex.InnerException);
                }
            }
            catch
            {
                try { LogDiag("Exception logging failed; original exception type: " + ex.GetType().FullName); } catch /* swallow(logging-failure): the fallback exception logger must not recursively report its own failure */ { }
            }
        }
    }
}
