using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginDebugger.Runtime
{
    /// <summary>Outcome of a FormattedValues JSON import: the parsed pairs plus any rejection messages.</summary>
    public sealed class FormattedValueImportResult
    {
        public Dictionary<string, string> Values { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; } = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    /// <summary>
    /// Import / export of an entity's <c>FormattedValues</c> (requirements FR-5.7). Unlike typed
    /// attributes (see <see cref="AttributeJson"/>) a formatted value carries no type — it is always
    /// a display string keyed by attribute logical name — so the JSON is a plain object of
    /// <c>{"&lt;attr&gt;":"&lt;display string&gt;"}</c>. A non-string value is rejected, not coerced.
    /// </summary>
    public static class FormattedValueJson
    {
        public static string Export(IEnumerable<KeyValuePair<string, string>> values)
        {
            var root = new JObject();
            if (values != null)
            {
                foreach (var pair in values)
                {
                    root[pair.Key] = pair.Value;
                }
            }
            return root.ToString(Formatting.Indented);
        }

        public static FormattedValueImportResult Import(string json)
        {
            var result = new FormattedValueImportResult();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException ex)
            {
                result.Errors.Add("Invalid JSON: " + ex.Message);
                return result;
            }

            foreach (var property in root.Properties())
            {
                if (property.Value.Type != JTokenType.String)
                {
                    result.Errors.Add($"'{property.Name}': a formatted value must be a string (got {property.Value.Type}).");
                    continue;
                }
                if (result.Values.ContainsKey(property.Name))
                {
                    result.Errors.Add($"'{property.Name}': duplicate key.");
                    continue;
                }
                result.Values[property.Name] = property.Value.Value<string>();
            }

            return result;
        }
    }
}
