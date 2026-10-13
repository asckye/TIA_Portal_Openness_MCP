using System;
using System.Linq;
using System.Text;
namespace TiaMcp.Adapters.Contracts
{
        public sealed class UnifiedScriptPair
        {
            public string JsName = "", YamlName = "", Text = "";
            public byte[] Js = Array.Empty<byte>(), Yaml = Array.Empty<byte>();
            public Encoding Encoding = new UTF8Encoding(false, true);
            public bool Bom;
            public byte[] Encode(string text) => (Bom ? Encoding.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.GetBytes(text)).ToArray();
        }
}
