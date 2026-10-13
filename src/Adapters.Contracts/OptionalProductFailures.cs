using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TiaMcp.Adapters.Contracts;

public static class OptionalProductFailures
{
    public static bool IsAssemblyLoadFailure(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        if (error.InnerException != null && IsAssemblyLoadFailure(error.InnerException)) return true;
        if (error is TypeLoadException typeLoad)
            return typeLoad.TypeName?.StartsWith("Siemens.Engineering.", StringComparison.Ordinal) == true;
        var fileName = error is FileNotFoundException notFound ? notFound.FileName
            : error is FileLoadException fileLoad ? fileLoad.FileName : null;
        if (fileName == null || fileName.Length == 0) return false;
        string name;
        if (fileName.IndexOf(',') >= 0)
        {
            // CLR load failures use a display identity; reject malformed names without swallowing parser exceptions.
            var identity = Regex.Match(fileName, @"\A(?<name>[^,]+),\s*Version=(?<version>[0-9]+(?:\.[0-9]+){1,3})(?:,\s*Culture=(?:neutral|[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*))?(?:,\s*PublicKeyToken=(?:null|[0-9a-fA-F]{16}))?\z", RegexOptions.CultureInvariant);
            if (!identity.Success || !Version.TryParse(identity.Groups["version"].Value, out _)) return false;
            name = identity.Groups["name"].Value;
        }
        else
        {
            name = Path.GetFileName(fileName);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
        }
        return name == "Siemens.Engineering" || name == "Siemens.Engineering.CFC" || name == "Siemens.Engineering.TestSuite"
            || name == "Siemens.Engineering.Testsuite" || name == "Siemens.Engineering.Sinumerik"
            || name == "Siemens.Engineering.Simotion" || name == "Siemens.Engineering.ScadaExporter"
            || name == "Siemens.MC.Simotion.Scripting";
    }
}
