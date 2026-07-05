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
    /// Unambiguous scalars (string, bool, whole number) are plain JSON values. Ambiguous types
    /// (optionset vs int, lookup, money, datetime, decimal/double, guid) use a typed envelope,
    /// e.g. <c>{"statuscode":{"t":"optionset","v":2}}</c> or
    /// <c>{"primarycontactid":{"t":"lookup","entity":"contact","v":"&lt;guid&gt;"}}</c>.
    /// Export produces exactly this shape so it round-trips, and a plain value supplied for an
    /// ambiguous column is REJECTED with a clear message rather than guessed.
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
            if (!AttributeTypeMapper.IsAmbiguous(attr.Kind))
            {
                switch (attr.Kind)
                {
                    case AttributeEditorKind.Boolean:
                        return new JValue(Convert.ToBoolean(attr.Value));
                    case AttributeEditorKind.WholeNumber:
                        return new JValue(Convert.ToInt32(attr.Value));
                    case AttributeEditorKind.BigInt:
                        return new JValue(Convert.ToInt64(attr.Value));
                    default:
                        return new JValue(Convert.ToString(attr.Value, CultureInfo.InvariantCulture));
                }
            }

            var envelope = new JObject { ["t"] = AttributeTypeMapper.EnvelopeToken(attr.Kind) };
            switch (attr.Kind)
            {
                case AttributeEditorKind.Money:
                case AttributeEditorKind.Decimal:
                    envelope["v"] = Convert.ToDecimal(attr.Value, CultureInfo.InvariantCulture);
                    break;
                case AttributeEditorKind.Double:
                    envelope["v"] = Convert.ToDouble(attr.Value, CultureInfo.InvariantCulture);
                    break;
                case AttributeEditorKind.OptionSet:
                    envelope["v"] = Convert.ToInt32(attr.Value, CultureInfo.InvariantCulture);
                    break;
                case AttributeEditorKind.MultiSelectOptionSet:
                    envelope["v"] = new JArray(AsInts(attr.Value).Cast<object>().ToArray());
                    break;
                case AttributeEditorKind.DateTime:
                    envelope["v"] = Convert.ToDateTime(attr.Value, CultureInfo.InvariantCulture).ToString("o", CultureInfo.InvariantCulture);
                    break;
                case AttributeEditorKind.Guid:
                    envelope["v"] = attr.Value.ToString();
                    break;
                case AttributeEditorKind.Lookup:
                    envelope["entity"] = attr.LookupEntity;
                    envelope["v"] = attr.Value.ToString();
                    break;
            }
            return envelope;
        }

        // ---- import helpers ----------------------------------------------------------------

        private static TypedAttribute ParseProperty(string name, JToken token, Func<string, AttributeEditorKind?> kindResolver)
        {
            // Typed envelope: an object carrying a "t" discriminator.
            if (token is JObject obj && obj["t"] != null)
            {
                var kind = AttributeTypeMapper.KindFromEnvelopeToken(obj["t"].Value<string>());
                return FromEnvelope(name, kind, obj);
            }

            // Plain scalar: only acceptable if the column is an unambiguous kind.
            var resolved = kindResolver?.Invoke(name);
            if (resolved.HasValue)
            {
                if (AttributeTypeMapper.IsAmbiguous(resolved.Value))
                {
                    throw new FormatException(
                        $"is '{resolved.Value}', which is ambiguous — supply a typed envelope, e.g. " +
                        $"{{\"t\":\"{AttributeTypeMapper.EnvelopeToken(resolved.Value)}\",\"v\":...}}.");
                }
                return FromPlainScalar(name, resolved.Value, token);
            }

            // No metadata: accept only the obviously-unambiguous JSON primitives.
            return FromUnknownScalar(name, token);
        }

        private static TypedAttribute FromEnvelope(string name, AttributeEditorKind kind, JObject obj)
        {
            var v = obj["v"];
            switch (kind)
            {
                case AttributeEditorKind.Money:
                case AttributeEditorKind.Decimal:
                    return new TypedAttribute(name, kind, v.Value<decimal>());
                case AttributeEditorKind.Double:
                    return new TypedAttribute(name, kind, v.Value<double>());
                case AttributeEditorKind.OptionSet:
                    return new TypedAttribute(name, kind, v.Value<int>());
                case AttributeEditorKind.MultiSelectOptionSet:
                    return new TypedAttribute(name, kind, v.Values<int>().ToList());
                case AttributeEditorKind.DateTime:
                    return new TypedAttribute(name, kind, DateTime.Parse(v.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                case AttributeEditorKind.Guid:
                    return new TypedAttribute(name, kind, Guid.Parse(v.Value<string>()));
                case AttributeEditorKind.Lookup:
                    var entity = obj["entity"]?.Value<string>()
                                 ?? throw new FormatException("lookup envelope requires an \"entity\" field.");
                    return new TypedAttribute(name, kind, Guid.Parse(v.Value<string>()), entity);
                default:
                    throw new FormatException($"'{kind}' is not a valid envelope kind.");
            }
        }

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
