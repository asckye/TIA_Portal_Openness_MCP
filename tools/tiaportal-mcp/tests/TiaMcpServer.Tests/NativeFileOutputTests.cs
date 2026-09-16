using System;
using System.IO;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.Tests
{
    internal static class NativeFileOutputTests
    {
        internal static void Run(Action<bool,string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var dir=Path.Combine(Path.GetTempPath(),"tia-output-test-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                check(Fails(()=>NativeFileOutput.Plan("relative.xml")),"native export refuses relative file path");
                check(Fails(()=>NativeFileOutput.Plan(dir)),"native export refuses existing directory as file");
                var file=NativeFileOutput.Plan(Path.Combine(dir,"new.xml"));
                check(!file.Exists,"preview does not create output file");
                check(Fails(()=>NativeFileOutput.Verify(file)),"missing native output fails verification");
                File.WriteAllText(file.FullName,"");
                check(Fails(()=>NativeFileOutput.Verify(file)),"empty native output fails verification");
                File.WriteAllText(file.FullName,"abc",new System.Text.UTF8Encoding(false));
                check(Fails(()=>NativeFileOutput.Plan(file.FullName)),"existing output cannot be overwritten");
                var result=NativeFileOutput.Verify(file);
                check(result["sha256"]!.GetValue<string>()=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","native output hash checks actual bytes");
                check(result["contentSemanticsVerified"]!.GetValue<bool>()==false,"hashed file does not imply content completeness");
                File.Delete(file.FullName);
            } finally { Directory.Delete(dir); }
        }
    }
}
