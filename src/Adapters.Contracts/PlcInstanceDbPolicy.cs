using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcInstanceDbPolicy
    {
        // CreatePlcInstanceDb arguments (API audit F18): number 0 produced "DB0", which compilation rejects.
        public static string? Refusal(bool autoNumber, int number)
            => number < 0 ? "Single DB name and nonnegative number required."
                : !autoNumber && number < 1 ? "number must be at least 1 when autoNumber=false (DB0 is invalid)." : null;

        // API audit F21: the V15.1 and V16 SDK documents CreateInstanceDB only "for Prodiag block"; ordinary FBs open from V17.
        public static string? SourceRefusal(string release, string programmingLanguage)
            => (release == "15.1" || release == "16") && !string.Equals(programmingLanguage, "ProDiag", StringComparison.Ordinal)
                ? "On TIA Portal V" + release + " PlcBlockComposition.CreateInstanceDB is documented for ProDiag function blocks only; this FB's language is " + programmingLanguage + ". Use V17 or later for ordinary FBs."
                : null;
    }
}
