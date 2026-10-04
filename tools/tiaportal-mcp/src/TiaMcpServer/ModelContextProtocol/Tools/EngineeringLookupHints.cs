using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class EngineeringLookupHints
    {
        internal static string BuildBlockDidYouMean(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = EngineServices.Get<Siemens.Portal>().GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                    blocks = EngineServices.Get<Siemens.Portal>().GetBlocks(softwarePath, escaped);

                var candidates = blocks
                    .Take(10)
                    .Select(b => EngineServices.Get<Siemens.Portal>().GetBlockPath(b))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch /* swallow(enumerate-optional): failure to enumerate block suggestions must preserve the original not-found error */
            {
                return string.Empty;
            }
        }

        internal static string BuildTypeDidYouMean(string softwarePath, string typePath)
        {
            if (string.IsNullOrEmpty(typePath) || typePath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(typePath);
                var types = EngineServices.Get<Siemens.Portal>().GetTypes(softwarePath, $"^{escaped}$");
                if (types == null || types.Count == 0)
                    types = EngineServices.Get<Siemens.Portal>().GetTypes(softwarePath, escaped);

                var candidates = types
                    .Take(10)
                    .Select(t => t.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch /* swallow(enumerate-optional): failure to enumerate type suggestions must preserve the original not-found error */
            {
                return string.Empty;
            }
        }
    }
}
