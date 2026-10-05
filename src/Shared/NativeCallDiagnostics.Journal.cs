using System;
using System.Text;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class NativeCallDiagnostics
    {
        private static void Write(Span span, string phase, Exception? error)
            => InvocationJournal.WriteRow(span.Correlation, "native:" + span.Member, phase, span.Type, span.Path,
                () => Details(span.Id, span.Parent?.Id, span.Site, span.Member, span.Category, span.ObjectId, error));

        internal static InvocationJournal.JsonLineObject Details(string id, string? parentId, string site, string member, string category, string objectId, Exception? error)
        {
            var exceptions = new StringBuilder("[");
            int count = 0;
            for (var current = error; current != null && count < 8; current = current.InnerException, count++)
            {
                if (count > 0) exceptions.Append(',');
                exceptions.Append(new InvocationJournal.JsonLineObject().String("type", current.GetType().FullName).Number("hresult", current.HResult));
            }
            return new InvocationJournal.JsonLineObject().String("nativeCallId", id).String("parentNativeCallId", parentId).String("callSite", site)
                .String("member", member).String("dispatch", category).String("objectId", objectId)
                .String("pathKind", "observed-access-lineage").String("exceptionType", error?.GetType().FullName)
                .Number("hresult", error == null ? (int?)null : error.HResult).Raw("exceptionChain", exceptions.Append(']').ToString());
        }
    }
}
