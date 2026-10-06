using System.Globalization;
using System.Reflection;
using System.Resources;

namespace TiaMcp.Updater
{
    internal static class UpdaterText
    {
        private static readonly ResourceManager Resources = new ResourceManager("TiaMcp.Updater.UpdaterMessages", Assembly.GetExecutingAssembly());

        internal static string Bilingual(string key, string english)
        {
            return Resources.GetString(key, CultureInfo.GetCultureInfo("zh-CN")) + " / " + english;
        }
    }
}
