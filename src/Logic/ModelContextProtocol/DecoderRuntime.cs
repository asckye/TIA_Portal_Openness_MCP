using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TiaMcp.Logic.ModelContextProtocol
{
    public sealed class DecoderRuntimeException : IOException
    {
        public DecoderRuntimeException() : base("Bundled decoder runtime is missing or invalid. Repair the installation.") { }
    }

    public static class DecoderRuntime
    {
        public const string RelativeDirectory = "runtime/decoder-python";
        public const string Version = "3.12.10";
        public const string InterpreterSha256 = "4d6f5f81a4bca11191c4c7c6b43632694d0a4ce74e068619d8fdc161d469859a";
        public const string CoreSha256 = "9a0e3435aaa680d868150f87ab3e388ad2eebc22f87e036155c7b4eda8cd2120";
        public static IReadOnlyList<string> BinaryFiles { get; } = Array.AsReadOnly(new[] {
            "python.exe", "python312.dll", "vcruntime140.dll", "_elementtree.pyd", "pyexpat.pyd" });
        // Recorded imports of FC, FB, unknown-instruction, V20 and XXE fixtures.
        // Frozen modules are included too so the archive has no machine-path dependency.
        public static IReadOnlyList<string> StandardLibraryFiles { get; } = Array.AsReadOnly(
            "__future__.py _collections_abc.py _weakrefset.py abc.py ast.py codecs.py collections/__init__.py collections/abc.py contextlib.py copy.py copyreg.py dataclasses.py dis.py encodings/__init__.py encodings/aliases.py encodings/utf_8.py encodings/utf_8_sig.py enum.py fnmatch.py functools.py genericpath.py importlib/__init__.py importlib/_bootstrap.py importlib/_bootstrap_external.py importlib/machinery.py inspect.py io.py ipaddress.py json/__init__.py json/decoder.py json/encoder.py json/scanner.py keyword.py linecache.py ntpath.py opcode.py operator.py os.py pathlib.py posixpath.py re/__init__.py re/_casefix.py re/_compiler.py re/_constants.py re/_parser.py reprlib.py stat.py token.py tokenize.py types.py urllib/__init__.py urllib/parse.py warnings.py weakref.py xml/__init__.py xml/etree/ElementPath.py xml/etree/ElementTree.py xml/etree/__init__.py zipimport.py".Split(' '));
        public static IReadOnlyList<string> RequiredFiles { get; } = Array.AsReadOnly(BinaryFiles.Concat(new[] {
            "stdlib.zip", "python._pth", "LICENSE.txt", "THIRD-PARTY-LICENSES.html" }).ToArray());

        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string Require(string bundleRoot)
        {
            try
            {
                string directory = Path.Combine(bundleRoot, RelativeDirectory);
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new DecoderRuntimeException();
                var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
                if (!names.SequenceEqual(RequiredFiles.Concat(new[] { "runtime.json" }).OrderBy(n => n, StringComparer.Ordinal)))
                    throw new DecoderRuntimeException();
                using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json"))))
                {
                    var receipt = document.RootElement;
                    if (receipt.GetProperty("version").GetString() != Version || receipt.GetProperty("tag").GetString() != "python312"
                        || receipt.GetProperty("architecture").GetString() != "x64" || receipt.GetProperty("license").GetString() != "LICENSE.txt"
                        || receipt.GetProperty("interpreterSha256").GetString() != InterpreterSha256)
                        throw new DecoderRuntimeException();
                    var files = receipt.GetProperty("files");
                    if (!files.EnumerateObject().Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)
                        .SequenceEqual(RequiredFiles.OrderBy(n => n, StringComparer.Ordinal))) throw new DecoderRuntimeException();
                    foreach (var name in RequiredFiles)
                    {
                        string path = Path.Combine(directory, name);
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || files.GetProperty(name).GetString() != Hash(path))
                            throw new DecoderRuntimeException();
                    }
                }
                if (Hash(Path.Combine(directory, "python.exe")) != InterpreterSha256 || Hash(Path.Combine(directory, "python312.dll")) != CoreSha256
                    || File.ReadAllText(Path.Combine(directory, "python._pth")) != ".\nstdlib.zip\n") throw new DecoderRuntimeException();
                using (var archive = ZipFile.OpenRead(Path.Combine(directory, "stdlib.zip")))
                    if (!archive.Entries.Select(e => e.FullName).SequenceEqual(StandardLibraryFiles)) throw new DecoderRuntimeException();
                return Path.Combine(directory, "python.exe");
            }
            catch (DecoderRuntimeException) { throw; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException
                || error is InvalidOperationException || error is KeyNotFoundException || error is ArgumentException)
            { throw new DecoderRuntimeException(); }
        }
    }
}
