using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginDebugger.Runtime
{
    /// <summary>One imported pre/post entity image (requirements FR-11.7).</summary>
    public sealed class ImportedImage
    {
        public bool IsPreImage { get; set; }
        public string Key { get; set; }
        public Entity Entity { get; set; }
    }

    /// <summary>A key + boxed value from an imported InputParameters / SharedVariables entry.</summary>
    public sealed class ImportedNamedValue
    {
        public string Key { get; set; }
        public object Value { get; set; }
    }

    /// <summary>
    /// The result of parsing a serialized <c>IExecutionContext</c> (requirements §4.11). Holds SDK
    /// objects (it lives only in the main domain, feeding the UI editors), plus any non-fatal
    /// <see cref="Warnings"/> gathered while parsing — an unknown <c>__type</c>, an unsupported value
    /// shape, etc. — so nothing is silently dropped.
    /// </summary>
    public sealed class ImportedContext
    {
        public string MessageName { get; set; }
        public int Stage { get; set; }
        public int Mode { get; set; }
        public int Depth { get; set; } = 1;
        public string PrimaryEntityName { get; set; }
        public Guid PrimaryEntityId { get; set; }
        public Guid UserId { get; set; }
        public Guid InitiatingUserId { get; set; }
        public Guid BusinessUnitId { get; set; }
        public Guid OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public Guid CorrelationId { get; set; }
        public Guid? OutputId { get; set; }

        public TargetKind TargetKind { get; set; }
        public Entity TargetEntity { get; set; }
        public EntityReference TargetReference { get; set; }

        public List<ImportedImage> Images { get; } = new List<ImportedImage>();
        public List<ImportedNamedValue> SharedVariables { get; } = new List<ImportedNamedValue>();
        public List<ImportedNamedValue> InputParameters { get; } = new List<ImportedNamedValue>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Parses a serialized <c>IExecutionContext</c> in the platform's DataContract-JSON shape
    /// (requirements §4.11 / FR-11) — the JSON emitted by the Plugin Registration Tool profiler and
    /// plugin trace logs. Values are self-describing via their <c>__type</c> discriminator, so no
    /// table metadata is needed to resolve type ambiguity.
    ///
    /// Only fundamentally malformed JSON throws; anything the harness can't represent is reported on
    /// <see cref="ImportedContext.Warnings"/> rather than aborting the whole paste (FR-11.8).
    /// </summary>
    public static class ExecutionContextImporter
    {
        private static readonly Regex WcfDate = new Regex(@"^/Date\((-?\d+)([+-]\d{4})?\)/$", RegexOptions.Compiled);

        public static ImportedContext Import(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("Nothing to import — paste a serialized execution context first.");
            }

            JObject root;
            try
            {
                // Keep dates as strings so we (not Newtonsoft) decide how /Date()/ literals are parsed.
                root = JsonConvert.DeserializeObject<JObject>(json,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            }
            catch (JsonException ex)
            {
                throw new FormatException("The pasted text is not valid JSON: " + ex.Message);
            }

            if (root == null)
            {
                throw new FormatException("The pasted JSON did not contain an execution-context object.");
            }

            var ctx = new ImportedContext
            {
                MessageName = (string)root["MessageName"],
                Stage = GetInt(root["Stage"], 0),
                Mode = GetInt(root["Mode"], 0),
                Depth = GetInt(root["Depth"], 1),
                PrimaryEntityName = (string)root["PrimaryEntityName"],
                PrimaryEntityId = GetGuid(root["PrimaryEntityId"]),
                UserId = GetGuid(root["UserId"]),
                InitiatingUserId = GetGuid(root["InitiatingUserId"]),
                BusinessUnitId = GetGuid(root["BusinessUnitId"]),
                OrganizationId = GetGuid(root["OrganizationId"]),
                OrganizationName = (string)root["OrganizationName"],
                CorrelationId = GetGuid(root["CorrelationId"])
            };

            ParseInputParameters(root["InputParameters"] as JArray, ctx);
            ParseImages(root["PreEntityImages"] as JArray, isPre: true, ctx: ctx);
            ParseImages(root["PostEntityImages"] as JArray, isPre: false, ctx: ctx);
            ParseSharedVariables(root["SharedVariables"] as JArray, ctx);
            ParseOutputId(root["OutputParameters"] as JArray, ctx);

            return ctx;
        }

        // ---- collections -------------------------------------------------------------------

        private static void ParseInputParameters(JArray array, ImportedContext ctx)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array)
            {
                var key = (string)item["key"];
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (string.Equals(key, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ParseValue(item["value"], "InputParameters[\"Target\"]", ctx.Warnings);
                    if (target is Entity entity)
                    {
                        ctx.TargetKind = TargetKind.Entity;
                        ctx.TargetEntity = entity;
                        if (ctx.PrimaryEntityId == Guid.Empty)
                        {
                            ctx.PrimaryEntityId = entity.Id;
                        }
                        if (string.IsNullOrEmpty(ctx.PrimaryEntityName))
                        {
                            ctx.PrimaryEntityName = entity.LogicalName;
                        }
                    }
                    else if (target is EntityReference reference)
                    {
                        ctx.TargetKind = TargetKind.EntityReference;
                        ctx.TargetReference = reference;
                        if (ctx.PrimaryEntityId == Guid.Empty)
                        {
                            ctx.PrimaryEntityId = reference.Id;
                        }
                        if (string.IsNullOrEmpty(ctx.PrimaryEntityName))
                        {
                            ctx.PrimaryEntityName = reference.LogicalName;
                        }
                    }
                    else
                    {
                        ctx.Warnings.Add("InputParameters[\"Target\"] was not an Entity or EntityReference and was skipped.");
                    }
                    continue;
                }

                var value = ParseValue(item["value"], $"InputParameters[\"{key}\"]", ctx.Warnings);
                ctx.InputParameters.Add(new ImportedNamedValue { Key = key, Value = value });
            }
        }

        private static void ParseImages(JArray array, bool isPre, ImportedContext ctx)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array)
            {
                var key = (string)item["key"];
                var label = $"{(isPre ? "PreEntityImages" : "PostEntityImages")}[\"{key}\"]";
                if (ParseValue(item["value"], label, ctx.Warnings) is Entity entity)
                {
                    ctx.Images.Add(new ImportedImage { IsPreImage = isPre, Key = key, Entity = entity });
                }
                else if (!string.IsNullOrEmpty(key))
                {
                    ctx.Warnings.Add($"{label} was not an Entity and was skipped.");
                }
            }
        }

        private static void ParseSharedVariables(JArray array, ImportedContext ctx)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array)
            {
                var key = (string)item["key"];
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var value = ParseValue(item["value"], $"SharedVariables[\"{key}\"]", ctx.Warnings);
                ctx.SharedVariables.Add(new ImportedNamedValue { Key = key, Value = value });
            }
        }

        private static void ParseOutputId(JArray array, ImportedContext ctx)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array)
            {
                if (string.Equals((string)item["key"], "id", StringComparison.OrdinalIgnoreCase))
                {
                    var id = GetGuid(item["value"]);
                    if (id != Guid.Empty)
                    {
                        ctx.OutputId = id;
                    }
                    return;
                }
            }
        }

        // ---- value dispatch ----------------------------------------------------------------

        /// <summary>Turns one JSON value (typed via <c>__type</c>, or a bare scalar) into an SDK/CLR object.</summary>
        private static object ParseValue(JToken token, string context, List<string> warnings)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var typeTag = (string)obj["__type"];
                if (string.IsNullOrEmpty(typeTag))
                {
                    warnings.Add($"{context}: object without a __type discriminator was skipped.");
                    return null;
                }
                return ParseTyped(typeTag, obj, context, warnings);
            }

            if (token.Type == JTokenType.Array)
            {
                // e.g. a SharedVariable whose value is itself a collection — not representable here.
                warnings.Add($"{context}: array/collection value is not supported and was skipped.");
                return null;
            }

            return ParseScalar((JValue)token);
        }

        private static object ParseTyped(string typeTag, JObject obj, string context, List<string> warnings)
        {
            var kind = typeTag.Split(':')[0];
            switch (kind)
            {
                case "Entity":
                    return BuildEntity(obj, warnings);
                case "EntityReference":
                    return new EntityReference((string)obj["LogicalName"], GetGuid(obj["Id"]))
                    {
                        Name = (string)obj["Name"]
                    };
                case "OptionSetValue":
                    return new OptionSetValue(GetInt(obj["Value"], 0));
                case "Money":
                    return new Money(GetDecimal(obj["Value"]));
                case "OptionSetValueCollection":
                {
                    var list = new OptionSetValueCollection();
                    if (obj["Value"] is JArray values)
                    {
                        foreach (var v in values)
                        {
                            list.Add(new OptionSetValue(GetInt(v["Value"] ?? v, 0)));
                        }
                    }
                    return list;
                }
                case "EntityCollection":
                {
                    var collection = new EntityCollection();
                    if (obj["Entities"] is JArray entities)
                    {
                        foreach (var e in entities)
                        {
                            if (e is JObject eo && BuildEntity(eo, warnings) is Entity ent)
                            {
                                collection.Entities.Add(ent);
                            }
                        }
                    }
                    return collection;
                }
                case "AliasedValue":
                    return ParseValue(obj["Value"], context, warnings);
                default:
                    warnings.Add($"{context}: unknown value type '{kind}' was skipped.");
                    return null;
            }
        }

        private static Entity BuildEntity(JObject obj, List<string> warnings)
        {
            var entity = new Entity((string)obj["LogicalName"]);
            var id = GetGuid(obj["Id"]);
            if (id != Guid.Empty)
            {
                entity.Id = id;
            }

            if (obj["Attributes"] is JArray attributes)
            {
                foreach (var attribute in attributes)
                {
                    var key = (string)attribute["key"];
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    var value = ParseValue(attribute["value"], $"attribute '{key}'", warnings);
                    if (value != null)
                    {
                        entity[key] = value;
                    }
                }
            }

            // FormattedValues, KeyAttributes, RelatedEntities, EntityState, RowVersion are ignored (FR-11.5).
            return entity;
        }

        /// <summary>
        /// Maps a bare JSON scalar to a CLR value. A string may be a WCF <c>/Date()/</c> literal or an
        /// exact Guid (uniqueidentifier attributes serialize as bare guid strings, with no <c>__type</c>).
        /// </summary>
        private static object ParseScalar(JValue value)
        {
            switch (value.Type)
            {
                case JTokenType.Boolean:
                    return (bool)value.Value;
                case JTokenType.Integer:
                {
                    var l = Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
                    return l >= int.MinValue && l <= int.MaxValue ? (int)l : (object)l;
                }
                case JTokenType.Float:
                    return Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);
                case JTokenType.String:
                    return ParseStringScalar((string)value.Value);
                default:
                    return value.Value;
            }
        }

        private static object ParseStringScalar(string text)
        {
            if (text == null)
            {
                return null;
            }

            var date = WcfDate.Match(text);
            if (date.Success)
            {
                var epochMs = long.Parse(date.Groups[1].Value, CultureInfo.InvariantCulture);
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(epochMs);
            }

            // Uniqueidentifier attributes come across as a bare guid string with no __type tag.
            if (Guid.TryParseExact(text, "D", out var guid))
            {
                return guid;
            }

            return text;
        }

        // ---- scalar helpers ----------------------------------------------------------------

        private static int GetInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            try { return Convert.ToInt32(((JValue)token).Value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static decimal GetDecimal(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0m;
            }
            try { return Convert.ToDecimal(((JValue)token).Value, CultureInfo.InvariantCulture); }
            catch { return 0m; }
        }

        private static Guid GetGuid(JToken token)
        {
            var text = token?.Type == JTokenType.String ? (string)token : token?.ToString();
            return Guid.TryParse(text, out var guid) ? guid : Guid.Empty;
        }
    }
}
