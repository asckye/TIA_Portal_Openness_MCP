using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public static class PlcImportFiles
    {
        private static T Observe<T>(Func<T> read)
        { try { return read(); } catch (CandidateObservationException ex) { throw CandidateHostMapping.Import(ex.Fault); } }
        public static IReadOnlyList<PlcImportInput> Read(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
            => Observe(() => CandidateImportFiles.Read(release, tool, request, locks));
        public static void SafePath(FileSystemInfo entry) => Observe(() => { CandidateImportFiles.SafePath(entry); return true; });
        public static XDocument Xml(byte[] bytes) => Observe(() => CandidateImportFiles.Xml(bytes));
        public static string XmlHash(byte[] bytes) => Observe(() => CandidateImportFiles.XmlHash(bytes));
        public static string DocumentHash(byte[] code, byte[]? resource) => Observe(() => CandidateImportFiles.DocumentHash(code, resource));
    }
}
