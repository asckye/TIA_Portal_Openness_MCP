using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// Defensive readers for the Openness object model. Attribute names and optional
    /// properties drift between TIA versions, and a missing one throws
    /// <c>EngineeringNotSupportedException</c> rather than returning null &#8212; which would
    /// abort a whole project scan over one cosmetic field. Everything optional goes through here.
    /// </summary>
    internal static class EngineeringExtensions
    {
        /// <summary>
        /// Attribute names this kind of object does not have. Whether an attribute exists is a
        /// property of the type, not of the instance, and asking for a missing one costs a COM
        /// round trip and a thrown <c>EngineeringNotSupportedException</c>. Over a program with
        /// hundreds of blocks that was hundreds of exceptions per optional field; now it is one.
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> UnsupportedAttributes =
            new ConcurrentDictionary<string, bool>();

        /// <summary>Resolved properties, including the misses, so reflection runs once per type.</summary>
        private static readonly ConcurrentDictionary<string, PropertyInfo> Properties =
            new ConcurrentDictionary<string, PropertyInfo>();

        /// <summary>Reads an attribute, returning <paramref name="fallback"/> when it is absent or of another type.</summary>
        public static T Attr<T>(this IEngineeringObject item, string name, T fallback = default)
        {
            if (item == null) return fallback;

            var key = item.GetType().FullName + "|" + name;
            if (UnsupportedAttributes.ContainsKey(key)) return fallback;

            try
            {
                var value = item.GetAttribute(name);
                if (value == null) return fallback;
                return Coerce(value, fallback);
            }
            catch (Exception)
            {
                UnsupportedAttributes[key] = true;
                return fallback;
            }
        }

        /// <summary>Reads a property that may not exist on this TIA version, without throwing.</summary>
        public static T Prop<T>(this object instance, string propertyName, T fallback = default)
        {
            if (instance == null) return fallback;

            var type = instance.GetType();
            var property = Properties.GetOrAdd(type.FullName + "|" + propertyName, _ => type.GetProperty(propertyName));
            if (property == null) return fallback;

            try
            {
                var value = property.GetValue(instance, null);
                return value == null ? fallback : Coerce(value, fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static T Coerce<T>(object value, T fallback)
        {
            try
            {
                if (value is T typed) return typed;

                var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (target == typeof(string)) return (T)(object)value.ToString();
                if (target.IsEnum) return (T)Enum.Parse(target, value.ToString(), true);
                return (T)Convert.ChangeType(value, target);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Flattens a <c>MultilingualText</c> to a single string, preferring the invariant
        /// or English entry and otherwise taking whatever the project actually carries.
        /// </summary>
        public static string AsText(this MultilingualText text)
        {
            if (text == null) return null;
            try
            {
                var items = text.Items.ToList();
                if (items.Count == 0) return null;

                var preferred = items.FirstOrDefault(i =>
                        i.Language?.Culture?.Name?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
                    ?? items[0];

                var value = preferred.Text;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Converts a TIA <see cref="DateTime"/> to UTC, treating unspecified kinds as local.</summary>
        public static DateTimeOffset? AsOffset(this DateTime value)
        {
            if (value == default) return null;
            var kind = value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Local)
                : value;
            return new DateTimeOffset(kind);
        }

        /// <summary>Enumerates a composition without letting one bad member kill the whole walk.</summary>
        public static IEnumerable<T> SafeEnumerate<T>(this IEnumerable<T> source, Action<Exception> onError = null)
        {
            IEnumerator<T> enumerator;
            try { enumerator = source.GetEnumerator(); }
            catch (Exception ex) { onError?.Invoke(ex); yield break; }

            using (enumerator)
            {
                while (true)
                {
                    T current;
                    try
                    {
                        if (!enumerator.MoveNext()) yield break;
                        current = enumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(ex);
                        yield break;
                    }
                    yield return current;
                }
            }
        }
    }
}
