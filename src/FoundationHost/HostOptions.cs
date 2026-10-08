using TiaMcp.Versioning;

namespace TiaMcp.LegacyHost;

internal sealed class HostOptions
{
    public required string ReleaseKey { get; init; }
    public required string WorkerExe { get; init; }
    // False when --worker-exe replaces the bundled worker (test fixtures and development builds).
    public bool BundledWorker { get; init; } = true;
    public required string ApiDirectory { get; init; }
    public string? ApiDirectorySource { get; init; }
    public required string BundleRoot { get; init; }
    public bool NativeEnabled { get; init; }
    public bool CatalogOnly { get; init; }
    public string Transport { get; init; } = "stdio";
    public string HttpPrefix { get; init; } = "http://127.0.0.1:8735/";
    public string ApiKey { get; init; } = "";
    public string? EngineWorkerExe { get; init; }
    public string? EngineCatalog { get; init; }
    public int EngineTimeoutSeconds { get; init; } = 120;
    public string? WorkerTimeoutConfig { get; init; }
    public bool WithUi { get; init; }
    public string? TiaPortalLocation { get; init; }
    public string Profile { get; init; } = "lite";

    public static HostOptions Parse(string[] args, string directory)
    {
        string explicitRoot = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args, out args);
        string bundleRoot = TiaOpenness.Shared.BundleLayout.RequireRoot(directory, explicitRoot);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var valued = new[] { "--release-key", "--tia-major-version", "--worker-exe", "--public-api", "--tia-portal-location", "--transport", "--http-prefix", "--http-api-key", "--logging", "--engine-worker", "--engine-catalog", "--worker-timeout-seconds", "--worker-timeout-config", "--profile" };
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--native-session" or "--offline" or "--catalog" or "--with-ui") { flags.Add(args[i]); continue; }
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
        bool engine = version.WorkerKind == "engine";
        bool explicitWorker = options.ContainsKey("--engine-worker");
        if (explicitWorker != options.ContainsKey("--engine-catalog") || explicitWorker && !engine)
            throw new ArgumentException("The engine host requires release 20 or 21 and both --engine-worker and --engine-catalog.");
        int timeout = 120;
        if (options.ContainsKey("--worker-timeout-seconds") && (!int.TryParse(Value("--worker-timeout-seconds"), out timeout) || timeout <= 0))
            throw new ArgumentException("--worker-timeout-seconds must be positive.");
        string profile = Value("--profile", Environment.GetEnvironmentVariable("TIA_MCP_PROFILE") ?? "lite").Trim().ToLowerInvariant();
        profile = profile is "full" or "all" ? "full" : "lite";
        var transport = Value("--transport", "stdio");
        if (transport is not ("stdio" or "http")) throw new ArgumentException("Use stdio or http transport.");
        var prefix = Value("--http-prefix", "http://127.0.0.1:8735/");
        var key = Value("--http-api-key");
        if (transport == "http")
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/" || !System.Net.IPAddress.TryParse(uri.Host, out _) || uri.Query.Length != 0)
                throw new ArgumentException("HTTP prefix must be an explicit IP address and port at the root path.");
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ArgumentException("HTTP requires a nonempty --http-api-key.");
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
            BundleRoot = bundleRoot, ReleaseKey = release,
            EngineWorkerExe = engine ? Path.GetFullPath(Value("--engine-worker", TiaOpenness.Shared.BundleLayout.EngineWorkerPath(bundleRoot, release, directory))) : null,
            EngineCatalog = engine ? Path.GetFullPath(Value("--engine-catalog", Path.Combine(Path.GetDirectoryName(TiaOpenness.Shared.BundleLayout.EngineWorkerPath(bundleRoot, release, directory))!, "tool-catalog.json"))) : null,
            EngineTimeoutSeconds = timeout, WithUi = flags.Contains("--with-ui"),
            WorkerTimeoutConfig = options.ContainsKey("--worker-timeout-config") ? File.ReadAllText(Path.GetFullPath(Value("--worker-timeout-config"))) : null,
            TiaPortalLocation = options.ContainsKey("--tia-portal-location") ? Value("--tia-portal-location") : null, Profile = profile,
            WorkerExe = Value("--worker-exe", Path.Combine(bundleRoot, "runtime", version.RuntimeDirectory, "worker", "TiaMcp.PlcWorker." + release + ".exe")),
            BundledWorker = !options.ContainsKey("--worker-exe"),
            ApiDirectory = api, ApiDirectorySource = apiSource, NativeEnabled = !flags.Contains("--offline"), CatalogOnly = flags.Contains("--catalog"), Transport = transport, HttpPrefix = prefix, ApiKey = key
        };
    }
}
