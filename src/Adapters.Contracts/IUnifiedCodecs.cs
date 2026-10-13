using System;
using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts
{
    public interface IUnifiedCodecs
    {
        public Func<string, string, UnifiedScriptPair>? ReadUnifiedScriptPair { get; }
        public Action<string, string>? RequireUnifiedPlainFile { get; }
        public Action<string>? ValidateUnifiedScript { get; }
        public Func<string, string, string, IEnumerable<Dictionary<string, object?>>>? DecodeUnifiedDocument { get; }
        public Func<string, string, Dictionary<string, object?>>? AnalyzeUnifiedScript { get; }
    }
}
