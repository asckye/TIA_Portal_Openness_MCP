namespace TiaMcp.BuildCommon;

public sealed class OfflineFixtures : IDisposable
{
    private readonly string parent;
    public string DirectoryPath { get; }

    public OfflineFixtures(string prefix = "offline-fixture-", string? root = null)
    {
        parent = Path.GetFullPath(Path.Combine(root ?? Repository.FindRoot(), "bin-build"));
        Directory.CreateDirectory(parent);
        DirectoryPath = Path.GetFullPath(Path.Combine(parent, prefix + Guid.NewGuid().ToString("N")));
        RequireOwnedPath();
        Directory.CreateDirectory(DirectoryPath);
    }

    private void RequireOwnedPath()
    {
        if (!string.Equals(Path.GetDirectoryName(DirectoryPath), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Unsafe offline fixture cleanup path");
    }

    public void Dispose()
    {
        RequireOwnedPath();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
