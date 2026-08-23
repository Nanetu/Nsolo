using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Cdm.Figma.Json
{
    /// <summary>
    /// A <see cref="StringEnumConverter"/> that falls back to an enum's default value when Figma
    /// sends a name this version of the package does not know about, instead of aborting the whole
    /// document.
    ///
    /// Figma keeps adding values to its enums. This package pins the set that existed when it was
    /// written, so a file using anything newer — <c>layoutMode: "GRID"</c> being the one that
    /// started this — fails to deserialize, and takes every other node in the file down with it.
    /// A design tool adding a feature should not make a document unreadable; the frame that used it
    /// should just come in without that feature.
    ///
    /// Falling back to the zero value is specifically right for <c>layoutMode</c>, which is why
    /// this is preferred over teaching the enum about GRID: consumers branch on
    /// <c>layoutMode != None</c> and then test for Horizontal or Vertical, so a fourth value would
    /// pass the first test and match neither of the others. Degrading to None means a grid frame
    /// arrives with no layout group and its children keep the absolute positions Figma gave them,
    /// which is a fair rendering of that frame and not a broken one.
    ///
    /// Unknown values are logged once each, so a design that quietly lost a feature on import says
    /// so rather than being silently approximated.
    /// </summary>
    public class TolerantStringEnumConverter : StringEnumConverter
    {
        private static readonly HashSet<string> ReportedValues = new HashSet<string>();

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            try
            {
                return base.ReadJson(reader, objectType, existingValue, serializer);
            }
            catch (JsonSerializationException)
            {
                Type enumType = Nullable.GetUnderlyingType(objectType) ?? objectType;

                // Only enums are handled here. Anything else that failed for its own reasons is a
                // real error and is left to travel.
                if (!enumType.IsEnum) throw;

                Report(enumType, reader.Value);

                // A nullable enum has somewhere better to put "no idea" than a made-up member.
                if (Nullable.GetUnderlyingType(objectType) != null) return null;

                return Enum.ToObject(enumType, 0);
            }
        }

        private static void Report(Type enumType, object value)
        {
            string key = $"{enumType.FullName}:{value}";
            if (!ReportedValues.Add(key)) return;

            UnityEngine.Debug.LogWarning(
                $"Figma: '{value}' is not a value this importer knows for {enumType.Name}. " +
                $"Falling back to {Enum.ToObject(enumType, 0)}. The document still imports; " +
                "whatever that value controlled will not be applied.");
        }
    }
}
