using System;
using System.IO;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    internal static class PlcBatchImportRecovery
    {
        internal static void Precheck()
        {
            var locations=TiaOpenness.Shared.DataLocations.Current;
            string path=locations.RecoveryAttemptedPath;
            try
            {
                if(locations.Root==null) throw new IOException("No writable data root is available.");
                TiaOpenness.Shared.NativeExportPolicy.CheckWritableDirectory(path);
            }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
            {
                var refusal=new AdapterPreconditionException("Recovery is unavailable; attempted path: "+path,"overwrite",false,error);refusal
                    .Data["attemptedPath"]=path;throw refusal;
            }
        }
        internal static string Directory()
        {
            Precheck();
            var root=TiaOpenness.Shared.DataLocations.Current.Root;
            var path=Path.Combine(root!,"recovery","plc-import-"+Guid.NewGuid().ToString("N"));
            try
            {
                for(var ancestor=new DirectoryInfo(path);ancestor!=null;ancestor=ancestor.Parent)
                    if(ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint)!=0)
                        throw new IOException("Recovery ancestry contains a reparse point.");
                System.IO.Directory.CreateDirectory(path);return path;
            }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
            {
                var refusal=new AdapterPreconditionException("Recovery is unavailable; attempted path: "+path,"overwrite",false,error);refusal
                    .Data["attemptedPath"]=path;throw refusal;
            }
        }
    }

}
