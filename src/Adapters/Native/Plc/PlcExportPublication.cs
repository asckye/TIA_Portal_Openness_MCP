using System;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters
{
    // API-independent file publication; the callback is the only native boundary.
    internal static class PlcExportPublication
    {
        private static void WriteNormalized(FileInfo file,string content,Encoding encoding)
        {
            using(var stream=new FileStream(file.FullName,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream,encoding)) writer.Write(content);
        }

        internal static string? Publish(FileInfo destination, Action<FileInfo> export) => Publish(destination,export,false);

        internal static string? Publish(FileInfo destination, Action<FileInfo> export, bool stripVolatile, Action<bool>? normalized = null,
                Action<FileInfo,string,Encoding>? writeNormalized = null)
        {
            // The native worker runs on Windows. Unix File.Move may race between
            // destination existence checks and rename, overwriting another publisher.
            // Refuse unsupported hosts before invoking export or touching the filesystem.
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Safe XML export publication requires the Windows worker.");
            var output=PlcFoundationPolicy.XmlOutput(destination.FullName);
            var stage=
                new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy
                    .FullPath(Path.Combine(output.Directory!.FullName,".tia-export-"+Guid.NewGuid().ToString("N"))));
            var staged=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.Combine(stage.FullName,"export.xml")));
            string phase="create-stage"; string? hash=null;
            string recoveryFile=staged.FullName; FileInfo? retainedNative=null;
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
                XDocument document;
                using(var stream=new FileStream(staged.FullName,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    var settings=new XmlReaderSettings {
                        DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024,CloseInput=false };
                    using(var reader=XmlReader.Create(stream,settings))
                    {
                        document=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
                        if(document.Root==null || document.Root.Name.LocalName!="Document") throw new IOException("Invalid Openness XML root.");
                    }
                    if(stripVolatile)
                    {
                        stream.Position=0;
                        using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                    }
                }
                bool changed=false;
                if(stripVolatile)
                {
                    phase="normalize-output";
                    var info=document.Root!.Elements(document.Root.Name.Namespace+"DocumentInfo").SingleOrDefault();
                    if(info!=null)
                    {
                        var bytes=File.ReadAllBytes(staged.FullName);
                        bool bom=bytes.Length>=3 && bytes[0]==0xef && bytes[1]==0xbb && bytes[2]==0xbf;
                        string native=Encoding.UTF8.GetString(bytes);
                        string newline=native.Contains("\r\n") ? "\r\n" : native.Contains("\r") ? "\r" : "\n";
                        var created=info.Element(info.Name.Namespace+"Created");
                        if(created!=null) created.Value="2000-01-01T00:00:00.0000000Z";
                        info.Element(info.Name.Namespace+"InstalledProducts")?.RemoveNodes();
                        var settings=new XmlWriterSettings { Encoding=new UTF8Encoding(bom),Indent=false,NewLineChars=newline,
                            NewLineHandling=NewLineHandling.Entitize,OmitXmlDeclaration=document.Declaration==null };
                        using(var buffer=new MemoryStream())
                        {
                            using(var writer=XmlWriter.Create(buffer,settings)) document.Save(writer);
                            var rewritten=Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\ufeff');
                            var sibling=new FileInfo(Path.Combine(stage.FullName,"normalized.xml"));
                            (writeNormalized ?? WriteNormalized)(sibling,rewritten.Replace("\n",newline),new UTF8Encoding(bom));
                            var nativeBackup=new FileInfo(Path.Combine(stage.FullName,"native.xml"));
                            File.Move(staged.FullName,nativeBackup.FullName);
                            retainedNative=nativeBackup; recoveryFile=nativeBackup.FullName;
                            File.Move(sibling.FullName,staged.FullName);
                            recoveryFile=staged.FullName; hash=null;
                        }
                        changed=true;
                    }
                }
                using(var stream=new FileStream(staged.FullName,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                    phase="publish";
                    // On the supported Windows worker, Move uses non-replacing rename. No copy,
                    // delete, overwrite flag or retry is used if another writer wins.
                    File.Move(staged.FullName,output.FullName);
                    normalized?.Invoke(changed);
                }
            }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch(Exception cause)
            {
                // Native exception messages/XML may contain project data: wire only the
                // fixed message and allowlisted recovery paths, phase and content hash.
                var failure=new IOException("XML export was not published; inspect the retained staging evidence before retrying.",cause);
                failure.Data["outputFile"]=output.FullName;
                failure.Data["stagedFile"]=recoveryFile;
                failure.Data["recoveryDirectory"]=stage.FullName;
                failure.Data["exportPhase"]=phase;
                if(hash!=null) failure.Data["stagedSha256"]=hash;
                throw failure;
            }
            // Publication has succeeded. Never turn a best-effort empty-directory
            // cleanup failure into an ambiguous failed publication, or delete sidecars.
            try { retainedNative?.Delete(); stage.Delete(false); return null; }
            catch(IOException) /* swallow(cleanup): publication already succeeded; return the retained staging path when empty-directory deletion fails */ { return stage.FullName; }
            catch(UnauthorizedAccessException) /* swallow(cleanup): publication already succeeded; return the retained staging path when deletion is denied */ { return stage.FullName; }
        }
    }
}
