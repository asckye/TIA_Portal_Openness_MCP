#nullable enable
#if STUDIO_VCI
using System;
using System.IO;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters.Native.Vci;

// Hash the exact bytes that were normalized. A failed XML comparison may still have a readable byte fingerprint.
internal sealed class VciContentProof
{
    internal FileInfo? File { get; }
    internal string? Sha256 { get; private set; }
    internal string Verification { get; private set; } = "not-comparable";
    internal string? Message { get; private set; }
    internal string? ExportFailure { get; private set; }
    internal bool Matches => Verification == "content-match";
    private readonly string workspace;

    internal VciContentProof(string workspace, FileInfo? file)
    {
        this.workspace = workspace;
        File = file;
    }

    private static byte[] Read(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException("The workspace or exported XML file is unavailable.", file.Name);
        CandidateImportFiles.SafePath(file);
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        return CandidatePrimitives.Read(stream);
    }

    internal string? CurrentHash()
    {
        try { return File == null ? null : CandidatePrimitives.ByteHash(Read(File)); }
        catch (Exception error) when (NativeRefusalProbe.AllowsRefusalProof(error))
        { Message = NativeFailureText.BoundedDiagnostic(error); return null; }
    }

    internal void Preview() => Sha256 = CurrentHash();

    internal void Compare(Func<Action<FileInfo, bool>> getExport, Func<Exception, bool> ordinary)
    {
        Verification = "not-comparable";
        Message = null;
        ExportFailure = null;
        Sha256 = null;
        if (File == null)
        {
            Message = "The workspace XML path is unavailable.";
            return;
        }
        try
        {
            byte[] bytes = Read(File);
            Sha256 = CandidatePrimitives.ByteHash(bytes);
            string expected = CandidateImportFiles.XmlHash(bytes);
            string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var temp = new DirectoryInfo(Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "vci-proof-" + Guid.NewGuid().ToString("N")));
            if (temp.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Comparison needs a folder outside the workspace.");
            CandidateImportFiles.SafePath(temp.Parent!);
            temp.Create();
            bool comparisonThrew = false;
            try
            {
                var export = getExport();
                foreach (bool defaults in new[] { false, true })
                {
                    var candidate = new FileInfo(Path.Combine(temp.FullName, defaults ? "defaults.xml" : "object.xml"));
                    try { export(candidate, defaults); }
                    catch (Exception error) when (ordinary(error))
                    {
                        Message = NativeFailureText.BoundedDiagnostic(error);
                        ExportFailure = NativeFailureText.BoundedMessage(NativeFailureText.Compose(error.Message,
                            error.GetBaseException().Message, Array.Empty<string>()), NativeFailureText.WithoutLocalPaths);
                        continue;
                    }
                    ExportFailure = null;
                    Message = null;
                    try
                    {
                        if (CandidateImportFiles.XmlHash(Read(candidate)) == expected)
                        {
                            Verification = "content-match";
                            return;
                        }
                        Verification = "no-match";
                    }
                    catch (Exception error) when (ordinary(error))
                    { Message = NativeFailureText.BoundedDiagnostic(error); }
                }
            }
            catch
            {
                comparisonThrew = true;
                throw;
            }
            finally
            {
                try { temp.Delete(true); }
                catch (Exception) when (comparisonThrew || Message != null)
                { /* swallow(cleanup): preserve the native comparison exception and its batch-stop semantics. */ }
            }
        }
        catch (Exception error) when (ordinary(error))
        {
            Verification = "not-comparable";
            Message = NativeFailureText.BoundedDiagnostic(error);
        }
    }
}
#endif
