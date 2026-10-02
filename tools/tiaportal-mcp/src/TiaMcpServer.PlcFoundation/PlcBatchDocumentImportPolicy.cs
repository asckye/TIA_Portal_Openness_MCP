using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcDocumentImportContext
    {
        internal PlcDocumentImportRequest Request=null!;
        internal Func<string[]> Scan=null!;
        internal Func<string,Stream> Open=null!;
        internal Func<IEnumerable<string>> Inventory=null!;
        internal Func<string,bool> Collision=null!;
        internal Action Recheck=null!;
        internal Func<PlcDocumentImportNative> Import=null!;
        internal PlcDocumentImportResult Run()=>PlcDocumentImportPolicy.Run(Request,Scan,Open,Inventory,Collision,Recheck,Import);
    }
    public sealed class PlcBatchDocumentImportItem
    {
        public string Name {get;internal set;}="";
        public string Status {get;internal set;}="not-attempted";
        public PlcDocumentImportResult Preview {get;internal set;}=null!;
        public PlcDocumentImportResult? Outcome {get;internal set;}
        public string[] PostInventory {get;internal set;}=new string[0];
    }
    public sealed class PlcBatchDocumentImportResult
    {
        public string Policy => "batch-document-global-db-v1";
        public string PlanHash {get;internal set;}="";
        public string Status {get;internal set;}="planned";
        public bool Attempted {get;internal set;}
        public bool MayHaveChanged => Attempted;
        public bool RequiresSessionReset {get;internal set;}
        public string Error {get;internal set;}="";
        public PlcBatchDocumentImportItem[] Items {get;internal set;}=new PlcBatchDocumentImportItem[0];
    }
    internal static class PlcBatchDocumentImportPolicy
    {
        internal const int MaximumItems=16; // At most 128 MiB of bounded code/resource pairs.
        private static string Field(string s)=>s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+s;
        internal static string PlanHash(IEnumerable<PlcBatchDocumentImportItem> items)=>PlcDocumentImportPolicy.Hash(Encoding.UTF8.GetBytes(Field("batch-document-global-db-v1")+string.Concat(items.SelectMany(x=>new[]{x.Name,x.Preview.PlanHash}).Select(Field))));
        private static string[] Snapshot(PlcDocumentImportContext c)
        {
            var entries=c.Inventory().Take(4097).ToArray();
            if(entries.Length>4096 || entries.Any(x=>string.IsNullOrEmpty(x)||x.Length>4096) || entries.Distinct(StringComparer.Ordinal).Count()!=entries.Length)throw new ArgumentException("Incomplete or ambiguous batch inventory.");
            return entries.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        private static string[] Fields(string value)
        {
            var fields=new List<string>();int at=0;
            while(at<value.Length)
            {
                int colon=value.IndexOf(':',at);int length;
                if(colon<0 || !int.TryParse(value.Substring(at,colon-at),out length) || length<0 || length>value.Length-colon-1)throw new InvalidDataException("Malformed inventory identity.");
                fields.Add(value.Substring(colon+1,length));at=colon+1+length;
            }
            if(fields.Count!=4)throw new InvalidDataException("Incomplete inventory identity.");return fields.ToArray();
        }
        private sealed class BorrowedStream : Stream
        {
            private readonly Stream stream;
            internal BorrowedStream(Stream stream){this.stream=stream;}
            public override bool CanRead=>stream.CanRead;public override bool CanSeek=>stream.CanSeek;public override bool CanWrite=>false;
            public override long Length=>stream.Length;public override long Position {get=>stream.Position;set=>stream.Position=value;}
            public override int Read(byte[] b,int o,int n)=>stream.Read(b,o,n);
            public override long Seek(long o,SeekOrigin s)=>stream.Seek(o,s);
            public override void Flush()=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
            // Outer manifest owns every lock until the whole batch ends.
            protected override void Dispose(bool disposing){}
        }
        internal static PlcBatchDocumentImportResult Run(PlcDocumentImportContext[] contexts,bool dryRun,string expectedHash,bool confirm,string expectedProject)
        {
            var names=contexts.Select(x=>x.Request.Name).ToArray();
            if(names==null || names.Length<1 || names.Length>MaximumItems || names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)throw new ArgumentException("Explicit ordered distinct manifest of 1..16 basenames required.");
            var r=contexts[0].Request;var originalOpens=contexts.Select(x=>x.Open).ToArray();var originalImports=contexts.Select(x=>x.Import).ToArray();var originalRechecks=contexts.Select(x=>x.Recheck).ToArray();
            foreach(var context in contexts){context.Request.DryRun=true;PlcDocumentImportPolicy.ValidateOptions(context.Request);}
            if(!dryRun && (!confirm || expectedProject!=r.Project || expectedHash.Length!=64 || expectedHash.Any(ch=>!"0123456789abcdef".Contains(ch))))throw new ArgumentException("Exact batch preview hash/project and confirmation required.");
            var locks=new Dictionary<string,Stream>(StringComparer.Ordinal);var hashes=new Dictionary<string,string>(StringComparer.Ordinal);var selections=new Dictionary<string,string[]>(StringComparer.Ordinal);
            var result=new PlcBatchDocumentImportResult();
            try
            {
                // Lock the complete explicit selection before parsing any set or inventory.
                foreach(var c in contexts)
                {
                    var name=c.Request.Name;var paths=c.Scan();selections.Add(name,paths);
                    if(paths.Length<1 || paths.Length>2)throw new ArgumentException("Invalid document pair.");
                    foreach(var path in paths){if(locks.ContainsKey(path))throw new ArgumentException("Overlapping document sets.");var stream=c.Open(path);locks.Add(path,stream);hashes.Add(path,PlcDocumentImportPolicy.Hash(PlcDocumentImportPolicy.Read(stream)));}
                }
                foreach(var c in contexts){c.Open=path=>new BorrowedStream(locks[path]);var native=c.Import;c.Import=()=>{result.Attempted=true;result.Items.Single(x=>x.Name==c.Request.Name).Status="failed";return native();};}
                var baseline=Snapshot(contexts[0]);var items=new List<PlcBatchDocumentImportItem>();
                foreach(var c in contexts)
                {
                    var name=c.Request.Name;var preview=c.Run();
                    if(preview.TargetIdentity!=r.TargetIdentity || preview.ProjectFile!=r.Project || preview.ProcessId!=r.ProcessId || preview.SoftwarePath!=r.Software || preview.GroupPath!=r.Group || preview.InputDirectory!=r.Directory || preview.Release!=r.Release)throw new ArgumentException("Batch sets must share one exact target/session/directory/release.");
                    if(!baseline.SequenceEqual(preview.Inventory,StringComparer.Ordinal))throw new ArgumentException("Inventory changed during complete batch preflight.");
                    items.Add(new PlcBatchDocumentImportItem{Name=name,Preview=preview});
                }
                result.Items=items.ToArray();result.PlanHash=PlanHash(items);
                if(dryRun)return result;
                if(result.PlanHash!=expectedHash)throw new ArgumentException("Batch manifest changed; review a new complete preview.");
                void Stable()
                {
                    foreach(var check in originalRechecks)check();if(!baseline.SequenceEqual(Snapshot(contexts[0]),StringComparer.Ordinal))throw new ArgumentException("Batch inventory changed outside verified imports.");
                    foreach(var c in contexts)if(!selections[c.Request.Name].SequenceEqual(c.Scan(),StringComparer.Ordinal))throw new ArgumentException("Document selection changed.");
                    foreach(var path in locks.Keys)if(hashes[path]!=PlcDocumentImportPolicy.Hash(PlcDocumentImportPolicy.Read(locks[path])))throw new IOException("Manifest bytes changed.");
                }
                foreach(var context in contexts)context.Recheck=Stable;
                foreach(var item in items)
                {
                    var c=contexts[items.IndexOf(item)];r=c.Request;
                    try
                    {
                        Stable();r.DryRun=true;var current=c.Run();
                        r.DryRun=false;r.Confirm=true;r.ExpectedProject=expectedProject;r.ExpectedHash=current.PlanHash;
                        var outcome=c.Run();item.Outcome=outcome;result.Attempted|=outcome.Attempted;
                        if(outcome.Status!="imported" || outcome.RequiresSessionReset){item.Status="failed";throw new InvalidDataException("Native document outcome uncertain.");}
                        item.Status="succeeded";
                        var after=Snapshot(c);item.PostInventory=after;var added=after.Except(baseline,StringComparer.Ordinal).ToArray();
                        if(baseline.Except(after,StringComparer.Ordinal).Any() || added.Length!=1)throw new InvalidDataException("Unexpected post-import inventory delta.");
                        var identity=Fields(added[0]);
                        if(identity[0]!=r.Group || identity[1]!=item.Name || identity[2]!="GlobalDB" || string.IsNullOrEmpty(identity[3]))throw new InvalidDataException("Unverified post-import identity.");
                        baseline=after;Stable();
                    }
                    catch(Exception)
                    {
                        if(!result.Attempted)throw;
                        if(item.Outcome?.Attempted==true)item.Status="failed";
                        result.Status="unknown";result.RequiresSessionReset=true;result.Error="batch-outcome-uncertain-no-replay-or-rollback";return result;
                    }
                }
                result.Status="imported";return result;
            }
            finally
            {
                for(int i=0;i<contexts.Length;i++){contexts[i].Open=originalOpens[i];contexts[i].Import=originalImports[i];contexts[i].Recheck=originalRechecks[i];}Exception? failure=null;
                foreach(var stream in locks.Values)try{stream.Dispose();}catch(Exception ex){failure=ex;}
                if(failure!=null)
                {
                    if(result.Attempted){result.Status="unknown";result.RequiresSessionReset=true;result.Error="batch-outcome-uncertain-no-replay-or-rollback";}
                    else throw new IOException("Manifest lock cleanup failed before any native import.",failure);
                }
            }
        }
    }
}
