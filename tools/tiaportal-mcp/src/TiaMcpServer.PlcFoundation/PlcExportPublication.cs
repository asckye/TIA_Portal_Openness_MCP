using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.PlcFoundation
{
    // API-independent file publication; the callback is the only native boundary.
    internal static class PlcExportPublication
    {
        internal static string? Publish(FileInfo destination, Action<FileInfo> export)
        {
            var output=PlcFoundationPolicy.XmlOutput(destination.FullName);
            var stage=new DirectoryInfo(Path.Combine(output.Directory!.FullName,".tia-export-"+Guid.NewGuid().ToString("N")));
            var staged=new FileInfo(Path.Combine(stage.FullName,output.Name));
            string phase="create-stage"; string? hash=null;
            try
            {
                // A sibling staging directory keeps the rename on the destination volume.
                stage.Create();
                if((stage.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging directory is a reparse point.");
                phase="native-export";
                export(staged);
                phase="validate-output"; staged.Refresh();
                if(!staged.Exists || staged.Length==0 || (staged.Attributes & FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Export did not produce a regular nonempty file.");
                using(var stream=new FileStream(staged.FullName,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024,CloseInput=false };
                    using(var reader=XmlReader.Create(stream,settings))
                    {
                        var document=XDocument.Load(reader);
                        if(document.Root==null || document.Root.Name.LocalName!="Document") throw new IOException("Invalid Openness XML root.");
                    }
                    stream.Position=0;
                    using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                    phase="publish";
                    // Two-argument Move never replaces an existing destination. No copy,
                    // delete, overwrite flag or retry is used if another writer wins.
                    File.Move(staged.FullName,output.FullName);
                }
            }
            catch(Exception cause)
            {
                // Native exception messages/XML may contain project data: wire only the
                // fixed message and allowlisted recovery paths, phase and content hash.
                var failure=new IOException("XML export was not published; inspect the retained staging evidence before retrying.",cause);
                failure.Data["outputFile"]=output.FullName;
                failure.Data["stagedFile"]=staged.FullName;
                failure.Data["recoveryDirectory"]=stage.FullName;
                failure.Data["exportPhase"]=phase;
                if(hash!=null) failure.Data["stagedSha256"]=hash;
                throw failure;
            }
            // Publication has succeeded. Never turn a best-effort empty-directory
            // cleanup failure into an ambiguous failed publication, or delete sidecars.
            try { stage.Delete(false); return null; }
            catch(IOException) { return stage.FullName; }
            catch(UnauthorizedAccessException) { return stage.FullName; }
        }
    }
}
