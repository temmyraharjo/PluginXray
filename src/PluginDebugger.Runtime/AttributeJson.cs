using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginDebugger.Runtime
{
    /// <summary>Outcome of a JSON import: the parsed attributes plus any rejection messages.</summary>
    public sealed class JsonImportResult
    {
        public List<TypedAttribute> Attributes { get; } = new List<TypedAttribute>();
        public List<string> Errors { get; } = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    /// <summary>
    /// Import / export of typed attribute values as JSON (requirements §4.5 / OD-3).
    ///
    /// Values mirror the CRM/Dataverse SDK object shapes (<c>Microsoft.Xrm.Sdk</c>): a lookup is
    /// <c>{"primarycontactid":{"Id":"&lt;guid&gt;","LogicalName":"contact","Name":"Jane"}}</c>,
    /// money is <c>{"revenue":{"Value":12.5}}</c>, a choice is <c>{"statuscode":{"Value":2}}</c>,
    /// a multi-select is an array of <c>{"Value":n}</c>, a datetime/guid is an ISO/guid string, and
    /// plain scalars (string, bool, number) stay plain. The object shape identifies the type
    /// <em>family</em>; table metadata (<paramref name="kindResolver"/>) pins the exact type where
    /// a shape is shared (e.g. Money vs OptionSetValue, int vs decimal). Export produces exactly
    /// these shapes so a value round-trips, and a value whose shape is incompatible with the
    /// column's metadata kind is REJECTED with a clear message rather than guessed.
    /// </summary>
    public static class AttributeJson
    {
        public static string Export(IEnumerable<TypedAttribute> attributes)
        {
            var root = new JObject();
            foreach (var attr in attributes)
            {
                root[attr.LogicalName] = ToToken(attr);
            }
            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Parses JSON into typed attributes. <paramref name="kindResolver"/> maps an attribute
        /// logical name to its metadata-derived kind (null if unknown); it is what lets a plain
        /// value on an ambiguous column be rejected.
        /// </summary>
        public static JsonImportResult Import(string json, Func<string, AttributeEditorKind?> kindResolver)
        {
            var result = new JsonImportResult();
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
                try
                {
                    result.Attributes.Add(ParseProperty(property.Name, property.Value, kindResolver));
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"'{property.Name}': {ex.Message}");
                }
            }

            return result;
        }

        // ---- export helpers ----------------------------------------------------------------

        private static JToken ToToken(TypedAttribute attr)
        {
            switch (attr.Kind)
            {
                case AttributeEditorKind.Boolean:
                    return new JValue(Convert.ToBoolean(attr.Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.WholeNumber:
                    return new JValue(Convert.ToInt32(attr.Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.BigInt:
                    return new JValue(Convert.ToInt64(attr.Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.Decimal:
                    return new JValue(Convert.ToDecimal(attr.Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.Double:
                    return new JValue(Convert.ToDouble(attr.Value, CultureInfo.InvariantCulture));
                case AttributeEditorKind.Guid:
                    return new JValue(attr.Value.ToString());
                case AttributeEditorKind.DateTime:
                    return new JValue(Convert.ToDateTime(attr.Value, CultureInfo.InvariantCulture).ToString("o", CultureInfo.InvariantCulture));

                // ---- CRM/Dataverse SDK object shapes ----
                case AttributeEditorKind.Money:
                    return new JObject { ["Value"] = Convert.ToDecimal(attr.Value, CultureInfo.InvariantCulture) };
                case AttributeEditorKind.OptionSet:
                    return new JObject { ["Value"] = Convert.ToInt32(attr.Value, CultureInfo.InvariantCulture) };
                case AttributeEditorKind.MultiSelectOptionSet:
                    return new JArray(AsInts(attr.Value).Select(v => (JToken)new JObject { ["Value"] = v }).ToArray());
                case AttributeEditorKind.Lookup:
                    var reference = new JObject
                    {
                        ["Id"] = attr.Value.ToString(),
                        ["LogicalName"] = attr.LookupEntity
                    };
                    if (!string.IsNullOrEmpty(attr.LookupName))
                    {
                        reference["Name"] = attr.LookupName;
                    }
                    return reference;

                default: // String / Memo
                    return new JValue(Convert.ToString(attr.Value, CultureInfo.InvariantCulture));
            }
        }

        // ---- import helpers ----------------------------------------------------------------

        private static TypedAttribute ParseProperty(string name, JToken token, Func<string, AttributeEditorKind?> kindResolver)
        {
            var resolved = kindResolver?.Invoke(name);

            // Array => OptionSetValueCollection (multi-select), each element a {"Value":n} object.
            if (token is JArray array)
            {
                RequireCompatible(name, resolved, AttributeEditorKind.MultiSelectOptionSet);
                return new TypedAttribute(name, AttributeEditorKind.MultiSelectOptionSet, ReadOptionArray(array));
            }

            // Object => a reference (Id + LogicalName) or a value object (Money / OptionSetValue).
            if (token is JObject obj)
            {
                return ParseObject(name, obj, resolved);
            }

            // Plain scalar (string / bool / number). A shape-based type (Money, OptionSet, lookup,
            // multi-select) supplied as a bare scalar is a shape mismatch and is rejected below.
            if (resolved.HasValue)
            {
                if (IsObjectShaped(resolved.Value))
                {
                    throw new FormatException(
                        $"is '{resolved.Value}', which expects a CRM object shape (e.g. " +
                        $"{ExampleShape(resolved.Value)}), not a bare {token.Type} value.");
                }
                return FromPlainScalar(name, resolved.Value, token);
            }

            // No metadata: accept only the obviously-unambiguous JSON primitives.
            return FromUnknownScalar(name, token);
        }

        /// <summary>
        /// Parses a JSON object into a lookup (<c>EntityReference</c>) or a value object
        /// (<c>Money</c> / <c>OptionSetValue</c>). An optional <c>__type</c> hint disambiguates the
        /// Money-vs-OptionSet case where no metadata is available (arbitrary InputParameters, FR-4.6).
        /// </summary>
        private static TypedAttribute ParseObject(string name, JObject obj, AttributeEditorKind? resolved)
        {
            var id = Property(obj, "Id");
            var logicalName = Property(obj, "LogicalName");
            if (id != null || logicalName != null)
            {
                RequireCompatible(name, resolved, AttributeEditorKind.Lookup);
                if (id == null || logicalName == null)
                {
                    throw new FormatException("an EntityReference requires both \"Id\" and \"LogicalName\".");
                }
                var entity = logicalName.Value<string>();
                var refName = Property(obj, "Name")?.Value<string>();
                return new TypedAttribute(name, AttributeEditorKind.Lookup, Guid.Parse(id.Value<string>()), entity, refName);
            }

            var value = Property(obj, "Value");
            if (value == null)
            {
                throw new FormatException(
                    "unrecognized object shape — expected an EntityReference (\"Id\"+\"LogicalName\") " +
                    "or a value object (\"Value\").");
            }

            // {"Value":n} is a Money or an OptionSetValue only. Metadata decides which; failing that
            // a __type hint; failing that, an integer defaults to OptionSetValue and a fractional
            // number to Money. A scalar-shaped column (decimal/double/int/…) is a shape mismatch.
            var kind = resolved ?? HintedValueKind(obj) ?? DefaultValueKind(value);
            switch (kind)
            {
                case AttributeEditorKind.Money:
                    return new TypedAttribute(name, AttributeEditorKind.Money, value.Value<decimal>());
                case AttributeEditorKind.OptionSet:
                    return new TypedAttribute(name, AttributeEditorKind.OptionSet, value.Value<int>());
                default:
                    throw new FormatException(
                        $"a {{\"Value\":…}} object is a Money or OptionSetValue, but the column is '{kind}'.");
            }
        }

        private static AttributeEditorKind? HintedValueKind(JObject obj)
        {
            var hint = Property(obj, "__type")?.Value<string>();
            if (string.IsNullOrEmpty(hint))
            {
                return null;
            }
            // Accept the platform-style "Money:http://…" form as well as a bare "Money".
            var colon = hint.IndexOf(':');
            if (colon > 0)
            {
                hint = hint.Substring(0, colon);
            }
            switch (hint)
            {
                case "Money": return AttributeEditorKind.Money;
                case "OptionSetValue": return AttributeEditorKind.OptionSet;
                default: return null;
            }
        }

        private static AttributeEditorKind DefaultValueKind(JToken value) =>
            value.Type == JTokenType.Integer ? AttributeEditorKind.OptionSet : AttributeEditorKind.Money;

        private static List<int> ReadOptionArray(JArray array)
        {
            var values = new List<int>();
            foreach (var element in array)
            {
                if (element is JObject o && Property(o, "Value") is JToken v)
                {
                    values.Add(v.Value<int>());
                }
                else if (element.Type == JTokenType.Integer)
                {
                    values.Add(element.Value<int>()); // tolerate a bare [1,2,3] for multi-select
                }
                else
                {
                    throw new FormatException("multi-select entries must be {\"Value\":n} objects.");
                }
            }
            return values;
        }

        /// <summary>Guards that the JSON shape matches the column's metadata kind, when known.</summary>
        private static void RequireCompatible(string name, AttributeEditorKind? resolved, AttributeEditorKind shapeKind)
        {
            if (resolved.HasValue && resolved.Value != shapeKind)
            {
                throw new FormatException(
                    $"is '{resolved.Value}', but the JSON shape is a '{shapeKind}' " +
                    $"({ExampleShape(shapeKind)}) — shapes must match.");
            }
        }

        /// <summary>Kinds whose CRM shape is a JSON object/array rather than a bare scalar.</summary>
        private static bool IsObjectShaped(AttributeEditorKind kind)
        {
            switch (kind)
            {
                case AttributeEditorKind.Money:
                case AttributeEditorKind.OptionSet:
                case AttributeEditorKind.MultiSelectOptionSet:
                case AttributeEditorKind.Lookup:
                    return true;
                default:
                    return false;
            }
        }

        private static string ExampleShape(AttributeEditorKind kind)
        {
            switch (kind)
            {
                case AttributeEditorKind.Money: return "{\"Value\":12.5}";
                case AttributeEditorKind.OptionSet: return "{\"Value\":2}";
                case AttributeEditorKind.MultiSelectOptionSet: return "[{\"Value\":1},{\"Value\":2}]";
                case AttributeEditorKind.Lookup: return "{\"Id\":\"<guid>\",\"LogicalName\":\"contact\"}";
                default: return kind.ToString();
            }
        }

        /// <summary>Case-insensitive property lookup so "id"/"Id"/"logicalName" all resolve.</summary>
        private static JToken Property(JObject obj, string name) =>
            obj.GetValue(name, StringComparison.OrdinalIgnoreCase);

        private static TypedAttribute FromPlainScalar(string name, AttributeEditorKind kind, JToken token)
        {
            switch (kind)
            {
                case AttributeEditorKind.Boolean:
                    return new TypedAttribute(name, kind, token.Value<bool>());
                case AttributeEditorKind.WholeNumber:
                    return new TypedAttribute(name, kind, token.Value<int>());
                case AttributeEditorKind.BigInt:
                    return new TypedAttribute(name, kind, token.Value<long>());
                case AttributeEditorKind.Decimal:
                    return new TypedAttribute(name, kind, token.Value<decimal>());
                case AttributeEditorKind.Double:
                    return new TypedAttribute(name, kind, token.Value<double>());
                case AttributeEditorKind.DateTime:
                    return new TypedAttribute(name, kind, DateTime.Parse(token.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                case AttributeEditorKind.Guid:
                    return new TypedAttribute(name, kind, Guid.Parse(token.Value<string>()));
                default:
                    return new TypedAttribute(name, kind, token.Value<string>());
            }
        }

        private static TypedAttribute FromUnknownScalar(string name, JToken token)
        {
            if (token.Type == JTokenType.Boolean)
            {
                return new TypedAttribute(name, AttributeEditorKind.Boolean, token.Value<bool>());
            }
            if (token.Type == JTokenType.Integer)
            {
                return new TypedAttribute(name, AttributeEditorKind.WholeNumber, token.Value<int>());
            }
            if (token.Type == JTokenType.String)
            {
                return new TypedAttribute(name, AttributeEditorKind.String, token.Value<string>());
            }

            throw new FormatException(
                "no metadata available and the value is not an unambiguous scalar (string/bool/integer); " +
                "supply a typed envelope.");
        }

        private static IEnumerable<int> AsInts(object value)
        {
            if (value is System.Collections.IEnumerable en && !(value is string))
            {
                return en.Cast<object>().Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture));
            }
            return new[] { Convert.ToInt32(value, CultureInfo.InvariantCulture) };
        }
    }
}
