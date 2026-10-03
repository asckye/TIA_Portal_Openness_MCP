using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Openness
{
    /// <summary>One HMI target's screens and tag tables, keyed by the path the wire protocol uses.</summary>
    internal sealed class HmiContext
    {
        public string DeviceId;
        public object Target;

        /// <summary>Path -> the Openness object, screens and tag tables together.</summary>
        public readonly Dictionary<string, object> Items =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, BlockKind> Kinds =
            new Dictionary<string, BlockKind>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, string> Folders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a classic WinCC HMI target through reflection rather than typed references.
    ///
    /// This is not squeamishness. The classic HMI types moved assembly between versions —
    /// <c>Siemens.Engineering.Hmi.dll</c> up to V20, <c>Siemens.Engineering.WinCC.dll</c> from V21 —
    /// and the adapter is compiled on the engineer's own machine against whatever they have. A
    /// typed reference to either one would fail to compile on the other, and a failed compile takes
    /// the PLC support down with it. Reflection compiles everywhere and, when a member is not
    /// there, yields nothing instead of failing.
    ///
    /// The folder names below are TIA's own: ScreenFolder, ScreenTemplateFolder, ScreenPopupFolder,
    /// ScreenSlideinFolder, TagFolder.
    /// </summary>
    internal static class HmiNavigator
    {
        /// <summary>The screen collections a classic HMI target exposes, in the order TIA lists them.</summary>
        private static readonly (string Property, string Collection, string Label)[] ScreenSources =
        {
            ("ScreenFolder", "Screens", "Screens"),
            ("ScreenTemplateFolder", "ScreenTemplates", "Templates"),
            ("ScreenPopupFolder", "ScreenPopups", "Popups"),
            ("ScreenSlideinFolder", "ScreenSlideins", "Slide-ins"),
        };

        /// <summary>True when this software object looks like a classic HMI target.</summary>
        public static bool IsHmiTarget(object software)
        {
            if (software == null) return false;
            var name = software.GetType().FullName ?? string.Empty;
            return name.IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("WinCC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Index(HmiContext context)
        {
            context.Items.Clear();
            context.Kinds.Clear();
            context.Folders.Clear();

            foreach (var source in ScreenSources)
            {
                var folder = Read(context.Target, source.Property);
                if (folder == null) continue;

                Walk(context, folder, source.Collection, source.Label, BlockKind.HmiScreen);
            }

            var tagFolder = Read(context.Target, "TagFolder");
            if (tagFolder != null) Walk(context, tagFolder, "TagTables", "Tags", BlockKind.TagTable);
        }

        /// <summary>
        /// Walks one folder and the user folders under it. TIA nests HMI folders exactly as it
        /// nests block folders, and the same slash-separated path shape is used for both.
        /// </summary>
        private static void Walk(HmiContext context, object folder, string collection, string prefix, BlockKind kind)
        {
            foreach (var item in Enumerate(Read(folder, collection)))
            {
                var name = Read(item, "Name") as string;
                if (string.IsNullOrEmpty(name)) continue;

                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                context.Items[path] = item;
                context.Kinds[path] = kind;
                context.Folders[path] = prefix;
            }

            foreach (var child in Enumerate(Read(folder, "Folders")))
            {
                var name = Read(child, "Name") as string;
                if (string.IsNullOrEmpty(name)) continue;

                Walk(context, child, collection, prefix.Length == 0 ? name : prefix + "/" + name, kind);
            }
        }

        public static BlockInfo Describe(HmiContext context, string path)
        {
            var item = context.Items[path];
            var folder = context.Folders.TryGetValue(path, out var known) ? known : string.Empty;

            return new BlockInfo
            {
                Path = path,
                FolderPath = folder,
                Name = Read(item, "Name") as string ?? path,
                Kind = context.Kinds.TryGetValue(path, out var kind) ? kind : BlockKind.Unknown,
                ProgrammingLanguage = null,
                // A screen has no compile state and no know-how protection; saying otherwise would
                // put a warning on every row.
                IsConsistent = true,
                IsKnowHowProtected = false,
                ModifiedDate = null,
            };
        }

        /// <summary>
        /// Exports one screen or HMI tag table. Openness gives these the same
        /// <c>Export(FileInfo, ExportOptions)</c> shape as a PLC block, so the call is found by
        /// signature rather than by type.
        /// </summary>
        public static string Export(object item, string directory, string fileName)
        {
            var method = item.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Export"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(FileInfo)
                                     && m.GetParameters()[1].ParameterType.IsEnum);

            if (method == null)
            {
                throw new NotSupportedException(
                    "This TIA version's " + item.GetType().Name + " has no Export(FileInfo, ExportOptions).");
            }

            var file = new FileInfo(Path.Combine(directory, fileName + ".xml"));
            if (file.Exists) file.Delete();

            var options = method.GetParameters()[1].ParameterType;
            var withDefaults = Enum.GetNames(options)
                .FirstOrDefault(n => string.Equals(n, "WithDefaults", StringComparison.OrdinalIgnoreCase))
                ?? Enum.GetNames(options).First();

            method.Invoke(item, new[] { file, Enum.Parse(options, withDefaults) });
            return file.FullName;
        }

        private static object Read(object instance, string property)
        {
            if (instance == null) return null;
            try
            {
                var info = instance.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                return info?.GetValue(instance, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Enumerates an Openness composition without letting one unreadable member end the walk —
        /// the same rule the typed navigator follows.
        /// </summary>
        private static IEnumerable<object> Enumerate(object value)
        {
            if (!(value is IEnumerable sequence)) yield break;

            IEnumerator enumerator;
            try { enumerator = sequence.GetEnumerator(); }
            catch (Exception) { yield break; }

            while (true)
            {
                object current;
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                    current = enumerator.Current;
                }
                catch (Exception)
                {
                    yield break;
                }
                if (current != null) yield return current;
            }
        }
    }
}
