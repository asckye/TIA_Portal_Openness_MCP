using System;
using System.Globalization;
using TiaMcp.Versioning;
using TiaMcp.WorkerChannel;

namespace TiaMcp.FoundationHost;

internal sealed class HostOptions
{
    internal TimeProvider SessionClock { get; set; } = TimeProvider.System;
    internal static readonly string WorkerTimeoutHelp =
        "All eight releases (V14 SP1-V21) share worker timeout budgets: "
        + string.Join(", ", WorkerTimeoutPolicy.Defaults.Select(budget =>
            (budget.Key == "firstAttach" ? "firstAttach per generation" : budget.Key) + " "
            + budget.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s")) + ". "
        + "--worker-timeout-seconds <seconds> sets the default. --worker-timeout-config <file> reads a JSON object of category names to seconds (0, 86400]. "
        + "TIA_MCP_WORKER_TIMEOUTS uses the same JSON and takes precedence over the file. Previews and reads use default; Disconnect keeps its bounded " +
            "shutdown wait. "
        + "All eight releases use the standalone PLC worker. --worker-exe selects a development worker. "
        + "Cancellation before dispatch returns CANCELLED; after dispatch the host waits for the real result.";
    public required string ReleaseKey { get; init; }
    public required string WorkerExe { get; init; }
    // False when --worker-exe replaces the bundled worker (test fixtures and development builds).
    public bool BundledWorker { get; init; } = true;
    public required string ApiDirectory { get; init; }
    public string? ApiDirectorySource { get; init; }
    // True when --public-api named the API directory (worker assemblies), not a TIA installation folder.
    public bool PublicApiOption { get; init; }
    public required string BundleRoot { get; init; }
    public bool NativeEnabled { get; init; }
    public bool CatalogOnly { get; init; }
    public string? CatalogToolsList { get; init; }
    public string CatalogPackage { get; init; } = "";
    public string? CatalogGeneratedAt { get; init; }
    public string Transport { get; init; } = "stdio";
    public string HttpPrefix { get; init; } = "http://127.0.0.1:8735/";
    public string ApiKey { get; init; } = "";
    public string[] HttpAllowedOrigins { get; init; } = [];
    public int EngineTimeoutSeconds { get; init; } = 120;
    public string? WorkerTimeoutConfig { get; init; }
    public bool WithUi { get; init; }
    public string? TiaPortalLocation { get; init; }
    public string Profile { get; init; } = "lite";
    public string? HostCommand { get; init; }
    public IReadOnlyDictionary<string, string> Presets { get; init; } = TiaMcp.Logic.V4.ParameterPresets.Empty;
    internal const string PresetHelp =
        "--preset-project-file <absolute existing .apN/.alsN file> pins the canonical filesystem project identity for all WRITE/ONLINE-WRITE dispatches " +
        "and binding targets (.alsN where local sessions exist). --preset-plc-software <softwarePath> pins PLC selectors named softwarePath, plc and " +
        "plcSoftwarePath, library instantiate targetSoftwarePath and ImportLibraryTypeDocuments.targetSoftwarePath, across all domains. " +
        "Mixed tools use objectKind=Block/Type, sourceKind=block/type, kind=plc*, action=connectTechnologyObject " +
        "or non-empty processImageObName, and library instantiate action/targetKind. InvokeObject/InvokeService refuse Software (including PLC aliases), " +
        "Device and DeviceItem while a PLC preset is set. HMI selectors, READ comparison operands, reviewed plural scopes " +
        "and nested references stay explicit. " +
        "See ParameterPresets.SoftwareParameters and ReviewedSoftwareParameters for exact coverage. " +
        "Omitted presets are injected; equal normalized values are accepted; different values are refused. Unconditional preset fields are hidden.";
    public int? OperatorOwner { get; init; }

    internal void ConfigureEngineeringResolver()
    {
        TiaMcpServer.Siemens.Engineering.TiaMajorVersion = TiaVersionCatalog.Get(ReleaseKey).MajorVersion;
        TiaMcpServer.Siemens.Engineering.TiaPortalLocationOverride = TiaPortalLocation;
    }

    internal static void RejectEngineOptions(string[] args, string directory)
    {
        if (args.Any(arg => arg.StartsWith("--engine-", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Engine worker options are retired. Use --release-key <key>; all releases use PlcWorker. Use --worker-exe <path> for " +
                "a development worker.");
    }

    public static HostOptions Parse(string[] args, string directory)
    {
        RejectEngineOptions(args, directory);
        string explicitRoot = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args, out args);
        string bundleRoot = TiaOpenness.Shared.BundleLayout.RequireRoot(directory, explicitRoot);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var valued = new[] {
            "--release-key", "--tia-major-version", "--worker-exe", "--public-api", "--tia-portal-location", "--transport", "--http-prefix", "--http-api-key",
                "--http-allowed-origins", "--logging", "--worker-timeout-seconds", "--worker-timeout-config", "--profile", "--host-command", "--operator-owner",
                "--catalog-tools-list", "--catalog-package", "--catalog-generated-at", "--preset-project-file", "--preset-plc-software"
        };
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--native-session" or "--offline" or "--catalog" or "--with-ui" or "--plc-worker") { flags.Add(args[i]); continue; }
            var name = args[i];
            if (!valued.Contains(name) || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown or incomplete option: " + name);
            if (!options.TryAdd(name, args[++i])) throw new ArgumentException("Repeated option: " + name);
        }
        string Value(string key, string fallback = "") => options.TryGetValue(key, out var value) ? value : fallback;
        string release = Value("--release-key", Value("--tia-major-version"));
        var marker = Path.Combine(directory, "release-key.txt");
        if (File.Exists(marker))
        {
            var packaged = File.ReadAllText(marker).Trim();
            if (release.Length == 0) release = packaged;
            else TiaVersionCatalog.RequireMatchingEngine(release, packaged);
        }
        if (options.ContainsKey("--release-key") && options.ContainsKey("--tia-major-version"))
            TiaVersionCatalog.RequireMatchingEngine(release, Value("--tia-major-version"));
        var version = TiaVersionCatalog.RequireRunnable(release);
        var presets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.TryGetValue("--preset-project-file", out var project))
        {
            if (!Path.IsPathFullyQualified(project) || !File.Exists(project))
                throw new ArgumentException("--preset-project-file requires an absolute existing project file for the selected release.");
            string canonicalProject = TiaMcp.Logic.V4.PresetProjectPath.Canonical(project);
            string extension = Path.GetExtension(canonicalProject);
            string major = release == "15.1" ? "15_1" : version.MajorVersion.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(extension, ".ap" + major, StringComparison.OrdinalIgnoreCase)
                    && !(version.MajorVersion >= 17 && string.Equals(extension, ".als" + major, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("--preset-project-file requires an absolute existing project file for the selected release.");
            presets.Add("expectedProjectFile", canonicalProject);
        }
        if (options.TryGetValue("--preset-plc-software", out var software))
        {
            if (string.IsNullOrWhiteSpace(software) || software != software.Trim())
                throw new ArgumentException("--preset-plc-software requires a non-empty softwarePath without surrounding whitespace.");
            presets.Add("softwarePath", software);
        }
        int timeout = 120;
        if (options.ContainsKey("--worker-timeout-seconds")
                && (!int
                    .TryParse(Value("--worker-timeout-seconds"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                        out timeout)
                    || timeout <= 0))
            throw new ArgumentException("--worker-timeout-seconds must be positive.");
        string profile = Value("--profile", Environment.GetEnvironmentVariable("TIA_MCP_PROFILE") ?? "lite").Trim().ToLowerInvariant();
        profile = profile is "full" or "all" ? "full" : "lite";
        var transport = Value("--transport", "stdio");
        if (transport is not ("stdio" or "http" or "operator")) throw new ArgumentException("Use stdio, http or operator transport.");
        int? operatorOwner = null;
        if (options.ContainsKey("--operator-owner"))
        {
            if (!int
                    .TryParse(Value("--operator-owner"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                        out int pid)
                    || pid <= 0 || transport == "stdio")
                throw new ArgumentException("--operator-owner requires a positive Workbench PID and http or operator transport.");
            operatorOwner = pid;
        }
        if (transport == "operator" && operatorOwner == null) throw new ArgumentException("Operator transport requires --operator-owner.");
        var prefix = Value("--http-prefix", "http://127.0.0.1:8735/");
        var key = Value("--http-api-key");
        var allowedOrigins = Value("--http-allowed-origins").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string allowed in allowedOrigins)
        {
            var origin = TiaOpenness.Shared.HttpAccessKeyStore.Endpoint(allowed);
            if (origin.AbsolutePath != "/") throw new ArgumentException("Allowed HTTP origins must be HTTP(S) authorities without a path.");
        }
        if (transport == "http")
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/" || !System.Net.IPAddress
                    .TryParse(uri.Host, out _) || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new ArgumentException("HTTP prefix must be an explicit IP address and port at the root path.");
            if (options.ContainsKey("--http-api-key"))
                Console.Error
                    .WriteLine("Deprecated: --http-api-key will be removed after this release. Save the HTTP access key in the Workbench protected " +
                    "configuration store.");
            else key = TiaOpenness.Shared.HttpAccessKeyStore
                .ReadHost(TiaOpenness.Shared.DataLocations.Current.ConfigDirectory, release, new Uri(new Uri(prefix), "mcp"));
            TiaOpenness.Shared.HttpAccessKeyStore.Validate(key);
        }
        var api = Value("--public-api");
        string? apiSource = string.IsNullOrWhiteSpace(api) ? null : "host-option";
        if (api.Length == 0)
        {
            string location;
            if (options.TryGetValue("--tia-portal-location", out var optionLocation))
            {
                location = optionLocation;
                if (!string.IsNullOrWhiteSpace(location)) apiSource = "host-option";
            }
            else
            {
                location = Environment.GetEnvironmentVariable("TiaPortalLocation") ?? "";
                if (!string.IsNullOrWhiteSpace(location)) apiSource = "environment";
            }
            api = version.FindApiDirectory(location) ?? "";
        }
        return new HostOptions {
            BundleRoot = bundleRoot, ReleaseKey = release, HttpAllowedOrigins = allowedOrigins,
            Presets = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(presets),
            OperatorOwner = operatorOwner,
            HostCommand = options.ContainsKey("--host-command") ? Value("--host-command") : null,
            EngineTimeoutSeconds = timeout, WithUi = flags.Contains("--with-ui"),
            WorkerTimeoutConfig = options.ContainsKey("--worker-timeout-config") ? File.ReadAllText(Path.GetFullPath(Value("--worker-timeout-config"))) : null,
            TiaPortalLocation = options.ContainsKey("--tia-portal-location") ? Value("--tia-portal-location") : null, Profile = profile,
            // The same bundled worker path for all eight releases (runtime/vK/worker), as for V14 SP1-V19 before.
            WorkerExe = Value("--worker-exe", TiaOpenness.Shared.BundleLayout.WorkerPath(bundleRoot, release)),
            BundledWorker = !options.ContainsKey("--worker-exe"),
            ApiDirectory = api, ApiDirectorySource = apiSource, PublicApiOption = !string.IsNullOrWhiteSpace(Value("--public-api")), NativeEnabled = !flags
                .Contains("--offline"), CatalogOnly = flags.Contains("--catalog"), Transport = transport, HttpPrefix = prefix, ApiKey = key,
            CatalogToolsList = options.GetValueOrDefault("--catalog-tools-list"), CatalogPackage = Value("--catalog-package"), CatalogGeneratedAt = options
                .GetValueOrDefault("--catalog-generated-at")
        };
    }
}
